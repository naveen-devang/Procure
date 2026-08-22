using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Procure.Models;

namespace Procure.Utilities
{
    public static class ClipboardItemParser
    {
        // Common header words in Excel exports to ignore if the first row is headers
        private static readonly HashSet<string> HeaderKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "item", "item name", "item description", "description", "name", "title", "requisition", "requisition title", "pr", "pr no", "pr number",
            "requestor", "requested by", "priority", "status", "department", "category",
            "qty", "quantity", "unit", "uom", "price", "unit price", "estimated price", "est price", "rate", "cost", "total", "amount", "notes", "remarks", "specification", "specs"
        };

        // Regex patterns for smart single-column quantity extraction
        // e.g. "5x Dell Monitor" or "5 * Dell Monitor"
        private static readonly Regex PrefixQtyRegex = new(
            @"^(?<qty>\d+(?:\.\d+)?)\s*(?:x|\*)\s+(?<name>.+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // e.g. "Dell Monitor x 5 pcs" or "Dell Monitor - 5 pcs" or "Dell Monitor * 5"
        private static readonly Regex SuffixQtyRegex = new(
            @"^(?<name>.+?)\s*(?:x|\*|\-|\:)\s*(?<qty>\d+(?:\.\d+)?)\s*(?<unit>[a-zA-Z]+)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // e.g. "Dell Monitor (5 pcs)" or "Dell Monitor [5 units]"
        private static readonly Regex ParenthesisQtyRegex = new(
            @"^(?<name>.+?)\s*[\(\[](?<qty>\d+(?:\.\d+)?)\s*(?<unit>[a-zA-Z]+)?[\)\]]$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Parses clipboard text (copied from Excel, Google Sheets, CSV, or raw text) into a list of PrItem domain models.
        /// </summary>
        public static List<PrItem> ParsePrItems(string? clipboardText, Guid prId, int startingSortOrder = 0)
        {
            var result = new List<PrItem>();
            if (string.IsNullOrWhiteSpace(clipboardText))
                return result;

            var rawLines = clipboardText
                .Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (rawLines.Count == 0)
                return result;

            int startIndex = 0;
            // Check if first line is a header row
            if (IsHeaderRow(rawLines[0]))
            {
                startIndex = 1;
            }

            int sortOrder = startingSortOrder;
            for (int i = startIndex; i < rawLines.Count; i++)
            {
                var line = rawLines[i];
                var item = ParseSingleItemLine(line, prId, sortOrder++);
                if (item != null && !string.IsNullOrWhiteSpace(item.ItemName))
                {
                    result.Add(item);
                }
            }

            return result;
        }

        /// <summary>
        /// Parses clipboard text into a list of quantity values (with optional extracted units) for single-column quantity pasting.
        /// </summary>
        public static List<(decimal Quantity, string? Unit)> ParseQuantities(string? clipboardText)
        {
            var result = new List<(decimal Quantity, string? Unit)>();
            if (string.IsNullOrWhiteSpace(clipboardText))
                return result;

            var rawLines = clipboardText
                .Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (rawLines.Count == 0)
                return result;

            int startIndex = 0;
            if (IsQuantityHeaderRow(rawLines[0]))
            {
                startIndex = 1;
            }

            for (int i = startIndex; i < rawLines.Count; i++)
            {
                var line = rawLines[i];
                var cols = SplitColumns(line);
                var valStr = cols[0];
                var qty = ParseQuantity(valStr, out string? unit);
                result.Add((qty <= 0 ? 1 : qty, unit));
            }

            return result;
        }

        /// <summary>
        /// Parses clipboard text into a list of unit strings for single-column UoM pasting.
        /// </summary>
        public static List<string> ParseUnits(string? clipboardText)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(clipboardText))
                return result;

            var rawLines = clipboardText
                .Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (rawLines.Count == 0)
                return result;

            int startIndex = 0;
            if (IsUnitHeaderRow(rawLines[0]))
            {
                startIndex = 1;
            }

            for (int i = startIndex; i < rawLines.Count; i++)
            {
                var line = rawLines[i];
                var cols = SplitColumns(line);
                var unitStr = cols[0].Trim();
                if (!string.IsNullOrWhiteSpace(unitStr))
                {
                    result.Add(unitStr);
                }
            }

            return result;
        }

        private static bool IsQuantityHeaderRow(string line)
        {
            var clean = SplitColumns(line)[0].Trim().ToLowerInvariant();
            return clean is "qty" or "quantity" or "quantities" or "count" or "amount" or "number" or "units";
        }

        private static bool IsUnitHeaderRow(string line)
        {
            var clean = SplitColumns(line)[0].Trim().ToLowerInvariant();
            return clean is "unit" or "uom" or "unit of measure" or "units" or "measurement";
        }

        /// <summary>
        /// Parses clipboard text into multiple BatchPrEntry rows for the Bulk PR creation table.
        /// </summary>
        public static List<BatchPrEntry> ParseBatchPrEntries(
            string? clipboardText,
            string defaultRequestor,
            string defaultPriority,
            string defaultNotes,
            IReadOnlyList<CustomFieldValue>? sharedCustomValues = null,
            bool isPrNoFirst = false)
        {
            var result = new List<BatchPrEntry>();
            if (string.IsNullOrWhiteSpace(clipboardText))
                return result;

            var rawLines = clipboardText
                .Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (rawLines.Count == 0)
                return result;

            int startIndex = 0;
            if (IsHeaderRow(rawLines[0]))
            {
                startIndex = 1;
            }

            var seenPrNos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = startIndex; i < rawLines.Count; i++)
            {
                var line = rawLines[i];
                var entryId = Guid.NewGuid();

                var columns = SplitColumns(line);
                string prNo;
                string description;
                string requestor;
                string priority;
                string notes;

                if (isPrNoFirst || columns[0].StartsWith("PR-", StringComparison.OrdinalIgnoreCase) || columns[0].StartsWith("PR#", StringComparison.OrdinalIgnoreCase) || columns[0].StartsWith("PR ", StringComparison.OrdinalIgnoreCase))
                {
                    prNo = columns[0].Trim();
                    description = columns.Length > 1 ? columns[1].Trim() : string.Empty;
                    requestor = columns.Length > 2 && !string.IsNullOrWhiteSpace(columns[2]) ? columns[2].Trim() : defaultRequestor;
                    priority = columns.Length > 3 && !string.IsNullOrWhiteSpace(columns[3]) ? columns[3].Trim() : defaultPriority;
                    notes = columns.Length > 4 && !string.IsNullOrWhiteSpace(columns[4]) ? columns[4].Trim() : defaultNotes;
                }
                else
                {
                    prNo = string.Empty;
                    description = columns[0].Trim();
                    requestor = columns.Length > 1 && !string.IsNullOrWhiteSpace(columns[1]) ? columns[1].Trim() : defaultRequestor;
                    priority = columns.Length > 2 && !string.IsNullOrWhiteSpace(columns[2]) ? columns[2].Trim() : defaultPriority;
                    notes = columns.Length > 3 && !string.IsNullOrWhiteSpace(columns[3]) ? columns[3].Trim() : defaultNotes;
                }

                // If PR Number was explicitly specified, deduplicate within clipboard lines
                if (isPrNoFirst || columns[0].StartsWith("PR-", StringComparison.OrdinalIgnoreCase) || columns[0].StartsWith("PR#", StringComparison.OrdinalIgnoreCase) || columns[0].StartsWith("PR ", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(prNo))
                    {
                        if (seenPrNos.Contains(prNo))
                        {
                            continue; // Skip duplicate PR Number within pasted clipboard batch
                        }
                        seenPrNos.Add(prNo);
                    }
                }

                // Clone custom values
                var rowCustomValues = new ObservableCollection<CustomFieldValue>();
                if (sharedCustomValues != null)
                {
                    foreach (var cv in sharedCustomValues)
                    {
                        rowCustomValues.Add(new CustomFieldValue
                        {
                            Id = Guid.NewGuid(),
                            PrId = entryId,
                            ColumnId = cv.ColumnId,
                            ColumnName = cv.ColumnName,
                            ColumnDataType = cv.ColumnDataType,
                            SelectOptions = cv.SelectOptions,
                            Value = cv.Value ?? string.Empty
                        });
                    }
                }

                // Initial line item matching the description
                var items = new ObservableCollection<PrItem>
                {
                    new PrItem
                    {
                        Id = Guid.NewGuid(),
                        PrId = entryId,
                        ItemName = description,
                        Quantity = 1,
                        Unit = "pcs",
                        SortOrder = 0
                    }
                };

                var entry = new BatchPrEntry
                {
                    Id = entryId,
                    PrNo = prNo,
                    Description = description,
                    Requestor = requestor,
                    Priority = priority,
                    Status = ProcurementStatus.PrRaised,
                    Notes = notes,
                    CustomValues = rowCustomValues,
                    Items = items
                };

                result.Add(entry);
            }

            return result;
        }

