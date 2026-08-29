using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Procure.Data;
using Procure.Pages;
using Procure.Services;

namespace Procure.Utilities
{
    /// <summary>
    /// The runnable check behind the pastel accent: that the pastel reaches every fill in both modes,
    /// that Primary is deep in Light and pastel in Dark and follows a theme switch, that the picker's
    /// swatches show the colour the fills will get, that every DynamicResource key any XAML file asks
    /// for actually exists, and that dark button text is readable on all eight pastels.
    ///
    /// Run it by launching a Debug build with PROCURE_ACCENT_SELFCHECK=1 set. It changes the theme and
    /// accent for real, then puts both back. Debug only, opt-in only.
    /// </summary>
    internal static class AccentSelfCheck
    {
        public static async Task RunAsync()
        {
            var log = new StringBuilder();
            var settings = IPlatformApplication.Current!.Services.GetRequiredService<ISettingsService>();
            var savedTheme = settings.AppTheme;
            var savedAccent = settings.AccentTheme;
            try
            {
                await Task.Delay(2500);
                CheckEveryAccentInBothModes(settings, log);
                CheckPrimaryFollowsThemeSwitch(settings, log);
                await CheckSwatchesHonestAsync(log);
                CheckNoUndefinedResourceKeys(log);
                CheckButtonTextContrast(log);
                Report("PASS", log);
            }
            catch (Exception ex)
            {
                Report("FAIL " + ex.Message, log);
                throw;
            }
            finally
            {
                settings.AppTheme = savedTheme;
                settings.AccentTheme = savedAccent;
            }
        }

        private static Color Res(string key)
        {
            if (Application.Current!.Resources.TryGetValue(key, out var v) && v is Color c) return c;
            throw new InvalidOperationException($"Resource '{key}' is missing or not a Color.");
        }

        private static string Hex(Color c) => c.ToArgbHex();

        // --- Fix 1: pastel on every fill, both modes; Primary deep in Light, pastel in Dark ---------

        private static void CheckEveryAccentInBothModes(ISettingsService settings, StringBuilder log)
        {
            foreach (var mode in new[] { "Light", "Dark" })
            {
                settings.AppTheme = mode;
                foreach (var p in SettingsService.PastelPalettes)
                {
                    settings.AccentTheme = p.Id;
                    var fill = Res("AccentFill");
                    var primary = Res("Primary");
                    var expectPrimary = mode == "Dark" ? p.DarkColor : p.LightColor;
                    if (Hex(fill) != Hex(p.DarkColor))
                        throw new InvalidOperationException($"{mode}/{p.Id}: AccentFill is {Hex(fill)}, expected the pastel {Hex(p.DarkColor)} - the pastel is not reaching the fills.");
                    if (Hex(primary) != Hex(expectPrimary))
                        throw new InvalidOperationException($"{mode}/{p.Id}: Primary is {Hex(primary)}, expected {Hex(expectPrimary)}.");
                }
                log.AppendLine($"{mode}: all {SettingsService.PastelPalettes.Count} accents -> AccentFill = pastel, Primary = {(mode == "Dark" ? "pastel" : "deep")}");
            }
        }

        // --- Fix 1b: a theme switch with the accent held re-resolves Primary --------------------------

        private static void CheckPrimaryFollowsThemeSwitch(ISettingsService settings, StringBuilder log)
        {
            var p = SettingsService.PastelPalettes.First(x => x.Id == "Mint");
            settings.AccentTheme = p.Id;
            settings.AppTheme = "Light";
            var before = Hex(Res("Primary"));
            settings.AppTheme = "Dark";
            var after = Hex(Res("Primary"));
            log.AppendLine($"theme switch with Mint held: Primary {before} -> {after}");
            if (before != Hex(p.LightColor) || after != Hex(p.DarkColor))
                throw new InvalidOperationException($"Primary did not follow the theme switch: {before} -> {after}; expected {Hex(p.LightColor)} -> {Hex(p.DarkColor)}.");
            if (Hex(Res("AccentFill")) != Hex(p.DarkColor))
                throw new InvalidOperationException("AccentFill changed on a theme switch; the pastel fill must be the same in both modes.");
        }

        // --- Fix 2: each swatch's border is the pastel that swatch will apply ----------------------

