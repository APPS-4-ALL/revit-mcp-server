using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.SheetManagement
{
    /// <summary>
    /// Creates sheets and places views on them, centered on the title block.
    /// - Views can be given by id or by name.
    /// - With no title block named, the smallest title block that fits the largest view is used.
    /// - A view already placed on another sheet is duplicated (with detailing) as "&lt;view&gt; - &lt;sheet number&gt;"
    ///   unless ifViewOnAnotherSheet = "skip".
    /// - The first created sheet is opened unless open = false.
    /// </summary>
    public class BatchCreateSheetsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private List<SheetDefinition> _sheets;
        private string _defaultTitleBlockName;
        private string _ifViewOnAnotherSheet = "duplicate";
        private bool _open = true;

        public AIResult<object> Result { get; private set; }
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetParameters(List<SheetDefinition> sheets, string defaultTitleBlockName, string ifViewOnAnotherSheet = "duplicate", bool open = true)
        {
            _sheets = sheets;
            _defaultTitleBlockName = defaultTitleBlockName;
            _ifViewOnAnotherSheet = string.IsNullOrWhiteSpace(ifViewOnAnotherSheet) ? "duplicate" : ifViewOnAnotherSheet.Trim().ToLowerInvariant();
            _open = open;
            TaskCompleted = false;
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
                var uidoc = app.ActiveUIDocument;
                var doc = uidoc.Document;
                var results = new List<object>();
                var warnings = new List<string>();
                int successCount = 0;
                ViewSheet firstSheet = null;

                // Resolve the views for every sheet up front (needed to pick a title block that fits)
                var sheetViews = new Dictionary<SheetDefinition, List<View>>();
                foreach (var def in _sheets)
                {
                    var views = new List<View>();
                    foreach (var id in def.ViewIds ?? new List<long>())
                    {
                        if (doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(id)) is View v) views.Add(v);
                        else warnings.Add($"Sheet {def.Number}: view id {id} not found");
                    }
                    foreach (var name in def.ViewNames ?? new List<string>())
                    {
                        var v = FindViewByName(doc, name);
                        if (v != null) views.Add(v);
                        else warnings.Add($"Sheet {def.Number}: no view named '{name}'");
                    }
                    sheetViews[def] = views.GroupBy(v => v.Id.GetValue()).Select(g => g.First()).ToList();
                }

                using (var transaction = new Transaction(doc, "Batch Create Sheets"))
                {
                    transaction.Start();
                    try
                    {
                        var titleBlockSizes = MeasureTitleBlocks(doc);
                        ElementId explicitDefault = FindTitleBlock(doc, _defaultTitleBlockName);

                        foreach (var sheetDef in _sheets)
                        {
                            try
                            {
                                var views = sheetViews[sheetDef];
                                ElementId titleBlockId =
                                    FindTitleBlock(doc, sheetDef.TitleBlockName)
                                    ?? explicitDefault
                                    ?? PickTitleBlock(titleBlockSizes, views)
                                    ?? ElementId.InvalidElementId;

                                var sheet = ViewSheet.Create(doc, titleBlockId);
                                sheet.SheetNumber = sheetDef.Number;
                                if (!string.IsNullOrEmpty(sheetDef.Name)) sheet.Name = sheetDef.Name;
                                doc.Regenerate();

                                var placedViews = PlaceViewsCentered(doc, sheet, views, warnings);

                                successCount++;
                                if (firstSheet == null) firstSheet = sheet;
                                var tb = doc.GetElement(titleBlockId) as FamilySymbol;
                                results.Add(new
                                {
                                    sheetId = sheet.Id.GetValue(),
                                    number = sheet.SheetNumber,
                                    name = sheet.Name,
                                    titleBlock = tb == null ? null : $"{tb.FamilyName}: {tb.Name}",
                                    success = true,
                                    viewsPlaced = placedViews
                                });
                            }
                            catch (Exception ex)
                            {
                                results.Add(new
                                {
                                    sheetId = (long)0,
                                    number = sheetDef.Number,
                                    name = sheetDef.Name,
                                    titleBlock = (string)null,
                                    success = false,
                                    viewsPlaced = (List<object>)null,
                                    message = ex.Message
                                });
                            }
                        }

                        transaction.Commit();
                    }
                    catch
                    {
                        if (transaction.GetStatus() == TransactionStatus.Started)
                            transaction.RollBack();
                        throw;
                    }
                }

                bool opened = false;
                if (_open && firstSheet != null)
                {
                    try { uidoc.ActiveView = firstSheet; opened = true; } catch { }
                }

                Result = new AIResult<object>
                {
                    Success = successCount > 0,
                    Message = $"Created {successCount}/{_sheets.Count} sheets" + (opened ? $"; opened {firstSheet.SheetNumber}" : ""),
                    Response = new { totalCreated = successCount, sheets = results, opened, warnings }
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<object> { Success = false, Message = $"Batch create sheets failed: {ex.Message}" };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        /// <summary>Places the views in a row centered on the sheet's title block.</summary>
        private List<object> PlaceViewsCentered(Document doc, ViewSheet sheet, List<View> views, List<string> warnings)
        {
            var placed = new List<object>();
            var viewports = new List<(Viewport vp, View view, string duplicatedFrom)>();

            foreach (var original in views)
            {
                var view = original;
                string duplicatedFrom = null;
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                {
                    string onSheet = view.get_Parameter(BuiltInParameter.VIEWPORT_SHEET_NUMBER)?.AsString();
                    bool alreadyPlaced = !string.IsNullOrEmpty(onSheet);
                    if (alreadyPlaced && _ifViewOnAnotherSheet == "duplicate" && view.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing))
                    {
                        view = doc.GetElement(view.Duplicate(ViewDuplicateOption.WithDetailing)) as View;
                        view.Name = UniqueViewName(doc, $"{original.Name} - {sheet.SheetNumber}");
                        // Keep the caption on the sheet identical to the original view's name
                        var titleOnSheet = view.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION);
                        if (titleOnSheet != null && !titleOnSheet.IsReadOnly && string.IsNullOrEmpty(titleOnSheet.AsString()))
                            titleOnSheet.Set(original.Name);
                        duplicatedFrom = original.Name;
                        warnings.Add($"'{original.Name}' is already on sheet {onSheet}; placed a copy '{view.Name}' on {sheet.SheetNumber}");
                    }
                    else
                    {
                        placed.Add(new { viewId = original.Id.GetValue(), viewName = original.Name, success = false, message = alreadyPlaced ? $"Already on sheet {onSheet}" : "View cannot be placed on a sheet" });
                        continue;
                    }
                }

                var vp = Viewport.Create(doc, sheet.Id, view.Id, XYZ.Zero);
                viewports.Add((vp, view, duplicatedFrom));
            }

            if (viewports.Count == 0) return placed;
            doc.Regenerate();

            // Center of the title block (or of the sheet outline when there is none)
            XYZ center;
            var tbInstance = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstElement();
            var tbBox = tbInstance?.get_BoundingBox(sheet);
            if (tbBox != null) center = new XYZ((tbBox.Min.X + tbBox.Max.X) / 2, (tbBox.Min.Y + tbBox.Max.Y) / 2, 0);
            else
            {
                var o = sheet.Outline;
                center = new XYZ((o.Min.U + o.Max.U) / 2, (o.Min.V + o.Max.V) / 2, 0);
            }

            // One row, evenly spaced, centered
            double gap = 10.0 / 304.8;
            var widths = viewports.Select(x => { var ol = x.vp.GetBoxOutline(); return ol.MaximumPoint.X - ol.MinimumPoint.X; }).ToList();
            double total = widths.Sum() + gap * (viewports.Count - 1);
            double x0 = center.X - total / 2;
            for (int i = 0; i < viewports.Count; i++)
            {
                double cx = x0 + widths[i] / 2;
                viewports[i].vp.SetBoxCenter(new XYZ(cx, center.Y, 0));
                x0 += widths[i] + gap;
                placed.Add(new
                {
                    viewId = viewports[i].view.Id.GetValue(),
                    viewName = viewports[i].view.Name,
                    viewportId = viewports[i].vp.Id.GetValue(),
                    duplicatedFrom = viewports[i].duplicatedFrom,
                    success = true
                });
            }
            return placed;
        }

        private static string UniqueViewName(Document doc, string baseName)
        {
            var names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
            if (!names.Contains(baseName)) return baseName;
            for (int i = 2; i < 100; i++)
                if (!names.Contains($"{baseName} ({i})")) return $"{baseName} ({i})";
            return baseName + " " + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private static View FindViewByName(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var candidates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && !(v is ViewSheet) && !(v is ViewSchedule) && string.Equals(v.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            return candidates
                .OrderBy(v => v.ViewType == ViewType.FloorPlan ? 0 : v.ViewType == ViewType.CeilingPlan ? 1 : 2)
                .ThenBy(v => v.Id.GetValue())
                .FirstOrDefault();
        }

        /// <summary>Width/height (feet) of every title block type, measured on a throw-away sheet.</summary>
        private static List<(ElementId id, string name, double w, double h)> MeasureTitleBlocks(Document doc)
        {
            var list = new List<(ElementId, string, double, double)>();
            foreach (var tb in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType().Cast<FamilySymbol>()
                         .OrderBy(t => t.FamilyName).ThenBy(t => t.Name))
            {
                using (var st = new SubTransaction(doc))
                {
                    st.Start();
                    try
                    {
                        var temp = ViewSheet.Create(doc, tb.Id);
                        doc.Regenerate();
                        var inst = new FilteredElementCollector(doc, temp.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstElement();
                        var bb = inst?.get_BoundingBox(temp);
                        if (bb != null) list.Add((tb.Id, $"{tb.FamilyName}: {tb.Name}", bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y));
                    }
                    catch { }
                    st.RollBack();
                }
            }
            return list;
        }

        /// <summary>Smallest title block whose sheet is at least 10% larger than the largest view; else the largest.</summary>
        private static ElementId PickTitleBlock(List<(ElementId id, string name, double w, double h)> sizes, List<View> views)
        {
            if (sizes.Count == 0) return null;
            double needW = 0, needH = 0;
            foreach (var v in views)
            {
                try
                {
                    var o = v.Outline; // paper-space size of the view at its scale
                    needW = Math.Max(needW, o.Max.U - o.Min.U);
                    needH = Math.Max(needH, o.Max.V - o.Min.V);
                }
                catch { }
            }
            var ordered = sizes.OrderBy(s => s.w * s.h).ThenBy(s => s.name).ToList();
            if (needW <= 0) return ordered.First().id;
            var fit = ordered.FirstOrDefault(s => s.w >= needW * 1.1 && s.h >= needH * 1.1);
            return fit.id ?? ordered.Last().id;
        }

        private static ElementId FindTitleBlock(Document doc, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var types = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType()
                .Cast<FamilySymbol>()
                .OrderBy(t => t.FamilyName).ThenBy(t => t.Name)
                .ToList();
            return (types.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals($"{t.FamilyName}: {t.Name}", name, StringComparison.OrdinalIgnoreCase))
                    ?? types.FirstOrDefault(t => t.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 || t.FamilyName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0))?.Id;
        }

        public string GetName() => "Batch Create Sheets";
    }

    public class SheetDefinition
    {
        public string Number { get; set; }
        public string Name { get; set; }
        public string TitleBlockName { get; set; }
        public List<long> ViewIds { get; set; }
        public List<string> ViewNames { get; set; }
    }
}
