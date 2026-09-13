using System;

namespace Procure.Utilities
{
    /// <summary>
    /// Lets Core code (view models) report a page-load time without depending on the
    /// UI-layer PerfHud. The host wires <see cref="PageLoad"/> to the HUD at startup.
    /// </summary>
    public static class PerfProbe
    {
        public static Action<string, long>? PageLoad { get; set; }

        public static void ReportPageLoad(string what, long milliseconds) =>
            PageLoad?.Invoke(what, milliseconds);
    }
}