        private static async Task CheckSwatchesHonestAsync(StringBuilder log)
        {
            // The DI singleton only receives DynamicResource updates once it is in the visual tree -
            // detached, it keeps whatever it resolved first and a wrong binding is invisible. So look
            // at the real page, on screen.
            await Shell.Current.GoToAsync("//settings");
            await Task.Delay(600);
            var page = IPlatformApplication.Current!.Services.GetRequiredService<SettingsPage>();
            if (!ReferenceEquals(Shell.Current.CurrentPage, page))
                throw new InvalidOperationException("Settings page is not the page on screen; cannot check live controls.");
            var swatches = Descendants(page).OfType<Button>()
                .Where(b => b.CommandParameter is string id && SettingsService.PastelPalettes.Any(p => p.Id == id))
                .ToList();
            if (swatches.Count != SettingsService.PastelPalettes.Count)
                throw new InvalidOperationException($"Found {swatches.Count} accent swatches; expected {SettingsService.PastelPalettes.Count}.");
            foreach (var b in swatches)
            {
                var p = SettingsService.PastelPalettes.First(x => x.Id == (string)b.CommandParameter);
                if (Hex(b.BorderColor) != Hex(p.DarkColor))
                    throw new InvalidOperationException($"Swatch '{p.Id}' shows {Hex(b.BorderColor)} but would apply {Hex(p.DarkColor)}.");
            }
            log.AppendLine($"swatches: {swatches.Count} previews match their fill");

            // And the real controls next to them: every toggle track, and every button that is not a
            // swatch (those paint their own chip), must be the pastel of the accent in force.
            var settings = IPlatformApplication.Current.Services.GetRequiredService<ISettingsService>();
            // In Dark mode Primary IS the pastel, so a control wrongly bound to Primary would pass.
            // Light is the mode where a wrong binding shows: Primary deep, AccentFill pastel.
            settings.AppTheme = "Light";
            await Task.Delay(200);
            var current = SettingsService.PastelPalettes.First(p => p.Id.Equals(settings.AccentTheme, StringComparison.OrdinalIgnoreCase));
            var all = Descendants(page).ToList();
            var switches = all.OfType<Microsoft.Maui.Controls.Switch>().ToList();
            // Only the accent-styled buttons: Settings is mostly grey secondary buttons by design.
            var primaryStyle = Application.Current!.Resources.TryGetValue("PrimaryButton", out var st) ? st as Style : null;
            var buttons = all.OfType<Button>().Where(b => primaryStyle != null && ReferenceEquals(b.Style, primaryStyle)).ToList();
            if (switches.Count == 0 || buttons.Count == 0)
                throw new InvalidOperationException($"Expected toggles and accent buttons on Settings; found {switches.Count} / {buttons.Count}.");
            foreach (var sw in switches)
                if (Hex(sw.OnColor) != Hex(current.DarkColor))
                    throw new InvalidOperationException($"A toggle track is {Hex(sw.OnColor)}; the pastel for {current.Id} is {Hex(current.DarkColor)} - the pastel is not reaching the fills.");
            var offFills = buttons.Where(b => Hex(b.BackgroundColor) != Hex(current.DarkColor)).Select(b => $"'{b.Text}'={Hex(b.BackgroundColor)}").ToList();
            log.AppendLine($"live: {switches.Count} toggles and {buttons.Count} accent buttons checked; off-pastel: {offFills.Count}");
            if (offFills.Count > 0)
                throw new InvalidOperationException("Filled buttons not on the pastel: " + string.Join(", ", offFills));
        }

        // --- Fix 3: nothing in any XAML file asks for a key that does not exist ---------------------
        // Six labels bound to an undefined 'AccentPrimary' for weeks with no error anywhere - a
        // DynamicResource to a missing key just silently leaves the property at its default.

        private static void CheckNoUndefinedResourceKeys(StringBuilder log)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Procure.csproj"))) dir = dir.Parent;
            if (dir == null) throw new InvalidOperationException("Could not locate Procure.csproj above the bin folder.");

            var keys = new SortedSet<string>();
            var definedInXaml = new HashSet<string>();   // page/control-level ResourceDictionaries
            foreach (var file in Directory.EnumerateFiles(dir.FullName, "*.xaml", SearchOption.AllDirectories))
            {
                if (file.Contains(@"\bin\") || file.Contains(@"\obj\")) continue;
                var text = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(text, @"\{(?:Dynamic|Static)Resource\s+([A-Za-z0-9_]+)\}"))
                    keys.Add(m.Groups[1].Value);
                foreach (Match m in Regex.Matches(text, @"x:Key=""([A-Za-z0-9_]+)"""))
                    definedInXaml.Add(m.Groups[1].Value);
            }
            var missing = keys.Where(k => !definedInXaml.Contains(k) && !Application.Current!.Resources.TryGetValue(k, out _)).ToList();
            log.AppendLine($"resource keys referenced: {keys.Count}, missing: {missing.Count}");
            if (missing.Count > 0)
                throw new InvalidOperationException("XAML references resource keys that do not exist: " + string.Join(", ", missing) + ". Those properties silently keep their defaults.");
        }

        // --- Fix 4: near-black text is readable on all eight pastels ----------------------------------

        private static void CheckButtonTextContrast(StringBuilder log)
        {
            var text = Res("DarkOnLightBackground");
            var worst = 99.0; var worstId = "";
            foreach (var p in SettingsService.PastelPalettes)
            {
                var ratio = Contrast(text, p.DarkColor);
                if (ratio < worst) { worst = ratio; worstId = p.Id; }
                if (ratio < 4.5)
                    throw new InvalidOperationException($"Button text {Hex(text)} on pastel {p.Id} {Hex(p.DarkColor)} is {ratio:0.0}:1; needs 4.5:1.");
            }
            var whiteWorst = SettingsService.PastelPalettes.Min(p => Contrast(Colors.White, p.DarkColor));
            log.AppendLine($"contrast: dark text worst {worst:0.0}:1 ({worstId}); white text would be {whiteWorst:0.0}:1");
        }

        private static double Contrast(Color a, Color b)
        {
            static double Lum(Color c)
            {
                static double Ch(float v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
                return 0.2126 * Ch(c.Red) + 0.7152 * Ch(c.Green) + 0.0722 * Ch(c.Blue);
            }
            var l1 = Lum(a) + 0.05; var l2 = Lum(b) + 0.05;
            return Math.Max(l1, l2) / Math.Min(l1, l2);
        }

        private static IEnumerable<Element> Descendants(Element root)
        {
            foreach (var child in ((IVisualTreeElement)root).GetVisualChildren().OfType<Element>())
            {
                yield return child;
                foreach (var d in Descendants(child)) yield return d;
            }
        }

        private static void Report(string result, StringBuilder log)
        {
            Debug.WriteLine("AccentSelfCheck: " + result);
            try
            {
                File.WriteAllText(
                    Path.Combine(DatabaseConstants.DatabaseDirectory, "accent-selfcheck.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {result}{Environment.NewLine}{log}");
            }
            catch
            {
                // A diagnostic must never be the thing that breaks the run.
            }
        }
    }
}
