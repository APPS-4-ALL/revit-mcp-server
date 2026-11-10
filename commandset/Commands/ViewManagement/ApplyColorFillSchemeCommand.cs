using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.ViewManagement;
using RevitMCPSDK.API.Base;
using System;
using System.Collections.Generic;

namespace RevitMCPCommandSet.Commands.ViewManagement
{
    public class ApplyColorFillSchemeCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private ApplyColorFillSchemeEventHandler _handler => (ApplyColorFillSchemeEventHandler)Handler;

        public override string CommandName => "apply_color_fill_scheme";

        public ApplyColorFillSchemeCommand(UIApplication uiApp)
            : base(new ApplyColorFillSchemeEventHandler(), uiApp) { }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    _handler.CategoryName = parameters?["categoryName"]?.Value<string>() ?? "Rooms";
                    _handler.ParameterName = parameters?["parameterName"]?.Value<string>() ?? "";
                    _handler.SchemeName = parameters?["schemeName"]?.Value<string>() ?? "";
                    _handler.ViewId = parameters?["viewId"]?.Value<long>() ?? 0;
                    _handler.PlaceLegend = parameters?["placeLegend"]?.Value<bool>() ?? true;
                    _handler.LegendPosition = parameters?["legendPosition"]?.Value<string>() ?? "right";
                    _handler.HideFloors = parameters?["hideFloors"]?.Value<bool>() ?? true;
                    _handler.ZoomToFit = parameters?["zoomToFit"]?.Value<bool>() ?? true;

                    var customColors = new List<ApplyColorFillSchemeEventHandler.ColorMapping>();
                    if (parameters?["customColors"] is JArray raw)
                    {
                        foreach (var item in raw)
                        {
                            customColors.Add(new ApplyColorFillSchemeEventHandler.ColorMapping
                            {
                                Value = item["value"]?.Value<string>() ?? "",
                                R = item["r"]?.Value<int>() ?? 200,
                                G = item["g"]?.Value<int>() ?? 200,
                                B = item["b"]?.Value<int>() ?? 200
                            });
                        }
                    }
                    _handler.CustomColors = customColors;

                    _handler.SetParameters();

                    if (RaiseAndWaitForCompletion(60000))
                        return _handler.Result;

                    throw new TimeoutException("Apply color fill scheme timed out");
                }
                catch (Exception ex)
                {
                    throw new Exception($"Apply color fill scheme failed: {ex.Message}");
                }
            }
        }
    }
}
