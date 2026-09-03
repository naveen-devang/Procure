using System;
using Microsoft.Maui.Controls;

namespace Procure.Controls
{
    // A rich-text note editor. On Windows it is backed by a native WinUI RichEditBox
    // (Platforms/Windows/NoteEditorHandler.cs) - MAUI ships no rich-text input control of its own.
    //
    // Data crosses the boundary through events, not bindable properties: the RTF is pushed in once
    // per note load via Load(), and edits come back out via ContentChanged. Round-tripping through a
    // TwoWay property would re-set the document on every keystroke and wipe the native undo stack.
    public enum NoteFormat
    {
        Bold, Italic, Underline, Strike,
        BulletList, NumberList, Checklist,
        Heading1, Heading2, Body,
        Undo, Redo
    }

    public sealed class NoteFormatEventArgs : EventArgs
    {
        public NoteFormatEventArgs(NoteFormat action) => Action = action;
        public NoteFormat Action { get; }
    }

    public sealed class NoteContentChangedEventArgs : EventArgs
    {
        public NoteContentChangedEventArgs(string rtf, string plainText)
        {
            Rtf = rtf;
            PlainText = plainText;
        }

        public string Rtf { get; }
        public string PlainText { get; }
    }

    public class NoteEditor : View
    {
        public bool IsReadOnly { get; set; }

        // Fired on every user edit. The page model debounces before persisting.
        public event EventHandler<NoteContentChangedEventArgs>? ContentChanged;

        // The toolbar raises these; the handler applies them to the document selection.
        public event EventHandler<NoteFormatEventArgs>? FormatRequested;

        // Page model raises this on note selection; the handler loads the RTF (once).
        public event EventHandler<string>? LoadRequested;

        public void Load(string? rtf) => LoadRequested?.Invoke(this, rtf ?? string.Empty);

        public void Apply(NoteFormat action) => FormatRequested?.Invoke(this, new NoteFormatEventArgs(action));

        internal void RaiseContentChanged(string rtf, string plainText)
            => ContentChanged?.Invoke(this, new NoteContentChangedEventArgs(rtf, plainText));
    }
}
