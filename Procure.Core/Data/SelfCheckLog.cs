using System;
using System.Diagnostics;
using System.IO;

namespace Procure.Data
{
    // Self-checks run before any window exists and Debug.WriteLine is awkward to capture from a
    // packaged/unpackaged launch, so they also append here: %TEMP%\procure-todo-selfcheck.log.
    internal static class SelfCheckLog
    {
        public static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "procure-todo-selfcheck.log");

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
