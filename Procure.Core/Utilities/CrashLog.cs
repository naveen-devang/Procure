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
        private static readonly string LogPath = Path.Combine(AppPaths.AppData, "crash.log");
        private static readonly string PreviousLogPath = Path.Combine(AppPaths.AppData, "crash.previous.log");

        /// <summary>Roll over at 1 MB - tens of thousands of lines, far more history than anyone
        /// reads, and small enough to open in Notepad and to attach to a message.</summary>
        private const long MaxBytes = 1024 * 1024;

        private static readonly object Gate = new();
        private static bool _rolledThisRun;

        public static void Write(string context, Exception? ex = null)
        {
            try
            {
                lock (Gate)
                {
                    RollIfTooBig();
                    File.AppendAllText(LogPath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{(ex is null ? "" : "\n" + ex)}\n\n");
                }
            }
            catch
            {
                // Nothing left to do - logging must never itself throw.
            }
        }

        /// <summary>Keeps one generation: the current log and the one before it, so the file cannot
        /// grow for the life of the install while a crash that happened yesterday is still readable.
        /// Checked once per run - the size only moves in one direction, and stat-ing the file on
        /// every single write would be the expensive part of logging.</summary>
        private static void RollIfTooBig()
        {
            if (_rolledThisRun) return;
            _rolledThisRun = true;

            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxBytes) return;

            File.Delete(PreviousLogPath);
            File.Move(LogPath, PreviousLogPath);
        }

    }
}
