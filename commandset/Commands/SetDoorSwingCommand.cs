using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using System;
using System.Collections.Generic;

namespace RevitMCPCommandSet.Commands
{
    public class SetDoorSwingCommand : WaitableCommandBase
    {
        private static readonly object _executionLock = new object();
        private SetDoorSwingEventHandler _handler => (SetDoorSwingEventHandler)Handler;

        public override string CommandName => "set_door_swing";

        public SetDoorSwingCommand(UIApplication uiApp)
            : base(new SetDoorSwingEventHandler(), uiApp) { }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    var name = parameters?["parameterName"]?.Value<string>();
                    _handler.ParameterName = string.IsNullOrWhiteSpace(name) ? "Swing" : name.Trim();
                    _handler.DoorIds = parameters?["doorIds"]?.ToObject<List<long>>() ?? new List<long>();
                    _handler.SetParameters();

                    if (RaiseAndWaitForCompletion(60000))
                        return _handler.Result;

                    throw new TimeoutException("Set door swing timed out");
                }
                catch (Exception ex)
                {
                    throw new Exception($"Set door swing failed: {ex.Message}");
                }
            }
        }
    }
}
