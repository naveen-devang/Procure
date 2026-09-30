using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Procure.Utilities
{
    public enum NotesBlockKind { Heading, Bullet, Paragraph }

    /// <summary>A line of release notes, split into plain and bold stretches.</summary>
    public sealed record NotesBlock(NotesBlockKind Kind, IReadOnlyList<(string Text, bool Bold)> Spans)
    {
        public string PlainText => string.Concat(Spans.Select(s => s.Text));
    }

    /// <summary>
    /// The small part of Markdown the GitHub release notes use - "## heading", "- bullet" / "* bullet",
    /// "**bold**" - read into blocks the What's New window draws as real headings, bullets and bold,
    /// instead of showing the marks. Anything else is plain text.
    /// </summary>
    public static partial class ReleaseNotesFormat
    {
        [GeneratedRegex(@"\*\*(.+?)\*\*")]
        private static partial Regex BoldPattern();

        public static List<NotesBlock> Parse(string? markdown)
        {
            var blocks = new List<NotesBlock>();
            foreach (var raw in (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith('#'))
                    blocks.Add(new NotesBlock(NotesBlockKind.Heading, Spans(line.TrimStart('#').Trim())));
                else if (line.StartsWith("- ") || line.StartsWith("* "))
                    blocks.Add(new NotesBlock(NotesBlockKind.Bullet, Spans(line[2..].Trim())));
                else
                    blocks.Add(new NotesBlock(NotesBlockKind.Paragraph, Spans(line)));
            }
            return blocks;
        }

        /// <summary>The notes without any marks, one block per line - for the short preview in Settings.</summary>
        public static string Plain(string? markdown) =>
            string.Join("\n", Parse(markdown).Select(b => b.Kind == NotesBlockKind.Bullet ? "• " + b.PlainText : b.PlainText));

        private static List<(string, bool)> Spans(string text)
        {
            var spans = new List<(string, bool)>();
            var at = 0;
            foreach (Match m in BoldPattern().Matches(text))
            {
                if (m.Index > at) spans.Add((text[at..m.Index], false));
                spans.Add((m.Groups[1].Value, true));
                at = m.Index + m.Length;
            }
            if (at < text.Length) spans.Add((text[at..], false));
            return spans;
        }

        public static void SelfCheck()
        {
            static void Check(bool ok, string what)
            {
                if (!ok) throw new InvalidOperationException("ReleaseNotesFormat: " + what);
            }

            var blocks = Parse("## What's new\r\n\r\n**Undo.** Deleting shows a bar.\n- **Suppliers:** each page\n* plain bullet\nno marks here");
            Check(blocks.Count == 5, "blank lines dropped, one block per line");
            Check(blocks[0].Kind == NotesBlockKind.Heading && blocks[0].PlainText == "What's new", "heading without its marks");
            Check(blocks[1].Spans.Count == 2 && blocks[1].Spans[0] == ("Undo.", true) && blocks[1].Spans[1] == (" Deleting shows a bar.", false),
                "bold then plain");
            Check(blocks[2].Kind == NotesBlockKind.Bullet && blocks[2].Spans[0] == ("Suppliers:", true), "bullet with bold");
            Check(blocks[3].Kind == NotesBlockKind.Bullet && blocks[3].PlainText == "plain bullet", "star bullet");
            Check(blocks[4].Kind == NotesBlockKind.Paragraph && blocks[4].Spans.Single() == ("no marks here", false), "plain line");
            Check(Plain("## Fixed\n- **Items tab:** opens") == "Fixed\n• Items tab: opens", "plain text for the preview");
            Check(Parse(null).Count == 0 && Parse("**unclosed").Single().PlainText == "**unclosed", "empty and odd input");
        }
    }
}
