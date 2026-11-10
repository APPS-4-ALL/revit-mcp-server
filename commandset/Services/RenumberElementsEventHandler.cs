using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class RenumberElementsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public List<long> ElementIds { get; set; } = new List<long>();
        public string TargetCategory { get; set; } = "";
        public string ParameterName { get; set; } = "";
        public int StartNumber { get; set; } = 1;
        public string Prefix { get; set; } = "";
        public string Suffix { get; set; } = "";
        public int Increment { get; set; } = 1;
        public string SortBy { get; set; } = "location";
        public bool DryRun { get; set; } = true;
        public bool PerLevel { get; set; } = false;
        public AIResult<object> Result { get; private set; }

        public void SetParameters(List<long> elementIds, string targetCategory, string parameterName, int startNumber, string prefix, string suffix, int increment, string sortBy, bool dryRun)
        {
            ElementIds = elementIds ?? new List<long>();
            TargetCategory = targetCategory ?? "";
            ParameterName = parameterName ?? "";
            StartNumber = startNumber;
            Prefix = prefix ?? "";
            Suffix = suffix ?? "";
            Increment = increment;
            SortBy = sortBy ?? "location";
            DryRun = dryRun;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;
                var elements = GetTargetElements(doc);

                if (elements.Count == 0)
                {
                    Result = new AIResult<object> { Success = false, Message = "No elements found to renumber" };
                    return;
                }

                // Sort elements and compute the new numbers
                var plan = BuildNumbering(doc, elements);

                var renumberResults = new List<object>();

                using (var transaction = DryRun ? null : new Transaction(doc, "Renumber Elements"))
                {
                    transaction?.Start();

                    foreach (var (elem, newValue) in plan)
                    {
                        string oldValue = GetCurrentNumber(elem);
                        bool success = true;
                        string message = "";

                        if (newValue == null)
                        {
                            renumberResults.Add(new
                            {
#if REVIT2024_OR_GREATER
                                id = elem.Id.Value,
#else
                                id = elem.Id.IntegerValue,
#endif
                                oldValue,
                                newValue = oldValue,
                                success = false,
                                message = "No adjacent room found - left unchanged"
                            });
                            continue;
                        }

                        if (!DryRun)
                        {
                            try
                            {
                                SetElementNumber(elem, newValue);
                            }
                            catch (Exception ex)
                            {
                                success = false;
                                message = ex.Message;
                            }
                        }

                        renumberResults.Add(new
                        {
#if REVIT2024_OR_GREATER
                            id = elem.Id.Value,
#else
                            id = elem.Id.IntegerValue,
#endif
                            oldValue,
                            newValue,
                            success,
                            message
                        });
                    }

                    transaction?.Commit();
                }

                int successCount = renumberResults.Count;
                Result = new AIResult<object>
                {
                    Success = true,
                    Message = DryRun
                        ? $"Preview: {successCount} elements would be renumbered (dry run)"
                        : $"Renumbered {successCount} elements",
                    Response = new { dryRun = DryRun, sortBy = SortBy, perLevel = PerLevel, totalProcessed = renumberResults.Count, renames = renumberResults }
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<object> { Success = false, Message = $"Renumber failed: {ex.Message}" };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private List<Element> GetTargetElements(Document doc)
        {
            if (ElementIds.Count > 0)
            {
                return ElementIds
                    .Select(id => doc.GetElement(ToElementId(id)))
                    .Where(e => e != null)
                    .ToList();
            }

            switch (TargetCategory)
            {
                case "Rooms":
                    // Unplaced / not enclosed rooms are skipped
                    return new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType()
                        .Where(e => !(e is Room r) || r.Area > 0)
                        .ToList();
                case "Doors":
                    return new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Doors)
                        .WhereElementIsNotElementType()
                        .ToList();
                case "Windows":
                    return new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Windows)
                        .WhereElementIsNotElementType()
                        .ToList();
                case "Parking":
                    return new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Parking)
                        .WhereElementIsNotElementType()
                        .ToList();
                default:
                    return new List<Element>();
            }
        }

        /// <summary>
        /// Orders the elements and assigns each its new number.
        /// perLevel: numbering restarts on every level as {prefix}{levelNumber}{seq:00}{suffix}
        /// (levelNumber = leading digits of the level name, else the level's 1-based order by elevation).
        /// clockwise: within each level, elements are swept clockwise from 12 o'clock around the center
        /// of that level's elements.
        /// </summary>
        private List<(Element elem, string value)> BuildNumbering(Document doc, List<Element> elements)
        {
            var result = new List<(Element, string)>();
            bool clockwise = SortBy == "clockwise";

            if (SortBy == "room")
                return BuildRoomNumbering(elements);

            if (!PerLevel && !clockwise)
            {
                int n = StartNumber;
                foreach (var e in SortElements(elements))
                {
                    result.Add((e, $"{Prefix}{n}{Suffix}"));
                    n += Increment;
                }
                return result;
            }

            var groups = elements
                .GroupBy(e => GetLevel(doc, e)?.Id.GetValue() ?? long.MinValue)
                .Select(g => (level: g.Key == long.MinValue ? null : doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(g.Key)) as Level, items: g.ToList()))
                .OrderBy(g => g.level == null ? double.MaxValue : g.level.Elevation)
                .ToList();

            int ordinal = 0;
            int running = StartNumber;
            foreach (var g in groups)
            {
                ordinal++;
                var ordered = clockwise ? SortClockwise(g.items) : SortElements(g.items);

                if (!PerLevel)
                {
                    foreach (var e in ordered) { result.Add((e, $"{Prefix}{running}{Suffix}")); running += Increment; }
                    continue;
                }

                string levelNo = LevelNumber(g.level, ordinal);
                int last = StartNumber + Increment * (ordered.Count - 1);
                int width = Math.Max(2, Math.Abs(last).ToString().Length);
                int seq = StartNumber;
                foreach (var e in ordered)
                {
                    result.Add((e, $"{Prefix}{levelNo}{seq.ToString().PadLeft(width, '0')}{Suffix}"));
                    seq += Increment;
                }
            }
            return result;
        }

        // Rooms that are passed through rather than served by a door
        private static readonly string[] CirculationKeywords =
            { "corridor", "lobby", "hall", "hallway", "vestibule", "vest", "stair", "stairs", "elevator", "lift", "circulation", "foyer", "passage" };

        private static bool IsCirculation(Room room)
        {
            if (room == null) return false;
            var tokens = System.Text.RegularExpressions.Regex.Split((room.Name ?? "").ToLowerInvariant(), "[^a-z0-9]+");
            return tokens.Any(t => CirculationKeywords.Contains(t));
        }

        /// <summary>
        /// The room a door/window serves: if exactly one side is circulation (corridor, lobby, stair...),
        /// the other side; otherwise the To Room (the room the door swings into), else the From Room.
        /// </summary>
        private static Room ServedRoom(Element e)
        {
            if (!(e is FamilyInstance fi)) return null;
            Room to = fi.ToRoom, from = fi.FromRoom;
            if (to != null && from != null && IsCirculation(to) && !IsCirculation(from)) return from;
            return to ?? from;
        }

        private static string Letters(int index)
        {
            // 0 -> A, 25 -> Z, 26 -> AA
            string s = "";
            index++;
            while (index > 0) { index--; s = (char)('A' + index % 26) + s; index /= 26; }
            return s;
        }

        /// <summary>
        /// sortBy=room: each element gets {prefix}{room number}{letter}{suffix}. Letters run clockwise
        /// (from north) around the room's location point. Elements with no adjacent room get null (unchanged).
        /// </summary>
        private List<(Element elem, string value)> BuildRoomNumbering(List<Element> elements)
        {
            var result = new List<(Element, string)>();
            var byRoom = elements
                .Select(e => (e, room: ServedRoom(e)))
                .GroupBy(x => x.room?.Id.GetValue() ?? long.MinValue)
                .ToList();

            foreach (var g in byRoom
                .Where(g => g.Key != long.MinValue)
                .OrderBy(g => g.First().room.Level?.Elevation ?? 0)
                .ThenBy(g => g.First().room.Number, StringComparer.OrdinalIgnoreCase))
            {
                var room = g.First().room;
                var center = (room.Location as LocationPoint)?.Point;
                if (center == null)
                {
                    var bb = room.get_BoundingBox(null);
                    center = bb != null ? (bb.Min + bb.Max) / 2 : XYZ.Zero;
                }

                var ordered = g
                    .Select(x =>
                    {
                        var p = GetPoint(x.e) ?? center;
                        double dx = p.X - center.X, dy = p.Y - center.Y;
                        double angle = Math.Atan2(dx, dy) * 180.0 / Math.PI;
                        if (angle < 0) angle += 360.0;
                        return (x.e, angle: Math.Round(angle, 3));
                    })
                    .OrderBy(x => x.angle)
                    .ThenBy(x => x.e.Id.GetValue())
                    .ToList();

                for (int i = 0; i < ordered.Count; i++)
                    result.Add((ordered[i].e, $"{Prefix}{room.Number}{Letters(i)}{Suffix}"));
            }

            foreach (var x in byRoom.Where(g => g.Key == long.MinValue).SelectMany(g => g).OrderBy(x => x.e.Id.GetValue()))
                result.Add((x.e, null));

            return result;
        }

        private static string LevelNumber(Level level, int ordinal)
        {
            if (level != null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(level.Name ?? "", @"^\s*(?:L|Level\s*)?0*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value;
            }
            return ordinal.ToString();
        }

        private static Level GetLevel(Document doc, Element e)
        {
            var id = e.LevelId;
            if (id == null || id == ElementId.InvalidElementId)
            {
                var p = e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ?? e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
                id = p?.AsElementId();
            }
            return id != null && id != ElementId.InvalidElementId ? doc.GetElement(id) as Level : null;
        }

        private static XYZ GetPoint(Element e)
        {
            if (e.Location is LocationPoint lp) return lp.Point;
            if (e.Location is LocationCurve lc) return lc.Curve.Evaluate(0.5, true);
            var bb = e.get_BoundingBox(null);
            return bb != null ? (bb.Min + bb.Max) / 2 : null;
        }

        private static List<Element> SortClockwise(List<Element> elements)
        {
            var withPoints = elements.Select(e => (e, p: GetPoint(e))).ToList();
            var located = withPoints.Where(x => x.p != null).ToList();
            if (located.Count == 0) return elements.OrderBy(e => e.Id.GetValue()).ToList();

            double cx = (located.Min(x => x.p.X) + located.Max(x => x.p.X)) / 2;
            double cy = (located.Min(x => x.p.Y) + located.Max(x => x.p.Y)) / 2;

            var ordered = located
                .Select(x =>
                {
                    double dx = x.p.X - cx, dy = x.p.Y - cy;
                    double angle = Math.Atan2(dx, dy) * 180.0 / Math.PI; // 0 = north, increasing clockwise
                    if (angle < 0) angle += 360.0;
                    return (x.e, angle: Math.Round(angle, 3), dist: Math.Sqrt(dx * dx + dy * dy));
                })
                .OrderBy(x => x.angle)
                .ThenBy(x => x.dist)
                .ThenBy(x => x.e.Id.GetValue())
                .Select(x => x.e)
                .ToList();

            ordered.AddRange(withPoints.Where(x => x.p == null).Select(x => x.e).OrderBy(e => e.Id.GetValue()));
            return ordered;
        }

        private List<Element> SortElements(List<Element> elements)
        {
            if (SortBy == "location")
            {
                return elements.OrderBy(e =>
                {
                    if (e.Location is LocationPoint lp) return lp.Point.X + lp.Point.Y * 10000;
                    if (e.Location is LocationCurve lc) return lc.Curve.GetEndPoint(0).X + lc.Curve.GetEndPoint(0).Y * 10000;
                    return 0.0;
                }).ToList();
            }

            if (SortBy == "name")
            {
                return elements.OrderBy(e => e.Name).ToList();
            }

            return elements;
        }

        private string GetCurrentNumber(Element elem)
        {
            if (!string.IsNullOrEmpty(ParameterName))
            {
                var param = elem.LookupParameter(ParameterName);
                return param?.AsString() ?? param?.AsValueString() ?? "";
            }

            if (elem is Room room) return room.Number;
            var numberParam = elem.get_Parameter(BuiltInParameter.DOOR_NUMBER)
                              ?? elem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            return numberParam?.AsString() ?? "";
        }

        private void SetElementNumber(Element elem, string value)
        {
            if (!string.IsNullOrEmpty(ParameterName))
            {
                var param = elem.LookupParameter(ParameterName);
                if (param != null && !param.IsReadOnly) { param.Set(value); return; }
            }

            if (elem is Room room) { room.Number = value; return; }

            var numberParam = elem.get_Parameter(BuiltInParameter.DOOR_NUMBER)
                              ?? elem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            if (numberParam != null && !numberParam.IsReadOnly)
                numberParam.Set(value);
            else
                throw new Exception($"Cannot find writable number parameter on element {elem.Id}");
        }

        private ElementId ToElementId(long id)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(id);
#else
            return new ElementId((int)id);
#endif
        }

        public string GetName() => "Renumber Elements";
    }
}
