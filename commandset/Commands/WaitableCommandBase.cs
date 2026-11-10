using Autodesk.Revit.UI;
using RevitMCPSDK.API.Base;
using RevitMCPSDK.API.Interfaces;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;

namespace RevitMCPCommandSet.Commands
{
    /// <summary>
    /// Base class for all commands. Fixes a stale-result bug in the SDK pattern:
    /// handlers signal completion with a ManualResetEvent that is only reset inside their
    /// SetParameters(...). Many commands assign handler properties directly and never call it,
    /// so from the second call on the event is already signaled: RaiseAndWaitForCompletion
    /// returns immediately with the PREVIOUS call's result while the real work runs afterwards.
    /// This wrapper resets every ManualResetEvent field on the handler before raising.
    /// </summary>
    public abstract class WaitableCommandBase : ExternalEventCommandBase
    {
        private static readonly ConcurrentDictionary<System.Type, FieldInfo[]> SignalFields =
            new ConcurrentDictionary<System.Type, FieldInfo[]>();

        protected WaitableCommandBase(IWaitableExternalEventHandler handler, UIApplication uiApp)
            : base(handler, uiApp)
        {
        }

        protected new bool RaiseAndWaitForCompletion(int timeoutMilliseconds)
        {
            ResetHandlerSignal(Handler);
            return base.RaiseAndWaitForCompletion(timeoutMilliseconds);
        }

        internal static void ResetHandlerSignal(object handler)
        {
            if (handler == null) return;
            var fields = SignalFields.GetOrAdd(handler.GetType(), FindSignalFields);
            foreach (var field in fields)
            {
                if (field.GetValue(handler) is EventWaitHandle signal)
                    signal.Reset();
            }
        }

        private static FieldInfo[] FindSignalFields(System.Type type)
        {
            var result = new System.Collections.Generic.List<FieldInfo>();
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (typeof(ManualResetEvent).IsAssignableFrom(f.FieldType))
                        result.Add(f);
                }
            }
            return result.ToArray();
        }
    }
}
