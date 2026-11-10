using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace RevitMCPCommandSet.Services.ViewManagement
{
    /// <summary>
    /// Applies a native Revit Color Fill Scheme (not per-element overrides) to a view for
    /// Rooms / Areas / Spaces, colors every value with a deterministic palette, and places a
    /// real Color Fill Legend next to the plan. Re-running is idempotent: the scheme is reused
    /// and the legend for that category in the view is replaced.
    /// </summary>
    public class ApplyColorFillSchemeEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        public string CategoryName { get; set; } = "Rooms";
        public string ParameterName { get; set; } = "";
        public string SchemeName { get; set; } = "";
        public long ViewId { get; set; } = 0;
        public bool PlaceLegend { get; set; } = true;
        public string LegendPosition { get; set; } = "right";
        public bool HideFloors { get; set; } = true;
        public bool ZoomToFit { get; set; } = true;
        public List<ColorMapping> CustomColors { get; set; } = new List<ColorMapping>();

        public AIResult<object> Result { get; private set; }
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetParameters() { TaskCompleted = false; _resetEvent.Reset(); }
        public bool WaitForCompletion(int timeoutMilliseconds = 60000) { return _resetEvent.WaitOne(timeoutMilliseconds); }

        public class ColorMapping
        {
            public string Value { get; set; }
            public int R { get; set; }
            public int G { get; set; }
            public int B { get; set; }
        }

        // Parameters tried, in order, when no parameterName is given ("color by use").
        private static readonly string[] UseParameterCandidates = { "Department", "Occupancy", "Name" };

        public void Execute(UIApplication app)
        {
            try
            {
                var uidoc = app.ActiveUIDocument;
                var doc = uidoc.Document;

                View view = ViewId != 0 ? doc.GetElement(ToId(ViewId)) as View : uidoc.ActiveView;
                if (view == null)
                    throw new InvalidOperationException($"View {ViewId} not found");
                if (view.IsTemplate)
                    throw new InvalidOperationException($"'{view.Name}' is a view template");

                var bic = ResolveCategory(CategoryName);
                var categoryId = new ElementId(bic);

                // Elements of the category in the whole model (for scheme entries) and in this view (for legend placement)
                var modelElements = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType()
                    .Where(IsPlaced).ToList();
                var viewElements = new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType()
                    .Where(IsPlaced).ToList();
                if (viewElements.Count == 0)
                    throw new InvalidOperationException($"No placed {CategoryName} visible in view '{view.Name}'. Activate a floor plan that shows them.");

                // Resolve parameter: explicit name, or the first populated "use" parameter
                string paramName = ParameterName;
                var autoNotes = new List<string>();
                if (string.IsNullOrWhiteSpace(paramName))
                {
                    foreach (var candidate in UseParameterCandidates)
                    {
                        int populated = modelElements.Count(e => !string.IsNullOrWhiteSpace(e.LookupParameter(candidate)?.AsString()));
                        if (populated > 0) { paramName = candidate; break; }
                        autoNotes.Add($"'{candidate}' is empty on all {CategoryName}");
                    }
                    if (string.IsNullOrWhiteSpace(paramName))
                        throw new InvalidOperationException("No populated use parameter found (tried " + string.Join(", ", UseParameterCandidates) + ")");
                }

                var sample = viewElements.Select(e => e.LookupParameter(paramName)).FirstOrDefault(p => p != null);
                if (sample == null)
                    throw new InvalidOperationException($"Parameter '{paramName}' not found on {CategoryName}");
                if (sample.StorageType != StorageType.String)
                    throw new InvalidOperationException($"Parameter '{paramName}' is {sample.StorageType}; only text parameters are supported");
                var paramId = sample.Id;

                var values = modelElements
                    .Select(e => e.LookupParameter(paramName)?.AsString())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var colors = AssignColors(values, CustomColors);

                string schemeName = string.IsNullOrWhiteSpace(SchemeName) ? DefaultSchemeName(bic, paramName) : SchemeName.Trim();

                var solidId = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                    .Where(f => f.GetFillPattern().IsSolidFill)
                    .OrderBy(f => f.GetFillPattern().Target == FillPatternTarget.Drafting ? 0 : 1)
                    .Select(f => f.Id).FirstOrDefault() ?? ElementId.InvalidElementId;

                var warnings = new List<string>();
                ColorFillScheme scheme;
                long legendId = 0;
                bool schemeCreated = false;
                int floorsHidden = 0;

                using (var t = new Transaction(doc, "Apply Color Fill Scheme"))
                {
                    t.Start();

                    var schemes = new FilteredElementCollector(doc).OfClass(typeof(ColorFillScheme)).Cast<ColorFillScheme>()
                        .Where(s => s.CategoryId == categoryId).ToList();
                    scheme = schemes.FirstOrDefault(s => s.Name == schemeName);
                    if (scheme == null)
                    {
                        var source = schemes.FirstOrDefault(s => s.ParameterDefinition == paramId && !s.IsByRange)
                                     ?? schemes.FirstOrDefault(s => !s.IsByRange)
                                     ?? schemes.FirstOrDefault();
                        if (source == null)
                            throw new InvalidOperationException($"The model has no color scheme for {CategoryName} to start from");
                        scheme = (ColorFillScheme)doc.GetElement(source.Duplicate(schemeName));
                        schemeCreated = true;
                    }

                    if (scheme.ParameterDefinition != paramId)
                    {
                        if (!scheme.IsValidParameterDefinitionId(paramId))
                            throw new InvalidOperationException($"'{paramName}' cannot drive a color scheme for {CategoryName}");
                        scheme.ParameterDefinition = paramId;
                    }
                    if (scheme.IsByRange)
                        throw new InvalidOperationException($"Scheme '{schemeName}' is defined by range; pass a different schemeName");
                    scheme.Title = schemeName;

                    // Update existing entries, add missing ones
                    var existing = scheme.GetEntries().ToDictionary(e => e.GetStringValue() ?? "", e => e, StringComparer.Ordinal);
                    foreach (var value in values)
                    {
                        var c = colors[value];
                        var color = new Color((byte)c.R, (byte)c.G, (byte)c.B);
                        if (existing.TryGetValue(value, out var entry))
                        {
                            // UpdateEntry throws if nothing changed, so only touch entries that differ (keeps re-runs idempotent)
                            bool sameColor = entry.Color.IsValid && entry.Color.Red == color.Red && entry.Color.Green == color.Green && entry.Color.Blue == color.Blue;
                            bool samePattern = solidId == ElementId.InvalidElementId || entry.FillPatternId == solidId;
                            if (sameColor && samePattern && entry.IsVisible) continue;
                            entry.Color = color;
                            if (solidId != ElementId.InvalidElementId) entry.FillPatternId = solidId;
                            entry.IsVisible = true;
                            scheme.UpdateEntry(entry);
                        }
                        else
                        {
                            var added = new ColorFillSchemeEntry(StorageType.String) { Color = color };
                            if (solidId != ElementId.InvalidElementId) added.FillPatternId = solidId;
                            added.SetStringValue(value);
                            scheme.AddEntry(added);
                        }
                    }
                    scheme.SortEntries();

                    try
                    {
                        view.SetColorFillSchemeId(categoryId, scheme.Id);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Cannot apply a color scheme to '{view.Name}' ({view.ViewType}): {ex.Message}. If a view template controls Color Scheme, remove it first.");
                    }

                    if (HideFloors)
                    {
                        var floorCat = new ElementId(BuiltInCategory.OST_Floors);
                        if (view.CanCategoryBeHidden(floorCat))
                        {
                            if (!view.GetCategoryHidden(floorCat)) { view.SetCategoryHidden(floorCat, true); floorsHidden = 1; }
                        }
                        else warnings.Add("Floors could not be hidden (controlled by a view template)");
                    }

                    if (PlaceLegend)
                    {
                        // Replace any legend of this category already in the view
                        var oldLegends = new FilteredElementCollector(doc, view.Id).OfClass(typeof(ColorFillLegend)).Cast<ColorFillLegend>()
                            .Where(l => l.ColorFillCategoryId == categoryId).Select(l => l.Id).ToList();
                        if (oldLegends.Count > 0) doc.Delete(oldLegends);

                        var extents = GetExtents(viewElements, view);
                        var legend = ColorFillLegend.Create(doc, view.Id, categoryId, extents.Max);
                        doc.Regenerate();
                        var lb = legend.get_BoundingBox(view);
                        if (lb != null)
                        {
                            double gap = 8.0 / 304.8 * Math.Max(1, view.Scale); // 8 mm on paper
                            XYZ delta;
                            switch ((LegendPosition ?? "right").ToLowerInvariant())
                            {
                                case "left":  delta = new XYZ(extents.Min.X - gap - lb.Max.X, extents.Max.Y - lb.Max.Y, 0); break;
                                case "below": delta = new XYZ(extents.Min.X - lb.Min.X, extents.Min.Y - gap - lb.Max.Y, 0); break;
                                case "above": delta = new XYZ(extents.Min.X - lb.Min.X, extents.Max.Y + gap - lb.Min.Y, 0); break;
                                default:      delta = new XYZ(extents.Max.X + gap - lb.Min.X, extents.Max.Y - lb.Max.Y, 0); break;
                            }
                            if (!delta.IsZeroLength()) ElementTransformUtils.MoveElement(doc, legend.Id, delta);
                        }
                        if (view.CropBoxActive)
                            warnings.Add("The view crop is active; widen the crop/annotation crop if the legend is cut off");
                        legendId = IdValue(legend.Id);
                    }

                    t.Commit();
                }

                bool zoomed = false;
                if (ZoomToFit)
                {
                    var uiView = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                    if (uiView != null) { uiView.ZoomToFit(); zoomed = true; }
                }

                var inView = new HashSet<string>(viewElements.Select(e => e.LookupParameter(paramName)?.AsString() ?? ""), StringComparer.Ordinal);
                Result = new AIResult<object>
                {
                    Success = true,
                    Message = $"Applied color scheme '{schemeName}' ({CategoryName} by {paramName}, {values.Count} values) to '{view.Name}'" +
                              (legendId != 0 ? " and placed a color fill legend" : ""),
                    Response = new
                    {
                        viewId = IdValue(view.Id),
                        viewName = view.Name,
                        schemeId = IdValue(scheme.Id),
                        schemeName,
                        schemeCreated,
                        category = CategoryName,
                        parameterName = paramName,
                        parameterAutoSelected = string.IsNullOrWhiteSpace(ParameterName),
                        legendId,
                        floorsHidden = floorsHidden == 1,
                        zoomedToFit = zoomed,
                        valuesInView = values.Count(v => inView.Contains(v)),
                        colors = values.Select(v => new
                        {
                            value = v,
                            hex = $"#{colors[v].R:X2}{colors[v].G:X2}{colors[v].B:X2}",
                            inView = inView.Contains(v)
                        }),
                        notes = autoNotes,
                        warnings
                    }
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<object> { Success = false, Message = $"Apply color fill scheme failed: {ex.Message}" };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        // -----------------------------------------------------------------------
        // Palette: values are grouped by function keyword; each group has its own
        // muted hue family. Within a group, shades are handed out in sorted order,
        // so the same model always gets the same colors.
        // -----------------------------------------------------------------------

        private struct Rgb { public int R, G, B; public Rgb(int r, int g, int b) { R = r; G = g; B = b; } }

        private static readonly (string Group, string[] Keywords, Rgb[] Shades)[] Groups =
        {
            ("food", new[] { "cafeteria", "caferteria", "cafe", "dining", "kitchen", "prep", "dish", "pantry", "lounge", "break", "dry", "canteen" },
                new[] { new Rgb(168,214,140), new Rgb(130,190,120), new Rgb(190,220,170), new Rgb(205,230,150), new Rgb(150,200,150), new Rgb(180,210,120) }),
            ("wet", new[] { "toilet", "toilets", "wc", "men", "women", "restroom", "restrooms", "bath", "bathroom", "shower", "showers", "locker", "lockers", "janitor" },
                new[] { new Rgb(160,215,215), new Rgb(190,225,230), new Rgb(225,180,200), new Rgb(170,205,225), new Rgb(210,190,220) }),
            ("circulation", new[] { "corridor", "lobby", "hall", "hallway", "vest", "vestibule", "stair", "stairs", "elevator", "lift", "entry", "foyer", "circulation", "ramp" },
                new[] { new Rgb(228,226,218), new Rgb(238,232,214), new Rgb(220,214,200), new Rgb(200,200,200), new Rgb(210,205,195), new Rgb(232,228,222) }),
            ("meeting", new[] { "conference", "meeting", "board", "auditorium", "assembly", "multipurpose" },
                new[] { new Rgb(158,154,200), new Rgb(180,165,215), new Rgb(140,135,190) }),
            ("learning", new[] { "instruction", "classroom", "class", "lab", "laboratory", "library", "media", "drafting", "study", "lecture", "seminar", "training", "computer", "reading", "studio" },
                new[] { new Rgb(141,182,226), new Rgb(102,153,204), new Rgb(166,206,227), new Rgb(120,170,190), new Rgb(176,196,232), new Rgb(130,160,215), new Rgb(150,200,210) }),
            ("office", new[] { "office", "offices", "admin", "administration", "advisor", "advisors", "staff", "director", "reception", "copy", "print", "workroom", "work", "workspace" },
                new[] { new Rgb(253,205,135), new Rgb(252,180,110), new Rgb(247,220,160), new Rgb(240,190,150), new Rgb(230,170,120), new Rgb(215,190,160), new Rgb(245,200,120) }),
            ("service", new[] { "electrical", "elec", "mechanical", "mech", "sprinkler", "storage", "store", "data", "server", "it", "utility", "riser", "telecom", "pump", "boiler", "plant" },
                new[] { new Rgb(200,170,200), new Rgb(220,150,150), new Rgb(185,185,160), new Rgb(190,160,180), new Rgb(205,185,205), new Rgb(175,165,190) }),
        };

        private static readonly Rgb[] Fallback =
        {
            new Rgb(141,211,199), new Rgb(255,237,160), new Rgb(190,186,218), new Rgb(251,128,114), new Rgb(128,177,211), new Rgb(253,180,98),
            new Rgb(179,222,105), new Rgb(252,205,229), new Rgb(204,235,197), new Rgb(188,128,189), new Rgb(217,217,217), new Rgb(255,255,179)
        };

        private static Dictionary<string, Rgb> AssignColors(List<string> sortedValues, List<ColorMapping> custom)
        {
            var result = new Dictionary<string, Rgb>(StringComparer.Ordinal);
            var customMap = (custom ?? new List<ColorMapping>())
                .Where(c => !string.IsNullOrEmpty(c.Value))
                .GroupBy(c => c.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var groupCounters = new Dictionary<string, int>();
            int fallbackIndex = 0;

            foreach (var value in sortedValues)
            {
                if (customMap.TryGetValue(value, out var cm))
                {
                    result[value] = new Rgb(Clamp(cm.R), Clamp(cm.G), Clamp(cm.B));
                    continue;
                }
                var tokens = Regex.Split(value.ToLowerInvariant(), "[^a-z0-9]+").Where(s => s.Length > 0).ToList();
                var group = Groups.FirstOrDefault(g => tokens.Any(tok => g.Keywords.Contains(tok)));
                if (group.Group != null)
                {
                    groupCounters.TryGetValue(group.Group, out int i);
                    groupCounters[group.Group] = i + 1;
                    result[value] = Lighten(group.Shades[i % group.Shades.Length], 18 * (i / group.Shades.Length));
                }
                else
                {
                    result[value] = Lighten(Fallback[fallbackIndex % Fallback.Length], 18 * (fallbackIndex / Fallback.Length));
                    fallbackIndex++;
                }
            }
            return result;
        }

        private static Rgb Lighten(Rgb c, int amount) =>
            amount == 0 ? c : new Rgb(Clamp(c.R + amount), Clamp(c.G + amount), Clamp(c.B + amount));

        private static int Clamp(int v) => Math.Max(0, Math.Min(255, v));

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static BuiltInCategory ResolveCategory(string name)
        {
            switch ((name ?? "Rooms").Trim().ToLowerInvariant())
            {
                case "rooms": case "room": case "ost_rooms": return BuiltInCategory.OST_Rooms;
                case "areas": case "area": case "ost_areas": return BuiltInCategory.OST_Areas;
                case "spaces": case "space": case "ost_mepspaces": return BuiltInCategory.OST_MEPSpaces;
                default: throw new InvalidOperationException($"Unsupported category '{name}'. Use Rooms, Areas or Spaces.");
            }
        }

        private static string DefaultSchemeName(BuiltInCategory bic, string paramName)
        {
            string noun = bic == BuiltInCategory.OST_Areas ? "Area" : bic == BuiltInCategory.OST_MEPSpaces ? "Space" : "Room";
            bool isUse = UseParameterCandidates.Contains(paramName, StringComparer.OrdinalIgnoreCase);
            return isUse ? $"{noun} Use" : $"{noun} {paramName}";
        }

        private static bool IsPlaced(Element e)
        {
            if (e is SpatialElement se) return se.Area > 0;
            return true;
        }

        private static BoundingBoxXYZ GetExtents(List<Element> elements, View view)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var e in elements)
            {
                var b = e.get_BoundingBox(view);
                if (b == null) continue;
                minX = Math.Min(minX, b.Min.X); minY = Math.Min(minY, b.Min.Y);
                maxX = Math.Max(maxX, b.Max.X); maxY = Math.Max(maxY, b.Max.Y);
            }
            if (minX == double.MaxValue) throw new InvalidOperationException("Could not compute plan extents");
            return new BoundingBoxXYZ { Min = new XYZ(minX, minY, 0), Max = new XYZ(maxX, maxY, 0) };
        }

        private static ElementId ToId(long id)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(id);
#else
            return new ElementId((int)id);
#endif
        }

        private static long IdValue(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        public string GetName() => "Apply Color Fill Scheme";
    }
}
