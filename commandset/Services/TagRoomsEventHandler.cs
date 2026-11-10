using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    /// <summary>
    /// Failure preprocessor that handles duplicate room number warnings during tagging.
    /// This suppresses warnings that might occur if the model has rooms with conflicting numbers.
    /// </summary>
    public class TagRoomFailurePreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            IList<FailureMessageAccessor> failures = failuresAccessor.GetFailureMessages();

            foreach (FailureMessageAccessor failure in failures)
            {
                string description = failure.GetDescriptionText();
                if (description.Contains("Number") ||
                    description.Contains("number") ||
                    description.Contains("duplicate") ||
                    description.Contains("Duplicate"))
                {
                    failuresAccessor.DeleteWarning(failure);
                }
            }

            return FailureProcessingResult.Continue;
        }
    }

    /// <summary>
    /// Creates room tags. Targets, in order of precedence:
    ///  - allLevels: the main floor plan of every level that has placed rooms (active view is not changed)
    ///  - roomIds: the active plan if it is on those rooms' level, otherwise that level's main floor plan
    ///  - default: the active floor/ceiling plan; if the active view is not a plan, the main floor plan
    ///    of the lowest level with rooms (and that view is activated)
    /// Rooms that already have a tag in the target view are skipped, so re-running is safe.
    /// </summary>
    public class TagRoomsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private UIApplication _uiApp;
        private UIDocument _uiDoc => _uiApp.ActiveUIDocument;
        private Document _doc => _uiDoc.Document;

        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public object TaggingResults { get; private set; }

        private bool _useLeader;
        private string _tagTypeId;
        private List<long> _roomIds;
        private bool _allLevels;

        public void SetParameters(bool useLeader, string tagTypeId, List<long> roomIds = null, bool allLevels = false)
        {
            _useLeader = useLeader;
            _tagTypeId = tagTypeId;
            _roomIds = roomIds;
            _allLevels = allLevels;
            _resetEvent.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            _uiApp = uiapp;
            string viewSwitchMessage = null;

            try
            {
                var placedRooms = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .Where(r => r.Area > 0)
                    .ToList();

                List<Room> requested = null;
                if (_roomIds != null && _roomIds.Count > 0)
                {
                    requested = _roomIds
                        .Select(id => _doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(id)) as Room)
                        .Where(r => r != null)
                        .ToList();
                    if (requested.Count == 0)
                        throw new InvalidOperationException("None of the given roomIds are rooms");
                }

                // Resolve target views
                var targets = new List<ViewPlan>();
                View activeView = _doc.ActiveView;
                var activePlan = activeView as ViewPlan;
                bool activeIsPlan = activePlan != null && !activePlan.IsTemplate && activePlan.GenLevel != null &&
                                    (activePlan.ViewType == ViewType.FloorPlan || activePlan.ViewType == ViewType.CeilingPlan);

                if (_allLevels)
                {
                    var levelIds = (requested ?? placedRooms).Select(r => r.LevelId).Distinct();
                    foreach (var levelId in levelIds)
                    {
                        var plan = (activeIsPlan && activePlan.GenLevel.Id == levelId) ? activePlan : FindMainFloorPlan(levelId);
                        if (plan != null) targets.Add(plan);
                    }
                    targets = targets.OrderBy(v => v.GenLevel.Elevation).ToList();
                }
                else
                {
                    ElementId wantedLevel = requested?.First().LevelId;
                    if (activeIsPlan && (wantedLevel == null || activePlan.GenLevel.Id == wantedLevel))
                    {
                        targets.Add(activePlan);
                    }
                    else
                    {
                        if (wantedLevel == null)
                        {
                            var lowest = placedRooms.OrderBy(r => r.Level?.Elevation ?? double.MaxValue).FirstOrDefault();
                            if (lowest == null) throw new InvalidOperationException("The model has no placed rooms");
                            wantedLevel = lowest.LevelId;
                        }
                        var plan = FindMainFloorPlan(wantedLevel);
                        if (plan == null)
                            throw new InvalidOperationException($"Active view '{activeView.Name}' is not a plan and no floor plan exists for level {_doc.GetElement(wantedLevel)?.Name}");
                        _uiDoc.ActiveView = plan;
                        viewSwitchMessage = $"Switched from '{activeView.Name}' to '{plan.Name}' for room tagging";
                        targets.Add(plan);
                    }
                }

                if (targets.Count == 0)
                    throw new InvalidOperationException("No floor plan views found to tag rooms in");

                var perView = new List<object>();
                var createdTags = new List<object>();
                var errors = new List<string>();
                int totalCreated = 0, totalSkipped = 0, totalRooms = 0;

                using (Transaction tran = new Transaction(_doc, "Tag Rooms"))
                {
                    FailureHandlingOptions failureOptions = tran.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(new TagRoomFailurePreprocessor());
                    failureOptions.SetClearAfterRollback(true);
                    failureOptions.SetDelayedMiniWarnings(false);
                    tran.SetFailureHandlingOptions(failureOptions);

                    tran.Start();

                    FamilySymbol roomTagType = FindRoomTagType(_doc);
                    if (roomTagType == null)
                        throw new InvalidOperationException("No room tag family type found in the project");
                    if (!roomTagType.IsActive)
                    {
                        roomTagType.Activate();
                        _doc.Regenerate();
                    }

                    foreach (var view in targets)
                    {
                        // Rooms to tag in this view
                        List<Room> rooms;
                        if (requested != null)
                            rooms = requested.Where(r => r.LevelId == view.GenLevel.Id).ToList();
                        else
                            rooms = new FilteredElementCollector(_doc, view.Id)
                                .OfCategory(BuiltInCategory.OST_Rooms)
                                .WhereElementIsNotElementType()
                                .Cast<Room>()
                                .ToList();

                        var alreadyTagged = new HashSet<long>(new FilteredElementCollector(_doc, view.Id)
                            .OfCategory(BuiltInCategory.OST_RoomTags)
                            .WhereElementIsNotElementType()
                            .Cast<RoomTag>()
                            .Where(t => t.Room != null)
                            .Select(t => t.Room.Id.GetValue()));

                        int created = 0, skipped = 0;
                        foreach (var room in rooms.OrderBy(r => r.Number, StringComparer.OrdinalIgnoreCase))
                        {
                            if (room.Area <= 0 || alreadyTagged.Contains(room.Id.GetValue())) { skipped++; continue; }
                            try
                            {
                                XYZ point = (room.Location as LocationPoint)?.Point;
                                if (point == null)
                                {
                                    var bbox = room.get_BoundingBox(view);
                                    if (bbox == null) { skipped++; continue; }
                                    point = (bbox.Min + bbox.Max) / 2;
                                }

                                RoomTag tag = _doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(point.X, point.Y), view.Id);
                                if (tag == null) { skipped++; continue; }
                                if (tag.GetTypeId() != roomTagType.Id) tag.ChangeTypeId(roomTagType.Id);
                                if (_useLeader) tag.HasLeader = true;
                                created++;
                                createdTags.Add(new
                                {
                                    tagId = tag.Id.GetValue().ToString(),
                                    roomId = room.Id.GetValue().ToString(),
                                    roomNumber = room.Number,
                                    roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Room",
                                    view = view.Name
                                });
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"Room {room.Number} in '{view.Name}': {ex.Message}");
                            }
                        }

                        totalCreated += created;
                        totalSkipped += skipped;
                        totalRooms += rooms.Count;
                        perView.Add(new { viewId = view.Id.GetValue().ToString(), viewName = view.Name, level = view.GenLevel.Name, created, skippedAlreadyTagged = skipped });
                    }

                    tran.Commit();
                }

                string message = $"Created {totalCreated} room tags in {targets.Count} view(s)" +
                                 (totalSkipped > 0 ? $"; skipped {totalSkipped} rooms that were already tagged or unplaced" : "");
                if (viewSwitchMessage != null) message = viewSwitchMessage + "\n" + message;

                TaggingResults = new
                {
                    success = true,
                    totalRooms,
                    taggedRooms = totalCreated,
                    skippedCount = totalSkipped,
                    views = perView,
                    tags = createdTags.Count <= 40 ? createdTags : null,
                    errors = errors.Count > 0 ? errors : null,
                    viewSwitched = viewSwitchMessage != null,
                    message
                };
            }
            catch (Exception ex)
            {
                TaggingResults = new
                {
                    success = false,
                    message = $"Error occurred: {ex.Message}"
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        /// <summary>
        /// The "main" floor plan of a level: a non-template floor plan on that level, preferring the one
        /// named exactly like the level, then one placed on a sheet, then the shortest name.
        /// </summary>
        private ViewPlan FindMainFloorPlan(ElementId levelId)
        {
            var level = _doc.GetElement(levelId) as Level;
            return new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .Where(v => v.ViewType == ViewType.FloorPlan && !v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == levelId)
                .OrderBy(v => level != null && v.Name == level.Name ? 0 : 1)
                .ThenBy(v => string.IsNullOrEmpty(v.get_Parameter(BuiltInParameter.VIEWPORT_SHEET_NUMBER)?.AsString()) ? 1 : 0)
                .ThenBy(v => v.Name.Length)
                .ThenBy(v => v.Id.GetValue())
                .FirstOrDefault();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName()
        {
            return "Tag Rooms";
        }

        private FamilySymbol FindRoomTagType(Document doc)
        {
            if (!string.IsNullOrEmpty(_tagTypeId) && long.TryParse(_tagTypeId, out long id))
            {
                Element element = doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(id));
                if (element is FamilySymbol symbol &&
                    symbol.Category != null &&
                    symbol.Category.Id.GetIntValue() == (int)BuiltInCategory.OST_RoomTags)
                {
                    return symbol;
                }
            }

            // Default: the room tag type used most in the model, else the first one (deterministic by id)
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .WhereElementIsElementType()
                .Where(e => e.Category != null && e.Category.Id.GetIntValue() == (int)BuiltInCategory.OST_RoomTags)
                .Cast<FamilySymbol>()
                .ToList();
            if (types.Count == 0) return null;

            var usage = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_RoomTags)
                .WhereElementIsNotElementType()
                .GroupBy(t => t.GetTypeId().GetValue())
                .ToDictionary(g => g.Key, g => g.Count());

            return types
                .OrderByDescending(t => usage.TryGetValue(t.Id.GetValue(), out int n) ? n : 0)
                .ThenBy(t => t.Name == "Room Tag" ? 0 : 1)
                .ThenBy(t => t.Id.GetValue())
                .First();
        }
    }
}