        private static PrItem? ParseSingleItemLine(string line, Guid prId, int sortOrder)
        {
            var columns = SplitColumns(line);

            if (columns.Length >= 2)
            {
                // Multi-column row (Tab or Comma separated from Excel)
                // Col 0: Item Name
                // Col 1: Quantity
                // Col 2: Unit (optional)
                // Col 3: Estimated Price (optional)
                // Col 4: Notes (optional)
                string itemName = columns[0].Trim();
                decimal quantity = ParseQuantity(columns[1], out string? unitFromQty);
                string unit = columns.Length > 2 && !string.IsNullOrWhiteSpace(columns[2])
                    ? columns[2].Trim()
                    : (!string.IsNullOrWhiteSpace(unitFromQty) ? unitFromQty : "pcs");

                decimal? price = null;
                if (columns.Length > 3 && !string.IsNullOrWhiteSpace(columns[3]))
                {
                    price = ParsePrice(columns[3]);
                }

                string notes = columns.Length > 4 ? columns[4].Trim() : string.Empty;

                return new PrItem
                {
                    Id = Guid.NewGuid(),
                    PrId = prId,
                    ItemName = itemName,
                    Quantity = quantity <= 0 ? 1 : quantity,
                    Unit = string.IsNullOrWhiteSpace(unit) ? "pcs" : unit,
                    EstimatedUnitPrice = price,
                    Notes = notes,
                    SortOrder = sortOrder
                };
            }
            else
            {
                // Single column: apply smart heuristics to extract quantity and unit if present
                string text = line.Trim();

                // Check "5x Dell Monitor"
                var match = PrefixQtyRegex.Match(text);
                if (match.Success)
                {
                    var qtyStr = match.Groups["qty"].Value;
                    var nameStr = match.Groups["name"].Value.Trim();
                    decimal.TryParse(qtyStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal q);

                    return new PrItem
                    {
                        Id = Guid.NewGuid(),
                        PrId = prId,
                        ItemName = nameStr,
                        Quantity = q <= 0 ? 1 : q,
                        Unit = "pcs",
                        SortOrder = sortOrder
                    };
                }

                // Check "Dell Monitor (5 pcs)" or "Dell Monitor [5 units]"
                match = ParenthesisQtyRegex.Match(text);
                if (match.Success)
                {
                    var nameStr = match.Groups["name"].Value.Trim();
                    var qtyStr = match.Groups["qty"].Value;
                    var unitStr = match.Groups["unit"].Value.Trim();
                    decimal.TryParse(qtyStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal q);

                    return new PrItem
                    {
                        Id = Guid.NewGuid(),
                        PrId = prId,
                        ItemName = nameStr,
                        Quantity = q <= 0 ? 1 : q,
                        Unit = string.IsNullOrWhiteSpace(unitStr) ? "pcs" : unitStr,
                        SortOrder = sortOrder
                    };
                }

                // Check "Dell Monitor x 5 pcs" or "Dell Monitor - 5 pcs"
                match = SuffixQtyRegex.Match(text);
                if (match.Success)
                {
                    var nameStr = match.Groups["name"].Value.Trim();
                    var qtyStr = match.Groups["qty"].Value;
                    var unitStr = match.Groups["unit"].Value.Trim();
                    decimal.TryParse(qtyStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal q);

                    return new PrItem
                    {
                        Id = Guid.NewGuid(),
                        PrId = prId,
                        ItemName = nameStr,
                        Quantity = q <= 0 ? 1 : q,
                        Unit = string.IsNullOrWhiteSpace(unitStr) ? "pcs" : unitStr,
                        SortOrder = sortOrder
                    };
                }

                // Standard single item name
                return new PrItem
                {
                    Id = Guid.NewGuid(),
                    PrId = prId,
                    ItemName = text,
                    Quantity = 1,
                    Unit = "pcs",
                    SortOrder = sortOrder
                };
            }
        }

