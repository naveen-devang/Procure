using System;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using WinColors = Microsoft.UI.Colors;

namespace Procure.App.Views;

/// <summary>
/// Native rich-text note editor. Direct WinUI port of the MAUI NoteEditorHandler
/// (Platforms/Windows/NoteEditorHandler.cs) - same TOM formatting, same colour-strip
/// and theme-foreground handling, same 150 ms edit debounce. RTF crosses the boundary
/// through Load() / ContentChanged, never a TwoWay property (that wipes the undo stack).
/// </summary>
public sealed partial class NoteEditor : UserControl
{
    public event EventHandler<(string rtf, string plainText)>? ContentChanged;

    private bool _suppress;
    private string _baseline = string.Empty;
    private readonly DispatcherQueue _queue = DispatcherQueue.GetForCurrentThread();

    public NoteEditor()
    {
        InitializeComponent();

        var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(WinColors.Transparent);
        foreach (var key in new[]
                 {
                     "TextControlBackground", "TextControlBackgroundPointerOver",
                     "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver",
                     "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
                 })
            Rich.Resources[key] = transparent;
        Rich.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
        Rich.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);

        Rich.TextChanged += OnTextChanged;
        Rich.ActualThemeChanged += (_, _) => ApplyThemeForeground();
    }

    public bool IsReadOnly { get => Rich.IsReadOnly; set => Rich.IsReadOnly = value; }

    // ---- load (once per note) ----
    public void Load(string? rtf)
    {
        _suppress = true;
        try
        {
            if (string.IsNullOrEmpty(rtf))
                Rich.Document.SetText(TextSetOptions.None, string.Empty);
            else
                Rich.Document.SetText(TextSetOptions.FormatRtf, StripColours(rtf));
            Rich.Document.Selection.SetRange(0, 0);
        }
        catch { /* malformed RTF - leave the box empty rather than crash */ }
        finally { _suppress = false; }

        ApplyThemeForeground();
        _baseline = CurrentStrippedRtf();
    }

    private string CurrentStrippedRtf()
    {
        Rich.Document.GetText(TextGetOptions.FormatRtf, out var rtf);
        return StripColours(rtf);
    }

    // ---- edits out (debounced 150 ms; the real save is debounced again downstream) ----
    private void OnTextChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        // Restart the one timer instead of building a new one per keystroke. The old version left
        // every timer it created running: typing a hundred characters meant a hundred live timer
        // objects, each holding the editor and each waking the UI thread once to discard its work.
        _editDebounce ??= CreateEditDebounce();
        _editDebounce.Stop();
        _editDebounce.Start();
    }

    private DispatcherQueueTimer? _editDebounce;

    private DispatcherQueueTimer CreateEditDebounce()
    {
        var timer = _queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(150);
        timer.IsRepeating = false;
        timer.Tick += (t, _) =>
        {
            t.Stop();
            if (_suppress) return;
            var stripped = CurrentStrippedRtf();
            if (stripped == _baseline) return;
            _baseline = stripped;
            Rich.Document.GetText(TextGetOptions.None, out var plain);
            ContentChanged?.Invoke(this, (stripped, plain.TrimEnd('\r', '\n')));
        };
        return timer;
    }

    // ---- formatting ----
    public void Apply(string action)
    {
        var sel = Rich.Document.Selection;
        if (sel is null) return;

        switch (action)
        {
            case "Bold": sel.CharacterFormat.Bold = FormatEffect.Toggle; break;
            case "Italic": sel.CharacterFormat.Italic = FormatEffect.Toggle; break;
            case "Underline":
                sel.CharacterFormat.Underline = sel.CharacterFormat.Underline == UnderlineType.None
                    ? UnderlineType.Single : UnderlineType.None;
                break;
            case "Strike": sel.CharacterFormat.Strikethrough = FormatEffect.Toggle; break;
            case "Bullet": ToggleList(sel, MarkerType.Bullet); break;
            case "Number": ToggleList(sel, MarkerType.Arabic); break;
            case "Checklist": ToggleChecklist(sel); break;
            case "H1": SetHeading(sel, 20f, FormatEffect.On); break;
            case "H2": SetHeading(sel, 15.5f, FormatEffect.On); break;
            case "Body": SetHeading(sel, 11f, FormatEffect.Off); break;
            case "Undo": if (Rich.Document.CanUndo()) Rich.Document.Undo(); break;
            case "Redo": if (Rich.Document.CanRedo()) Rich.Document.Redo(); break;
        }

        if (Rich.FocusState == FocusState.Unfocused) Rich.Focus(FocusState.Programmatic);
    }

    private static void ToggleList(ITextSelection sel, MarkerType marker) =>
        sel.ParagraphFormat.ListType = sel.ParagraphFormat.ListType == marker ? MarkerType.None : marker;

    private static void SetHeading(ITextSelection sel, float size, FormatEffect bold)
    {
        sel.CharacterFormat.Size = size;
        sel.CharacterFormat.Bold = bold;
    }

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
        foreach (var line in lines)
        {
            if (line.Length == 0 || line.StartsWith(Unchecked) || line.StartsWith(Checked)) continue;
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
            else if (lines[i].StartsWith(Unchecked)) lines[i] = lines[i][Unchecked.Length..];
            else if (lines[i].StartsWith(Checked)) lines[i] = lines[i][Checked.Length..];
        }
        range.SetText(TextSetOptions.None, string.Join('\r', lines));
    }

    private static readonly Regex ColourTable = new(@"\{\\colortbl[^}]*\}", RegexOptions.Compiled);
    private static readonly Regex ColourRun = new(@"\\cf\d+ ?|\\highlight\d+ ?", RegexOptions.Compiled);

    private static string StripColours(string rtf)
    {
        if (string.IsNullOrEmpty(rtf)) return rtf;
        rtf = ColourTable.Replace(rtf, string.Empty);
        rtf = ColourRun.Replace(rtf, string.Empty);
        return rtf;
    }

    private void ApplyThemeForeground()
    {
        var color = Rich.ActualTheme == ElementTheme.Dark ? WinColors.White : WinColors.Black;
        _suppress = true;
        try
        {
            Rich.Document.GetRange(0, int.MaxValue).CharacterFormat.ForegroundColor = color;
            var dcf = Rich.Document.GetDefaultCharacterFormat();
            dcf.ForegroundColor = color;
            Rich.Document.SetDefaultCharacterFormat(dcf);
        }
        catch { }
        finally { _suppress = false; }
    }
}
