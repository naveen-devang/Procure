using System;
using System.IO;

namespace Procure.Utilities
{
    /// <summary>Last-resort diagnostic trail. Before this existed, a crash or a swallowed load
    /// failure left zero trace anywhere - there was no way to confirm what actually happened, only
    /// to theorize from reading the code. Appends to a plain text file next to the database; never
    /// throws itself, since a logger that can crash the app is worse than no logger.</summary>
    public static class CrashLog
    {
        private static readonly string LogPath =
            Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "crash.log");

        public static void Write(string context, Exception? ex = null)
        {
            try
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{(ex is null ? "" : "\n" + ex)}\n\n");
            }
            catch
            {
                // Nothing left to do - logging must never itself throw.
            }
        }
    }
}
