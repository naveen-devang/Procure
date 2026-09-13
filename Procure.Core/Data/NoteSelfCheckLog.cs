using System;
using System.Diagnostics;
using System.IO;

namespace Procure.Data
{
    // Parallel to SelfCheckLog, writing to %TEMP%\procure-note-selfcheck.log so the notes checks
    // don't clobber the todo checks' file.
    internal static class NoteSelfCheckLog
    {
        public static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "procure-note-selfcheck.log");

        public static void Reset()
        {
            try { File.WriteAllText(Path, $"--- run {DateTime.Now:u} ---{Environment.NewLine}"); } catch { }
        }

        public static void Write(string line)
        {
            Debug.WriteLine(line);
            try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}"); } catch { }
        }
    }
}
