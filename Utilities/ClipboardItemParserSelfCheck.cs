using System;
using System.Diagnostics;
using System.Linq;
using Procure.Models;

namespace Procure.Utilities
{
    /// <summary>
    /// The runnable check behind <see cref="ClipboardItemParser"/>'s clipboard grammar. It rebuilds
    /// the reported failure - an Excel cell that holds a multi-line item name, copied with its
    /// quantity and unit - and asserts it lands as ONE line item with the name intact, not shattered
    /// across several broken rows.
    ///
    /// Excel/Sheets put a cell containing newlines on the clipboard wrapped in "..." (RFC 4180),
    /// with "" for a literal quote. The old parser split on every CR/LF blindly, so one item became
    /// two and the wrapping quotes leaked into the name.
    ///
    /// Run it by launching a Debug build with PROCURE_SELFCHECK=1. No database is touched.
    /// </summary>
    internal static class ClipboardItemParserSelfCheck
    {
        private static readonly Guid Pr = Guid.NewGuid();

        public static void Run()
        {
            try
            {
                MultiLineCellStaysOneItem();
                QuotedCellKeepsItsCommas();
                PlainMultiRowStillFansOut();
                TabRowKeepsColumns();
                EscapedQuotesUnescape();
                HeaderRowStillSkipped();
                LooksLikeGridClassifies();
                NormalizeSingleNameUnwraps();
                BatchEntryMultiLineDescription();
                Debug.WriteLine("CLIPBOARD ITEM PARSER SELF-CHECKS PASSED");
                CrashLog.Write("CLIPBOARD ITEM PARSER SELF-CHECKS PASSED");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("CLIPBOARD ITEM PARSER SELF-CHECKS FAILED: " + ex);
                CrashLog.Write("CLIPBOARD ITEM PARSER SELF-CHECKS FAILED", ex);
                throw;
            }
        }

        /// <summary>The reported bug: one multi-line cell + qty + unit used to import as two rows.</summary>
        private static void MultiLineCellStaysOneItem()
        {
            var clip = "\"BOLT, HEX HD\r\nM12 x 40, GR 8.8\"\t50\tNOS\r\n";
            var items = ClipboardItemParser.ParsePrItems(clip, Pr);

            Assert(items.Count == 1, $"one item, not one per visual line; got {items.Count}");
            Assert(items[0].ItemName == "BOLT, HEX HD\r\nM12 x 40, GR 8.8", $"the line break survives inside the name; got '{items[0].ItemName}'");
            Assert(!items[0].ItemName.Contains('"'), "the wrapping quotes are gone");
            Assert(items[0].Quantity == 50m, $"quantity reads from column 2; got {items[0].Quantity}");
            Assert(items[0].Unit == "NOS", $"unit reads from column 3; got '{items[0].Unit}'");
        }

        /// <summary>A quoted single cell may contain commas - they are part of the name, not columns.</summary>
        private static void QuotedCellKeepsItsCommas()
        {
            var items = ClipboardItemParser.ParsePrItems("\"BEARING, DEEP GROOVE, 6205\"\t10\tpcs", Pr);
            Assert(items.Count == 1, $"one item; got {items.Count}");
            Assert(items[0].ItemName == "BEARING, DEEP GROOVE, 6205", $"commas stay in the name; got '{items[0].ItemName}'");
            Assert(items[0].Quantity == 10m, $"got {items[0].Quantity}");
        }

        /// <summary>A genuine multi-row paste with no quotes still becomes one item per row.</summary>
        private static void PlainMultiRowStillFansOut()
        {
            var items = ClipboardItemParser.ParsePrItems("Dell Monitor U2424\nHDMI Cable 2m\nUSB-C Dock", Pr);
            Assert(items.Count == 3, $"three rows, three items; got {items.Count}");
            Assert(items[1].ItemName == "HDMI Cable 2m", $"got '{items[1].ItemName}'");
        }

        /// <summary>An unquoted tab row keeps name / qty / unit in their columns.</summary>
        private static void TabRowKeepsColumns()
        {
            var items = ClipboardItemParser.ParsePrItems("Gasket Spiral Wound\t4\tset\n", Pr);
            Assert(items.Count == 1 && items[0].ItemName == "Gasket Spiral Wound" && items[0].Quantity == 4m && items[0].Unit == "set",
                $"got name='{items[0].ItemName}' qty={items[0].Quantity} unit='{items[0].Unit}'");
        }

        /// <summary>"" inside a quoted cell is one literal quote.</summary>
        private static void EscapedQuotesUnescape()
        {
            var items = ClipboardItemParser.ParsePrItems("\"VALVE 2\"\" GATE CL150\"\t2\tea", Pr);
            Assert(items.Count == 1, $"got {items.Count}");
            Assert(items[0].ItemName == "VALVE 2\" GATE CL150", $"got '{items[0].ItemName}'");
        }

        /// <summary>A header line above the data is still recognised and dropped.</summary>
        private static void HeaderRowStillSkipped()
        {
            var items = ClipboardItemParser.ParsePrItems("Item\tQty\tUnit\n\"multi\r\nline\"\t5\tea", Pr);
            Assert(items.Count == 1, $"header dropped, one data row; got {items.Count}");
            Assert(items[0].ItemName == "multi\r\nline", $"got '{items[0].ItemName}'");
        }

        private static void LooksLikeGridClassifies()
        {
            Assert(!ClipboardItemParser.LooksLikeGrid("\"BOLT HD\r\nM12 x 40\""), "a quoted multi-line cell is not a grid");
            Assert(!ClipboardItemParser.LooksLikeGrid("VALVE, GATE 150LB"), "a name with a comma is not a grid");
            Assert(!ClipboardItemParser.LooksLikeGrid("Ball Valve"), "a plain word is not a grid");
            Assert(ClipboardItemParser.LooksLikeGrid("Widget\t5\tpcs"), "one tab-separated row is a grid");
            Assert(ClipboardItemParser.LooksLikeGrid("a\nb\nc"), "several rows are a grid");
        }

        private static void NormalizeSingleNameUnwraps()
        {
            Assert(ClipboardItemParser.NormalizeSingleName("\"BOLT, HEX\r\nM12\"") == "BOLT, HEX\r\nM12", "strips the wrapper, keeps the newline");
            Assert(ClipboardItemParser.NormalizeSingleName("VALVE, GATE 150LB") == "VALVE, GATE 150LB", "leaves an unquoted name alone");
        }

        private static void BatchEntryMultiLineDescription()
        {
            var rows = ClipboardItemParser.ParseBatchPrEntries(
                "\"Consolidated Master\r\nRequisition Q3\"\tJ. Okafor\r\n",
                defaultRequestor: "fallback", defaultPriority: ProcurementPriority.Normal, defaultNotes: "");
            Assert(rows.Count == 1, $"one PR row, not one per visual line; got {rows.Count}");
            Assert(rows[0].Description == "Consolidated Master\r\nRequisition Q3", $"got '{rows[0].Description}'");
            Assert(rows[0].Requestor == "J. Okafor", $"got '{rows[0].Requestor}'");
        }

        private static void Assert(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException("ClipboardItemParser: " + what);
        }
    }
}