        private static string[] SplitColumns(string line)
        {
            if (line.Contains('\t'))
            {
                return line.Split('\t');
            }

            if (line.Contains(',') && !line.StartsWith('"'))
            {
                return line.Split(',');
            }

            return new[] { line };
        }

        private static bool IsHeaderRow(string line)
        {
            var cols = SplitColumns(line);
            int matchCount = 0;
            foreach (var col in cols)
            {
                var clean = col.Trim();
                if (HeaderKeywords.Contains(clean))
                {
                    matchCount++;
                }
            }

            return matchCount > 0 && (matchCount >= 2 || cols.Length == 1 && HeaderKeywords.Contains(cols[0].Trim()));
        }

        private static decimal ParseQuantity(string raw, out string? extractedUnit)
        {
            extractedUnit = null;
            if (string.IsNullOrWhiteSpace(raw))
                return 1;

            var clean = raw.Trim();
            // Separate numbers from units if formatted like "5pcs" or "10 units"
            var match = Regex.Match(clean, @"^(?<num>\d+(?:\.\d+)?)\s*(?<unit>[a-zA-Z]+)?$");
            if (match.Success)
            {
                var numStr = match.Groups["num"].Value;
                extractedUnit = match.Groups["unit"].Value;
                if (decimal.TryParse(numStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal val))
                {
                    return val;
                }
            }

            if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.CurrentCulture, out decimal parsedVal) ||
                decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out parsedVal))
            {
                return parsedVal;
            }

            return 1;
        }

        private static readonly HashSet<string> PricingHeaderKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "price", "unit price", "quoted price", "quoted rate", "quoted unit price", "rate", "unit rate", "cost", "unit cost",
            "discount", "disc", "disc %", "discount %", "discount amount", "line discount",
            "last price", "last rate", "prev price", "previous price", "est price", "estimated price", "last unit price",
            "historical price", "historical rate", "hist price", "hist rate"
        };

        public static List<RfqPricingPasteRow> ParseRfqPricingData(string? clipboardText, RfqPricingColumn startCol)
        {
            var result = new List<RfqPricingPasteRow>();
            if (string.IsNullOrWhiteSpace(clipboardText))
                return result;

            var rawLines = clipboardText
                .Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (rawLines.Count == 0)
                return result;

            int startIndex = 0;
            if (IsPricingHeaderRow(rawLines[0]))
            {
                startIndex = 1;
            }

            for (int i = startIndex; i < rawLines.Count; i++)
            {
                var line = rawLines[i];
                var cols = SplitColumns(line);
                var row = new RfqPricingPasteRow();

                if (startCol == RfqPricingColumn.UnitPrice)
                {
                    if (cols.Length > 0)
                    {
                        row.UnitPrice = ParsePrice(cols[0]);
                        row.HasUnitPrice = true;
                    }
                    if (cols.Length > 1)
                    {
                        row.Discount = ParsePrice(cols[1]);
                        row.HasDiscount = true;
                    }
                    if (cols.Length > 2)
                    {
                        row.LastPrice = ParsePrice(cols[2]);
                        row.HasLastPrice = true;
                    }
                }
                else if (startCol == RfqPricingColumn.Discount)
                {
                    if (cols.Length > 0)
                    {
                        row.Discount = ParsePrice(cols[0]);
                        row.HasDiscount = true;
                    }
                    if (cols.Length > 1)
                    {
                        row.LastPrice = ParsePrice(cols[1]);
                        row.HasLastPrice = true;
                    }
                }
                else if (startCol == RfqPricingColumn.LastPrice)
                {
                    if (cols.Length > 0)
                    {
                        row.LastPrice = ParsePrice(cols[0]);
                        row.HasLastPrice = true;
                    }
                }

                result.Add(row);
            }

            return result;
        }

        private static bool IsPricingHeaderRow(string line)
        {
            var cols = SplitColumns(line);
            int matchCount = 0;
            foreach (var col in cols)
            {
                var clean = col.Trim();
                if (PricingHeaderKeywords.Contains(clean) || HeaderKeywords.Contains(clean))
                {
                    matchCount++;
                }
            }

            return matchCount > 0 && (matchCount >= 2 || (cols.Length == 1 && (PricingHeaderKeywords.Contains(cols[0].Trim()) || HeaderKeywords.Contains(cols[0].Trim()))));
        }

        private static decimal? ParsePrice(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            // Strip currency symbols and formatting (e.g. "$", "AED", "€", "£", ",", " ", "%")
            var sanitized = Regex.Replace(raw, @"[^\d\.\,\-]", "").Trim();
            if (string.IsNullOrWhiteSpace(sanitized))
                return null;

            // Handle comma thousands separators
            if (sanitized.Contains(',') && !sanitized.Contains('.'))
            {
                sanitized = sanitized.Replace(",", ".");
            }
            else if (sanitized.Contains(',') && sanitized.Contains('.'))
            {
                sanitized = sanitized.Replace(",", "");
            }

            if (decimal.TryParse(sanitized, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal result))
            {
                return result;
            }

            return null;
        }
    }

    public enum RfqPricingColumn
    {
        UnitPrice = 0,
        Discount = 1,
        LastPrice = 2
    }

    public sealed class RfqPricingPasteRow
    {
        public decimal? UnitPrice { get; set; }
        public decimal? Discount { get; set; }
        public decimal? LastPrice { get; set; }
        public bool HasUnitPrice { get; set; }
        public bool HasDiscount { get; set; }
        public bool HasLastPrice { get; set; }
    }
}
