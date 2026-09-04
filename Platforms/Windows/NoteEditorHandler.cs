using System;
using System.Text.RegularExpressions;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Procure.Controls;
using Mux = Microsoft.UI.Xaml;
using WinColors = Microsoft.UI.Colors;

namespace Procure.Platforms.Windows
{
    // Bridges the MAUI NoteEditor View to a native WinUI RichEditBox. Formatting is applied through
    // the Text Object Model (Document.Selection); the note round-trips as RTF.
    public class NoteEditorHandler : ViewHandler<NoteEditor, RichEditBox>
    {
        public static readonly IPropertyMapper<NoteEditor, NoteEditorHandler> Mapper =
            new PropertyMapper<NoteEditor, NoteEditorHandler>(ViewMapper)
            {
                [nameof(NoteEditor.IsReadOnly)] = (h, v) => h.PlatformView.IsReadOnly = v.IsReadOnly,
            };

        public NoteEditorHandler() : base(Mapper) { }

        // True while we push a document programmatically, so the resulting TextChanged is ignored.
        private bool _suppress;

        // The colour-stripped RTF last handed to the page model. TextChanged that produces the same
        // value (theme recolour, load echo that leaked past _suppress) is not a real edit and never
        // reaches the page model - so opening a note, or toggling the theme, never triggers a save.
        private string _baseline = string.Empty;

        private string CurrentStrippedRtf()
        {
            PlatformView.Document.GetText(TextGetOptions.FormatRtf, out var rtf);
            return StripColours(rtf);
        }

        protected override RichEditBox CreatePlatformView() => new()
        {
            IsSpellCheckEnabled = true,
            TextWrapping = Mux.TextWrapping.Wrap,
            BorderThickness = new Mux.Thickness(0),
            Padding = new Mux.Thickness(6, 6, 6, 40),
            VerticalContentAlignment = Mux.VerticalAlignment.Top,
            // No RequestedTheme: an explicit value breaks WinUI theme inheritance, so the control
            // would freeze on its creation-time theme while the rest of the page re-themes on a
            // toggle. Left to inherit from the page root, which NativeTheme.ApplyToPages drives.
        };

        protected override void ConnectHandler(RichEditBox platformView)
        {
            base.ConnectHandler(platformView);

            // Same background and border in every visual state, so the area never changes
            // appearance on focus / hover. The wrapping MAUI Border is the only visible chrome and
            // it carries the AppThemeBinding, so this stays theme-correct.
            var transparent = new Mux.Media.SolidColorBrush(WinColors.Transparent);
            foreach (var key in new[]
                     {
                         "TextControlBackground", "TextControlBackgroundPointerOver",
                         "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
                         "TextControlBorderBrush", "TextControlBorderBrushPointerOver",
                         "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
                     })
                platformView.Resources[key] = transparent;
            platformView.Resources["TextControlBorderThemeThickness"] = new Mux.Thickness(0);
            platformView.Resources["TextControlBorderThemeThicknessFocused"] = new Mux.Thickness(0);

            platformView.TextChanged += OnTextChanged;
            platformView.ActualThemeChanged += OnActualThemeChanged;
            VirtualView.LoadRequested += OnLoadRequested;
            VirtualView.FormatRequested += OnFormatRequested;
        }

        protected override void DisconnectHandler(RichEditBox platformView)
        {
            platformView.TextChanged -= OnTextChanged;
            platformView.ActualThemeChanged -= OnActualThemeChanged;
            if (VirtualView is not null)
            {
                VirtualView.LoadRequested -= OnLoadRequested;
                VirtualView.FormatRequested -= OnFormatRequested;
            }
            base.DisconnectHandler(platformView);
        }

        // ---- load (once per note) ----
        private void OnLoadRequested(object? sender, string rtf)
        {
            _suppress = true;
            try
            {
                if (string.IsNullOrEmpty(rtf))
                    PlatformView.Document.SetText(TextSetOptions.None, string.Empty);
                else
                    // Strip colours on load too, not just on save: notes written before the
                    // save-side strip (or in another theme) still carry a baked foreground.
                    PlatformView.Document.SetText(TextSetOptions.FormatRtf, StripColours(rtf));

                // Caret at the start; SetText also clears the undo stack, which is what we want on load.
                PlatformView.Document.Selection.SetRange(0, 0);
            }
            catch { /* malformed RTF - leave the box empty rather than crash */ }
            finally { _suppress = false; }

            ApplyThemeForeground();
            _baseline = CurrentStrippedRtf();   // the freshly-loaded note is the "no changes" state
        }

        // ---- edits out ----
        // CurrentStrippedRtf() pulls the FULL document's RTF and runs it through two regex passes -
        // cost scales with document size. Running that on every single keystroke measurably costs
        // CPU on a long note, for no benefit: the actual save is already debounced 800ms downstream
        // (NotePageModel.ScheduleSave), so nothing needs this synchronously. Same generation-counter
        // debounce idiom used for search/filter elsewhere in the app.
        private int _textChangedGeneration;

        private void OnTextChanged(object? sender, Mux.RoutedEventArgs e)
        {
            if (_suppress || VirtualView is null) return;

            var generation = ++_textChangedGeneration;
            Microsoft.Maui.Dispatching.Dispatcher.GetForCurrentThread()
                ?.DispatchDelayed(TimeSpan.FromMilliseconds(150), () =>
                {
                    if (generation != _textChangedGeneration || _suppress || VirtualView is null) return;

                    var stripped = CurrentStrippedRtf();
                    if (stripped == _baseline) return;   // nothing actually changed
                    _baseline = stripped;

                    PlatformView.Document.GetText(TextGetOptions.None, out var plain);
                    VirtualView.RaiseContentChanged(stripped, plain.TrimEnd('\r', '\n'));
                });
        }

