using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RevitMCPCommandSet.Services
{
    /// <summary>
    /// Works out each door's swing from its plan symbol (the swing arc: its center is the hinge,
    /// its bulge is the side the leaf opens to) and writes it to a text parameter on the doors,
    /// creating that parameter if the project does not have it.
    ///
    /// Values: "Left" / "Right" (hinge side as seen from the side you push the door from),
    /// "Pair" (two leaves hinged on opposite jambs), "Double acting" (swings both ways),
    /// "None" (no swing arc, e.g. sliding or openings).
    /// </summary>
    public class SetDoorSwingEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        public string ParameterName { get; set; } = "Swing";
        public List<long> DoorIds { get; set; } = new List<long>();

        public AIResult<object> Result { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetParameters() { _resetEvent.Reset(); }
        public bool WaitForCompletion(int timeoutMilliseconds = 60000) { return _resetEvent.WaitOne(timeoutMilliseconds); }

        // Fixed GUID so every run (and every copy of the model) gets the identical shared parameter
        private static readonly Guid SwingParameterGuid = new Guid("6f3c1a52-4b7e-4f0e-9d4a-2a1d5e8b7c31");

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;

                var doors = (DoorIds != null && DoorIds.Count > 0
                        ? DoorIds.Select(id => doc.GetElement(ToId(id)))
                        : new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType())
                    .OfType<FamilyInstance>()
                    .Where(d => d.Category != null && d.Category.Id.Equals(new ElementId(BuiltInCategory.OST_Doors)))
                    .ToList();
                if (doors.Count == 0)
                    throw new InvalidOperationException("No doors found");

                // One plan view per level to read the plan symbol (swing arcs are plan-only geometry)
                var plans = new Dictionary<long, ViewPlan>();
                foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                             .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel != null)
                             .OrderBy(v => v.Name == v.GenLevel.Name ? 0 : 1)
                             .ThenBy(v => v.Name.Length)
                             .ThenBy(v => IdValue(v.Id)))
                {
                    long key = IdValue(v.GenLevel.Id);
                    if (!plans.ContainsKey(key)) plans[key] = v;
                }

                var swings = new Dictionary<FamilyInstance, string>();
                var notes = new List<string>();
                foreach (var d in doors)
                {
                    plans.TryGetValue(IdValue(d.LevelId), out var plan);
                    swings[d] = plan == null ? "None" : ComputeSwing(d, plan);
                    if (plan == null) notes.Add($"Door {IdValue(d.Id)}: no floor plan for its level");
                }

                bool created = false;
                int written = 0;
                using (var tg = new TransactionGroup(doc, "Set Door Swing"))
                {
                    tg.Start();

                    if (doors[0].LookupParameter(ParameterName) == null)
                    {
                        using (var t = new Transaction(doc, "Create Swing Parameter"))
                        {
                            t.Start();
                            CreateDoorTextParameter(doc, ParameterName);
                            t.Commit();
                        }
                        created = true;
                    }

                    using (var t = new Transaction(doc, "Write Door Swing"))
                    {
                        t.Start();
                        foreach (var kv in swings)
                        {
                            var p = kv.Key.LookupParameter(ParameterName);
                            if (p == null || p.IsReadOnly || p.StorageType != StorageType.String)
                            {
                                notes.Add($"Door {IdValue(kv.Key.Id)}: '{ParameterName}' is missing, read-only or not text");
                                continue;
                            }
                            if (p.AsString() != kv.Value) p.Set(kv.Value);
                            written++;
                        }
                        t.Commit();
                    }

                    tg.Assimilate();
                }

                Result = new AIResult<object>
                {
                    Success = true,
                    Message = $"Wrote door swing to '{ParameterName}' on {written} of {doors.Count} doors" + (created ? " (parameter created)" : ""),
                    Response = new
                    {
                        parameterName = ParameterName,
                        parameterCreated = created,
                        doors = doors.Count,
                        written,
                        counts = swings.GroupBy(kv => kv.Value).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
                        notes = notes.Take(20),
                        nextSteps = new[] { $"Add '{ParameterName}' as a field in a door schedule (create_schedule)" }
                    }
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<object> { Success = false, Message = $"Set door swing failed: {ex.Message}" };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private static string ComputeSwing(FamilyInstance door, ViewPlan plan)
        {
            double width = door.Symbol?.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble()
                           ?? door.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble()
                           ?? 0;

            var arcs = new List<Arc>();
            var geometry = door.get_Geometry(new Options { View = plan });
            if (geometry != null) CollectArcs(geometry, arcs);
            arcs = arcs.Where(a => width <= 0 ? a.Radius > 0.5 : a.Radius > width * 0.3 && a.Radius < width * 1.2).ToList();
            if (arcs.Count == 0) return "None";

            XYZ loc = (door.Location as LocationPoint)?.Point;
            if (loc == null)
            {
                // Curtain-wall doors have no location point (and sometimes no model bounding box)
                var bb = door.get_BoundingBox(null) ?? door.get_BoundingBox(plan);
                if (bb == null) return "None";
                loc = (bb.Min + bb.Max) / 2;
            }
            XYZ facing = door.FacingOrientation, hand = door.HandOrientation;

            var sides = arcs.Select(a => Math.Sign(Flat(a.Evaluate(0.5, true) - loc).DotProduct(facing))).Where(s => s != 0).Distinct().ToList();
            var hinges = arcs.Select(a => Math.Sign(Flat(a.Center - loc).DotProduct(hand))).Where(s => s != 0).Distinct().ToList();

            if (sides.Count > 1) return "Double acting";
            if (hinges.Count > 1) return "Pair";
            if (sides.Count == 0) return "None";

            // Stand on the push side, looking toward the swing side: is the hinge on your right?
            XYZ look = facing.Multiply(sides[0]);
            XYZ right = new XYZ(look.Y, -look.X, 0);
            return Flat(arcs[0].Center - loc).DotProduct(right) > 0 ? "Right" : "Left";
        }

        private static void CollectArcs(GeometryElement geometry, List<Arc> arcs)
        {
            foreach (var obj in geometry)
            {
                if (obj is Arc arc && arc.IsBound) arcs.Add(arc);
                else if (obj is GeometryInstance gi) CollectArcs(gi.GetInstanceGeometry(), arcs);
            }
        }

        private static XYZ Flat(XYZ v) => new XYZ(v.X, v.Y, 0);

        private static void CreateDoorTextParameter(Document doc, string name)
        {
#if REVIT2024_OR_GREATER
            var app = doc.Application;
            string tempFile = System.IO.Path.GetTempFileName();
            string previous = app.SharedParametersFilename;
            try
            {
                System.IO.File.WriteAllText(tempFile, "");
                app.SharedParametersFilename = tempFile;
                var group = app.OpenSharedParameterFile().Groups.Create("MCP");
                var options = new ExternalDefinitionCreationOptions(name, SpecTypeId.String.Text)
                {
                    Visible = true,
                    Description = "Door swing (Left/Right/Pair/Double acting), computed from the plan swing arc",
                    GUID = name == "Swing" ? SwingParameterGuid : Guid.NewGuid()
                };
                var definition = group.Definitions.Create(options);
                var categories = app.Create.NewCategorySet();
                categories.Insert(Category.GetCategory(doc, BuiltInCategory.OST_Doors));
                var binding = app.Create.NewInstanceBinding(categories);
                if (!doc.ParameterBindings.Insert(definition, binding, GroupTypeId.IdentityData))
                    throw new InvalidOperationException($"Could not add parameter '{name}' to Doors");
            }
            finally
            {
                app.SharedParametersFilename = previous ?? "";
                try { System.IO.File.Delete(tempFile); } catch { }
            }
#else
            throw new InvalidOperationException($"Doors have no '{name}' parameter; create a text instance parameter '{name}' for Doors first (manage_project_parameters)");
#endif
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

        public string GetName() => "Set Door Swing";
    }
}
