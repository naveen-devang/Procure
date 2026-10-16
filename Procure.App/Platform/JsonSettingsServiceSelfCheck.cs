using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Procure.App.Platform;

/// <summary>
/// Exercises JsonSettingsService against scratch files: the atomic write, the last-good copy, a damaged
/// file being kept rather than overwritten, a failed write being reported, and concurrent use. Opt in
/// with PROCURE_SETTINGS_SELFCHECK=1 (Debug builds). Never touches the real settings.json.
/// </summary>
internal static class JsonSettingsServiceSelfCheck
{
    public static void Run()
    {
        var log = new StringBuilder();
        var fails = 0;
        var checks = 0;
        void Check(bool ok, string what)
        {
            checks++;
            if (!ok) fails++;
            log.AppendLine((ok ? "  ok    " : "  FAIL  ") + what);
        }

        var dir = Path.Combine(Path.GetTempPath(), "procure-settings-selfcheck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            JsonSettingsService Open() => new(path, wireDatabaseFolder: false);

            // write and read back
            var a = Open();
            a.AppTheme = "Dark";
            a.UrgentOverdueDays = 3;
            Check(a.LastSaveError is null, "a normal save reports no error");
            Check(File.Exists(path) && !File.Exists(path + ".tmp"), "the file exists and no temp file is left behind");
            var b = Open();
            Check(b.AppTheme == "Dark" && b.UrgentOverdueDays == 3, "a new instance reads what was saved");

            // second save keeps the previous file as .bak
            a.AccentTheme = "Mint";
            Check(File.Exists(path + ".bak"), "a later save keeps the previous file as .bak");

            // damaged main file, good .bak: restored, the damaged one kept
            File.WriteAllText(path, "{ this is not json");
            var c = Open();
            Check(c.AppTheme == "Dark", "a damaged file falls back to the last good copy");
            Check(Directory.GetFiles(dir, "settings.damaged-*.json").Length == 1, "and the damaged file is kept aside, not overwritten");
            c.DefaultCurrency = "USD";
            Check(Open().DefaultCurrency == "USD", "saving after recovery works");

            // damaged main file and no good copy: defaults, no exception, damaged file kept
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
            File.WriteAllText(path, "null");
            var d = Open();
            Check(d.AppTheme == "System" && d.UrgentOverdueDays == 5, "no readable file at all gives defaults, without throwing");
            Check(Directory.GetFiles(dir, "settings.damaged-*.json").Length == 1, "and the unreadable file is kept");

            // main file missing but .bak present (swap interrupted)
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
            var e1 = Open(); e1.AppTheme = "Light"; e1.AppTheme = "Dark";
            File.Delete(path);
            Check(Open().AppTheme is "Light" or "Dark", "a missing file with a .bak present recovers from the .bak");

            // a write that cannot happen is reported, not thrown
            var broken = new JsonSettingsService(Path.Combine(dir, "no-such-folder", "settings.json"), wireDatabaseFolder: false);
            var threw = false;
            try { broken.AppTheme = "Dark"; } catch { threw = true; }
            Check(!threw, "a save to an unwritable place does not throw");
            Check(broken.LastSaveError is not null, "but LastSaveError says why");
            Check(broken.AppTheme == "Dark", "and the value still applies for this run");

            // concurrent readers and a writer
            var shared = Open();
            var errors = new List<Exception>();
            var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                try
                {
                    for (var n = 0; n < 300; n++)
                    {
                        if (i % 2 == 0) shared.UrgentOverdueDays = 1 + (n % 9);
                        else _ = shared.UrgentOverdueDays + shared.NormalOverdueDays;
                    }
                }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            })).ToArray();
            Task.WaitAll(tasks);
            Check(errors.Count == 0, $"8 threads reading and saving at once raise no error ({errors.Count})");
            Check(Open().UrgentOverdueDays is >= 1 and <= 9, "and the file left behind is readable");
        }
        catch (Exception ex)
        {
            fails++;
            log.AppendLine("  THREW " + ex);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* scratch */ }
        }

        var summary = fails == 0 ? $"PASS  {checks} checks" : $"FAIL  {fails} of {checks} checks failed";
        Procure.Utilities.CrashLog.Write("SETTINGS SELF-CHECK " + summary + Environment.NewLine + log);
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "procure-settings-selfcheck.log"), summary + Environment.NewLine + log); }
        catch { /* the crash log already has it */ }
    }
}