        // ---- formatting ----
        private void OnFormatRequested(object? sender, NoteFormatEventArgs e)
        {
            var sel = PlatformView.Document.Selection;
            if (sel is null) return;

            switch (e.Action)
            {
                case NoteFormat.Bold:
                    sel.CharacterFormat.Bold = FormatEffect.Toggle;
                    break;
                case NoteFormat.Italic:
                    sel.CharacterFormat.Italic = FormatEffect.Toggle;
                    break;
                case NoteFormat.Underline:
                    sel.CharacterFormat.Underline = sel.CharacterFormat.Underline == UnderlineType.None
                        ? UnderlineType.Single : UnderlineType.None;
                    break;
                case NoteFormat.Strike:
                    sel.CharacterFormat.Strikethrough = FormatEffect.Toggle;
                    break;
                case NoteFormat.BulletList:
                    ToggleList(sel, MarkerType.Bullet);
                    break;
                case NoteFormat.NumberList:
                    ToggleList(sel, MarkerType.Arabic);
                    break;
                case NoteFormat.Checklist:
                    ToggleChecklist(sel);
                    break;
                case NoteFormat.Heading1:
                    SetHeading(sel, 20f, FormatEffect.On);
                    break;
                case NoteFormat.Heading2:
                    SetHeading(sel, 15.5f, FormatEffect.On);
                    break;
                case NoteFormat.Body:
                    SetHeading(sel, 11f, FormatEffect.Off);
                    break;
                case NoteFormat.Undo:
                    if (PlatformView.Document.CanUndo()) PlatformView.Document.Undo();
                    break;
                case NoteFormat.Redo:
                    if (PlatformView.Document.CanRedo()) PlatformView.Document.Redo();
                    break;
            }

            // Toolbar buttons carry AllowFocusOnInteraction=false, so a pointer click keeps focus
            // here. Only reclaim it for keyboard-activated buttons.
            if (PlatformView.FocusState == Mux.FocusState.Unfocused)
                PlatformView.Focus(Mux.FocusState.Programmatic);
        }

        private static void ToggleList(ITextSelection sel, MarkerType marker) =>
            sel.ParagraphFormat.ListType = sel.ParagraphFormat.ListType == marker ? MarkerType.None : marker;

        private static void SetHeading(ITextSelection sel, float size, FormatEffect bold)
        {
            sel.CharacterFormat.Size = size;
            sel.CharacterFormat.Bold = bold;
        }

        // ponytail: RichEditBox has no checkbox list marker. v1 checklist = a "U+2610 / U+2612 "
        // prefix on each selected paragraph, toggled here and by a tap in the page. Upgrade path is
        // a custom paragraph style if this proves fiddly.
        private const string Unchecked = "\u2610 ";
        private const string Checked = "\u2612 ";

        private static void ToggleChecklist(ITextSelection sel)
        {
            var range = sel.GetClone();
            range.StartOf(TextRangeUnit.Paragraph, false);
            range.EndOf(TextRangeUnit.Paragraph, true);

            range.GetText(TextGetOptions.None, out var block);
            var lines = block.Split('\r');
            var anyBare = false;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith(Unchecked) || lines[i].StartsWith(Checked)) continue;
                if (lines[i].Length == 0) continue;
                anyBare = true;
                break;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                if (anyBare)
                {
                    if (!lines[i].StartsWith(Unchecked) && !lines[i].StartsWith(Checked))
                        lines[i] = Unchecked + lines[i];
                }
                else
                {
                    if (lines[i].StartsWith(Unchecked)) lines[i] = lines[i][Unchecked.Length..];
                    else if (lines[i].StartsWith(Checked)) lines[i] = lines[i][Checked.Length..];
                }
            }

            range.SetText(TextSetOptions.None, string.Join('\r', lines));
        }

        // RichEditBox emits a colour table even for "automatic" text, and once an RTF carries one,
        // "auto" resolves to the OS window-text colour (black), not the themed control foreground -
        // so a note loaded from RTF stays black on a dark ground. Drop the table AND every \cf
        // reference entirely; the control then paints from its own Foreground, which follows the
        // theme. Safe for v1 (no colour picker); revisit when Phase 2 adds colours.
        private static readonly Regex ColourTable = new(@"\{\\colortbl[^}]*\}", RegexOptions.Compiled);
        private static readonly Regex ColourRun = new(@"\\cf\d+ ?|\\highlight\d+ ?", RegexOptions.Compiled);

        private static string StripColours(string rtf)
        {
            if (string.IsNullOrEmpty(rtf)) return rtf;
            rtf = ColourTable.Replace(rtf, string.Empty);
            rtf = ColourRun.Replace(rtf, string.Empty);
            return rtf;
        }

        // Belt-and-suspenders on top of StripColours: force every run and the document default to
        // the theme's text colour. Runs on load and on every theme change, so switching notes after
        // a toggle can't leave a doc painted in the old theme's colour.
        // ponytail: nukes any per-run colour - fine until a colour picker exists.
        private void ApplyThemeForeground()
        {
            if (PlatformView is null) return;
            var color = PlatformView.ActualTheme == Mux.ElementTheme.Dark ? WinColors.White : WinColors.Black;

            _suppress = true;
            try
            {
                var all = PlatformView.Document.GetRange(0, int.MaxValue);
                all.CharacterFormat.ForegroundColor = color;

                var dcf = PlatformView.Document.GetDefaultCharacterFormat();
                dcf.ForegroundColor = color;
                PlatformView.Document.SetDefaultCharacterFormat(dcf);
            }
            catch { }
            finally { _suppress = false; }
        }

        private void OnActualThemeChanged(Mux.FrameworkElement sender, object args) => ApplyThemeForeground();
    }
}
