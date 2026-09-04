using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Procure.Models;

namespace Procure.Services.Export
{
    public static class PcrPdfExporter
    {
        private class PdfPage
        {
            public StringBuilder Stream { get; } = new();

            // NaN means "not yet recorded" - auto-scale lets curY run well below zero for a long
            // sheet (the whole point is drawing past where a normal page would have broken), so a
            // plain "> 0" unset-check was silently skipping every column line once that happened.
            public double TableTopY { get; set; } = double.NaN;
            public double TableBottomY { get; set; } = double.NaN;

            // The band this page's Qty|Unit Price divider spans: from the top of the "Qty / Unit
            // Price" sub-header row (NOT TableTopY, which is the top of the merged vendor-name row
            // above it) down to where the item rows end. Past that bottom a vendor's total sits in
            // a visually undivided span, the same merged look Excel's totals row gets from a real
            // cell merge. Both are per-page: every page draws its own header, and every page that
            // carries item rows draws the divider through them - recording only the last page's
            // bottom left page 1 with no divider at all while page 2 drew one straight through its
            // vendor names.
            public double MinorDividerTopY { get; set; } = double.NaN;
            public double ItemRowsBottomY { get; set; } = double.NaN;
        }

        // 3pt left inset + 4pt right inset + a 6pt gap so the currency and the amount never end up
        // shoulder to shoulder. Shared by the width budget and the draw-time fit check, which have
        // to agree or a column sized to the exact figure still triggers a shrink.
        private const double MoneyCellPad = 13;

        public static byte[] GeneratePdf(
            PurchaseRequisition pr,
            PriceComparisonRequest pcr,
            IReadOnlyList<RequestForQuotation> selectedRfqs,
            string remarks,
            PcrPdfOptions? options = null)
        {
            options ??= new PcrPdfOptions();
            bool shrink = options.LayoutMode == PdfLayoutMode.ShrinkToFit;

            // Base size is always expressed landscape (width > height); portrait swaps the two.
            // ISO sizes converted from mm at 72/25.4 pt/mm; US sizes from inches at 72 pt/in.
            var (baseWidth, baseHeight) = options.PaperSize switch
            {
                PdfPaperSize.A0 => (3370.0, 2384.0),
                PdfPaperSize.A1 => (2384.0, 1684.0),
                PdfPaperSize.A2 => (1684.0, 1191.0),
                PdfPaperSize.A3 => (1191.0, 842.0),
                PdfPaperSize.A5 => (595.0, 420.0),
                PdfPaperSize.A6 => (420.0, 298.0),
                PdfPaperSize.Letter => (792.0, 612.0),
                PdfPaperSize.Legal => (1008.0, 612.0),
                PdfPaperSize.Tabloid => (1224.0, 792.0),
                PdfPaperSize.Executive => (756.0, 522.0),
                _ => (842.0, 595.0) // A4
            };
            bool portrait = options.Orientation == PdfOrientation.Portrait;
            double pageWidth = portrait ? baseHeight : baseWidth;
            double pageHeight = portrait ? baseWidth : baseHeight;

            double margin = options.MarginPreset switch
            {
                PdfMarginPreset.Narrow => 18,
                PdfMarginPreset.Wide => 54,
                _ => 36 // Normal
            };
            double marginLeft = margin;
            double marginRight = margin;
            double marginTop = margin;
            double contentWidth = pageWidth - marginLeft - marginRight;
            double bottomLimit = margin;

            // Shrink-to-fit tightens every gap and row height below so a short comparison sheet has a
            // real chance of landing on one page instead of spilling a near-empty footer onto its own
            // page: the totals block (12 summary rows + remarks + signature boxes) is ~270-285pt tall,
            // more than half a landscape page, so the un-tightened layout only ever fits ~10-item lists.
            double titleGap = shrink ? 18 : 22;
            double metaLineGap = shrink ? 11 : 14;
            double metaGapAfter = shrink ? 13 : 18;
            double continuationHeaderGap = shrink ? 13 : 16;
            double tableHeaderRowH1 = shrink ? 17 : 20;
            double tableHeaderRowH2 = shrink ? 11 : 14;
            double itemRowH = shrink ? 17 : 20;
            double summaryRowH = shrink ? 11.5 : 13.5;
            double afterTableGap = shrink ? 8 : 14;
            double remarksGap = shrink ? 14 : 18;
            double signatureBoxHeight = shrink ? 58 : 75;

            int supplierCount = selectedRfqs.Count;
            const double slNoWidth = 30;

            // Not const any more: both can be widened below out of the description column's slack
            // when their own content genuinely doesn't fit (see the borrow pass).
            double qtyWidth = 45;

            // Fetched early (normally built alongside the item-render loop further down) purely so
            // its item names can drive the description column's width below - content-driven, not
            // just "how many vendor columns are there".
            var prItems = pr.Items?.ToList() ?? new List<PrItem>();

            // The description column only grows as wide as its own longest item name actually needs
            // - not simply however much space fewer vendor columns leave spare, which used to hand
            // "GYPSUM" the same oversized column as a genuinely long description. Still bounded both
            // ways: never smaller than the old fixed 170pt, and never wide enough to crowd vendor/
            // price columns below a readable floor - a long vendor name forced into a too-narrow
            // column would wrap 5-6 lines deep, which is worse than a slightly-under-ideal
            // description column.
            const double descWidthFloor = 170;
            const double descWidthCap = 300;
            const double vendorPairWidthFloor = 95; // floor for one vendor's Qty+Price pair together
            const double descPadding = 16;

            // Historical Price never carries a Qty column - there's no vendor's own quantity for it to
            // show, just the same one reference number every row already had - so it's fixed narrower
            // than a vendor's pair instead of scaling with supplier count the way it used to.
            double historicalWidth = 65;

            double availableForDescAndVendors = contentWidth - slNoWidth - qtyWidth - historicalWidth;
            double maxDescWidthByVendorFloor = availableForDescAndVendors - (supplierCount * vendorPairWidthFloor);

            double widestItemTextWidth = prItems.Count > 0
                ? prItems.Max(item => MeasureTextWidth(item.ItemName, "F1", 7.5))
                : 0;
            double idealDescWidth = widestItemTextWidth + descPadding;
            double effectiveDescCap = Math.Max(descWidthFloor, Math.Min(descWidthCap, maxDescWidthByVendorFloor));
            double descWidth = Math.Clamp(idealDescWidth, descWidthFloor, effectiveDescCap);

            double remainingWidth = availableForDescAndVendors - descWidth;
            // A vendor's own quoted quantity ("2800 MT") is always shorter than its money amount
            // ("AED 1,102,500.00"), so the pair splits unevenly - same proportions the Excel version
            // uses (10-wide Qty column against an 18-wide Unit Price column there).
            double basePairWidth = supplierCount > 0 ? remainingWidth / supplierCount : remainingWidth;

            // Per-vendor from here on, not one shared figure: the borrow pass below widens only the
            // columns whose own values don't fit, and leaves every other vendor exactly as it was.
            var vendorQtyW = new double[supplierCount];
            var vendorPriceW = new double[supplierCount];
            for (int i = 0; i < supplierCount; i++)
            {
                vendorQtyW[i] = basePairWidth * 0.4;
                vendorPriceW[i] = basePairWidth * 0.6;
            }
            double VendorPairW(int i) => vendorQtyW[i] + vendorPriceW[i];

            // Base currency for the Historical Price column.
            var defaultCurrency = string.IsNullOrWhiteSpace(selectedRfqs.FirstOrDefault()?.Currency) ? "AED" : selectedRfqs.First().Currency.Trim();

            int VendorQtyColIdx(int i) => 3 + (i * 2);
            int VendorPriceColIdx(int i) => 3 + (i * 2) + 1;
            int HistoricalColIdx() => 3 + (supplierCount * 2);

            // Which RfqItem backs each (item, vendor) cell. Resolved once here for the measuring
            // pass and reused by the render loop, rather than matching twice with the same rules.
            var matchedRfqItems = new RfqItem?[Math.Max(prItems.Count, 1), Math.Max(supplierCount, 1)];
            for (int p = 0; p < prItems.Count; p++)
            {
                for (int i = 0; i < supplierCount; i++)
                {
                    var rf = selectedRfqs[i];
                    // Exact PrItemId link wins; name matching only covers unlinked lines.
                    matchedRfqItems[p, i] =
                        rf.Items?.FirstOrDefault(ri => ri.PrItemId.HasValue && ri.PrItemId.Value == prItems[p].Id)
                        ?? rf.Items?.FirstOrDefault(ri => !ri.PrItemId.HasValue && string.Equals(ri.ItemName, prItems[p].ItemName, StringComparison.OrdinalIgnoreCase));
                }
            }

            // ---- Borrow pass -------------------------------------------------------------------
            // Every column is a fixed slice decided before a single value is measured, so a value
            // wider than its slice used to just overflow: a money cell pins the currency to its left
            // edge and the amount to its right, so a long amount collided with "AED" in the middle,
            // and an over-wide quantity spilled into the next column. Description is the only column
            // with real slack (it wraps to two lines), so it donates - exactly the shortfall, only to
            // the columns actually short, and only when there is one. On roomier paper (A3 and up)
            // nothing is ever short, so this pass computes a zero deficit and changes nothing.
            {
                const double cellPad = 7;   // 3pt left inset + 4pt right inset, as the cells draw
                string Cur(RequestForQuotation rf) => string.IsNullOrWhiteSpace(rf.Currency) ? "AED" : rf.Currency.Trim();

                // MoneyCellPad, not cellPad: the currency sits hard left and the amount hard right,
                // so budgeting only the two insets sizes the column to the width where they exactly
                // touch - "AED1,102,500.00". The extra is the gap that keeps them two words.
                double MoneyW(string cur, decimal amount, string font, double size)
                    => MoneyCellPad + MeasureTextWidth(cur, font, size)
                       + MeasureTextWidth(amount.ToString("N2", CultureInfo.InvariantCulture), font, size);

                var dQty = new double[supplierCount];
                var dPrice = new double[supplierCount];
                double dPrq = 0, dHist = 0;

                double prqNeed = 0, histNeed = 0;
                for (int p = 0; p < prItems.Count; p++)
                {
                    var item = prItems[p];
                    prqNeed = Math.Max(prqNeed, cellPad + MeasureTextWidth(
                        $"{item.Quantity.ToString("G29", CultureInfo.InvariantCulture)} {item.Unit}", "F1", 7.5));

                    decimal rowLast = item.EstimatedUnitPrice ?? 0m;
                    for (int i = 0; i < supplierCount; i++)
                    {
                        var ri = matchedRfqItems[p, i];
                        if (ri?.LastPrice > 0) { rowLast = ri.LastPrice.Value; break; }
                    }
                    if (rowLast > 0) histNeed = Math.Max(histNeed, MoneyW(defaultCurrency, rowLast, "F1", 7.5));
                }
                dPrq = Math.Max(0, prqNeed - qtyWidth);
                dHist = Math.Max(0, histNeed - historicalWidth);

                for (int i = 0; i < supplierCount; i++)
                {
                    var rf = selectedRfqs[i];
                    var cur = Cur(rf);
                    double qtyNeed = 0, priceNeed = 0;

                    for (int p = 0; p < prItems.Count; p++)
                    {
                        var ri = matchedRfqItems[p, i];
                        if (ri?.QuotedUnitPrice is not > 0) continue;
                        qtyNeed = Math.Max(qtyNeed, cellPad + MeasureTextWidth(ri.FormattedQuantity, "F1", 7));
                        var net = Math.Max(0m, ri.QuotedUnitPrice.Value - (ri.Discount ?? 0m));
                        priceNeed = Math.Max(priceNeed, MoneyW(cur, net, "F1", 7.5));
                    }

                    dQty[i] = Math.Max(0, qtyNeed - vendorQtyW[i]);
                    dPrice[i] = Math.Max(0, priceNeed - vendorPriceW[i]);

                    // The summary rows draw across the vendor's whole pair, so they constrain the
                    // pair rather than either half; any extra goes to the money side.
                    var baseAmt = rf.BaseAmount > 0 ? rf.BaseAmount : (rf.QuoteAmount ?? 0m);
                    double pairNeed = 0;
                    foreach (var amt in new[] { baseAmt, rf.Discount ?? 0m, baseAmt - (rf.Discount ?? 0m),
                                                rf.Freight ?? 0m, rf.OtherCharges ?? 0m, rf.TotalLandedCost })
                    {
                        pairNeed = Math.Max(pairNeed, MoneyW(cur, amt, "F2", 7.5));
                    }
                    foreach (var text in new[] {
                        string.IsNullOrWhiteSpace(rf.VatType) ? "5%" : rf.VatType,
                        string.IsNullOrWhiteSpace(rf.PaymentTerms) ? "30 Days Net" : rf.PaymentTerms,
                        string.IsNullOrWhiteSpace(rf.Incoterms) ? "DDP" : rf.Incoterms,
                        string.IsNullOrWhiteSpace(rf.DeliveryLeadTime) ? "-" : rf.DeliveryLeadTime,
                        string.IsNullOrWhiteSpace(rf.Warranty) ? "-" : rf.Warranty,
                        string.IsNullOrWhiteSpace(rf.TechnicalApproval) ? "-" : rf.TechnicalApproval })
                    {
                        pairNeed = Math.Max(pairNeed, cellPad + MeasureTextWidth(text, "F1", 7.5));
                    }

                    var pairHave = vendorQtyW[i] + dQty[i] + vendorPriceW[i] + dPrice[i];
                    if (pairNeed > pairHave) dPrice[i] += pairNeed - pairHave;
                }

                double totalDeficit = dQty.Sum() + dPrice.Sum() + dPrq + dHist;
                if (totalDeficit > 0)
                {
                    // How far description can be squeezed. Not an arbitrary number: the summary row
                    // labels are drawn inside this column unclipped, so crushing it past the widest
                    // one ("Total Price Excl. VAT After Discount") pushes that label out into the PR
                    // Quantity column.
                    double labelFloor = 8 + new[]
                    {
                        "Total Price Excl. VAT", "Discount", "Total Price Excl. VAT After Discount",
                        "Freight/Shipping Charges", "Other Charges", "VAT", "Total Price Incl. VAT",
                        "Payment Terms", "Delivery Terms", "Lead Time (Days)", "Warranty",
                        "Technical Approval"
                    }.Max(l => MeasureTextWidth(l, "F2", 7.5));

                    double take = Math.Min(totalDeficit, Math.Max(0, descWidth - labelFloor));
                    if (take > 0)
                    {
                        // Enough to go round: everyone short gets exactly their shortfall. Not
                        // enough: everyone gets the same fraction of it, and the leftover falls to
                        // the per-cell shrink at draw time.
                        double f = take / totalDeficit;
                        double granted = 0;
                        for (int i = 0; i < supplierCount; i++)
                        {
                            vendorQtyW[i] += dQty[i] * f; granted += dQty[i] * f;
                            vendorPriceW[i] += dPrice[i] * f; granted += dPrice[i] * f;
                        }
                        qtyWidth += dPrq * f; granted += dPrq * f;
                        historicalWidth += dHist * f; granted += dHist * f;
                        descWidth -= granted;
                    }
                }
            }

            // colX[3] IS vendor 0's Qty column start already (the last entry of the literal above) -
            // each loop pass below only needs to append the TWO boundaries after that: this vendor's
            // Price start, then the next vendor's Qty start (or Historical's start, on the last pass).
            // Appending a third "Qty start" up front here once duplicated colX[3], which pushed every
            // later boundary one index past what VendorQtyColIdx/VendorPriceColIdx/HistoricalColIdx
            // expect - the actual cause of the garbled, overlapping export, not a width/tightness bug.
            var colX = new List<double> { marginLeft, marginLeft + slNoWidth, marginLeft + slNoWidth + descWidth, marginLeft + slNoWidth + descWidth + qtyWidth };
            {
                double x = colX[3];
                for (int i = 0; i < supplierCount; i++)
                {
                    colX.Add(x + vendorQtyW[i]);        // this vendor's Unit Price column start
                    x += VendorPairW(i);
                    colX.Add(x);                        // next vendor's Qty start, or Historical's start after the last vendor
                }
                colX.Add(x + historicalWidth);          // right edge
            }

            var plantCode = string.IsNullOrWhiteSpace(pr.Plant) ? "RW01" : pr.Plant.Trim();
            var cleanRfqList = selectedRfqs
                .Select(rf => rf.RfqNo.Replace("RFQ-", "").Trim())
                .Where(num => !string.IsNullOrWhiteSpace(num))
                .ToList();
            var rfqNumsDisplay = cleanRfqList.Count > 0 ? string.Join("/", cleanRfqList) : "-";
            var collectiveNo = string.IsNullOrWhiteSpace(pcr.PcrNo) ? "-" : pcr.PcrNo;
            var dateStr = DateTime.Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

            var pages = new List<PdfPage>();
            PdfPage currentPage = null!;
            double curY = 0;

            void DrawLine(double x1, double y1, double x2, double y2, double width = 0.5, string strokeHex = "000000")
            {
                var (r, g, b) = HexToRgb(strokeHex);
                currentPage.Stream.AppendLine($"{r:F3} {g:F3} {b:F3} RG");
                currentPage.Stream.AppendLine($"{width:F2} w");
                currentPage.Stream.AppendLine($"{x1:F2} {y1:F2} m {x2:F2} {y2:F2} l S");
            }

            void DrawRect(double x, double y, double w, double h, double lineWidth = 0.5, string? fillHex = null, string strokeHex = "000000")
            {
                if (fillHex != null)
                {
                    var (fr, fg, fb) = HexToRgb(fillHex);
                    currentPage.Stream.AppendLine($"{fr:F3} {fg:F3} {fb:F3} rg");
                }
                var (sr, sg, sb) = HexToRgb(strokeHex);
                currentPage.Stream.AppendLine($"{sr:F3} {sg:F3} {sb:F3} RG");
                currentPage.Stream.AppendLine($"{lineWidth:F2} w");
                currentPage.Stream.AppendLine($"{x:F2} {y:F2} {w:F2} {h:F2} re {(fillHex != null ? "B" : "S")}");
            }

            void DrawText(string text, double x, double y, string font = "F1", double fontSize = 9, string align = "left", double width = 0, string colorHex = "000000")
            {
                if (string.IsNullOrEmpty(text)) return;
                var (r, g, b) = HexToRgb(colorHex);
                var safeText = EscapePdfText(text);

                double approxWidth = MeasureTextWidth(text, font, fontSize);

                double finalX = x;
                if (align == "center" && width > 0)
                {
                    finalX = x + Math.Max(0, (width - approxWidth) / 2.0);
                }
                else if (align == "right" && width > 0)
                {
                    finalX = x + Math.Max(0, width - approxWidth - 4);
                }

                currentPage.Stream.AppendLine("BT");
                currentPage.Stream.AppendLine($"/{font} {fontSize:F1} Tf");
                currentPage.Stream.AppendLine($"{r:F3} {g:F3} {b:F3} rg");
                currentPage.Stream.AppendLine($"{finalX:F2} {y:F2} Td");
                currentPage.Stream.AppendLine($"({safeText}) Tj");
                currentPage.Stream.AppendLine("ET");
            }

            void DrawFittedText(string text, double x, double y, string font = "F1", double baseFontSize = 8.5, string align = "left", double maxWidth = 350, string colorHex = "000000", double minFontSize = 6.5)
            {
                if (string.IsNullOrEmpty(text)) return;
                double approxWidth = MeasureTextWidth(text, font, baseFontSize);

                double finalFontSize = baseFontSize;
                if (approxWidth > maxWidth && maxWidth > 0)
                {
                    finalFontSize = Math.Max(minFontSize, baseFontSize * (maxWidth / approxWidth));
                }

                DrawText(text, x, y, font: font, fontSize: finalFontSize, align: align, width: maxWidth, colorHex: colorHex);
            }

            // Greedy wrap against the column's real width (via the exporter's own
            // MeasureTextWidth), capped at maxLines. Breaks after a space OR after ; , : - so a
            // compound token with no spaces at all (e.g. "TUBE;PN:153032,M:QS-6,PBT,6MM,6MM") still
            // has real places to wrap, the way Word treats punctuation as a line-break opportunity
            // even without whitespace - rather than one bare word landing alone on a line while
            // everything else piles onto the next. A chunk with no break point at all (rare: truly
            // no spaces or punctuation) still renders whole on its own line - never split mid-word.
            List<string> WrapText(string text, string font, double fontSize, double maxWidth, int maxLines, bool truncate = true)
            {
                if (string.IsNullOrEmpty(text)) return new List<string> { string.Empty };

                var chunks = System.Text.RegularExpressions.Regex.Matches(text, @"[^\s;,:\-]*[\s;,:\-]+|[^\s;,:\-]+$")
                    .Select(m => m.Value)
                    .Where(c => c.Length > 0)
                    .ToList();

                var lines = new List<string>();
                var current = string.Empty;

                foreach (var chunk in chunks)
                {
                    var candidate = current + chunk;
                    if (current.Length == 0 || MeasureTextWidth(candidate.TrimEnd(), font, fontSize) <= maxWidth)
                    {
                        current = candidate;
                    }
                    else
                    {
                        lines.Add(current.TrimEnd());
                        current = chunk;
                    }
                }
                if (current.Length > 0) lines.Add(current.TrimEnd());
                if (lines.Count == 0) lines.Add(string.Empty);

                // Non-truncating mode (long free text like remarks): keep every line, and hard-split
                // any single line still wider than the column - a run with no space or punctuation
                // (e.g. "aaaaaaaa...") has no break opportunity, so it must break mid-run or overrun.
                if (!truncate)
                {
                    var wrapped = new List<string>();
                    foreach (var raw in lines)
                    {
                        var rem = raw;
                        while (MeasureTextWidth(rem, font, fontSize) > maxWidth && rem.Length > 1)
                        {
                            int cut = rem.Length;
                            while (cut > 1 && MeasureTextWidth(rem[..cut], font, fontSize) > maxWidth) cut--;
                            wrapped.Add(rem[..cut]);
                            rem = rem[cut..];
                        }
                        wrapped.Add(rem);
                    }
                    return wrapped;
                }

                // Wrapping the whole string needed more lines than the cap allows, or the single
                // word left on the capped line is itself still wider than the column - either way
                // content got cut, so the last visible line needs an ellipsis.
                var needsEllipsis = lines.Count > maxLines
                    || MeasureTextWidth(lines[Math.Min(lines.Count, maxLines) - 1], font, fontSize) > maxWidth;

                if (lines.Count > maxLines) lines = lines.Take(maxLines).ToList();

                if (needsEllipsis)
                {
                    var last = lines[^1];
                    while (last.Length > 0 && MeasureTextWidth(last + "..", font, fontSize) > maxWidth)
                    {
                        last = last[..^1];
                    }
                    lines[^1] = last.TrimEnd() + "..";
                }

                return lines;
            }

            // Draws a small block of already-wrapped lines vertically centered within a row band
            // that runs from y (top) down by rowHeight - so a 1-line neighbor in the same header
            // row still reads as centered once the row grows to fit someone else's 2-line name.
            void DrawCenteredBlock(List<string> lines, double x, double topY, double rowHeight, double lineHeight, string font, double fontSize, double width, string align = "center")
            {
                var blockHeight = lines.Count * lineHeight;
                var firstBaselineY = topY - ((rowHeight - blockHeight) / 2.0) - (lineHeight * 0.75);
                for (int i = 0; i < lines.Count; i++)
                {
                    DrawText(lines[i], x, firstBaselineY - (i * lineHeight), font: font, fontSize: fontSize, align: align, width: width);
                }
            }

            void DrawMoneyCell(double cellX, double cellWidth, double textY, string currency, decimal? amount, bool isBold = false, double fontSize = 7.5, bool showZeroAsDash = false)
            {
                if (amount.HasValue && (!showZeroAsDash || amount.Value > 0))
                {
                    var font = isBold ? "F2" : "F1";
                    var amtStr = amount.Value.ToString("N2", CultureInfo.InvariantCulture);

                    // Currency is pinned left and the amount right, so a pair too wide for the cell
                    // used to collide in the middle. The borrow pass above already widened this
                    // column as far as the description could fund; whatever is still missing comes
                    // off the type size here, and past the floor the currency label is dropped
                    // rather than allowed to overlap the figure it belongs to.
                    var textW = MeasureTextWidth(currency, font, fontSize) + MeasureTextWidth(amtStr, font, fontSize);
                    var showCurrency = true;
                    if (MoneyCellPad + textW > cellWidth && cellWidth > MoneyCellPad)
                    {
                        // Solve for the size that fits rather than scaling by width/needed: the
                        // padding is fixed and doesn't shrink with the type, so the naive ratio
                        // always lands slightly over and the cell drops its currency for nothing.
                        fontSize = Math.Max(5.5, fontSize * (cellWidth - MoneyCellPad) / textW);
                        // Epsilon, not a bare ">": a cell sized to exactly this figure comes back a
                        // rounding step over its own width and drops the currency for nothing.
                        if (MoneyCellPad + MeasureTextWidth(currency, font, fontSize) + MeasureTextWidth(amtStr, font, fontSize) > cellWidth + 0.05)
                        {
                            // Floored and still over: better to lose the currency label, which the
                            // column heading and every other row still carry, than to print it on
                            // top of the figure.
                            showCurrency = false;
                        }
                    }

                    if (showCurrency) DrawText(currency, cellX + 3, textY, font: font, fontSize: fontSize, align: "left");
                    DrawFittedText(amtStr, cellX, textY, font: font, baseFontSize: fontSize, align: "right", maxWidth: cellWidth, minFontSize: 5.0);
                }
                else
                {
                    DrawText("-", cellX, textY, font: isBold ? "F2" : "F1", fontSize: fontSize, align: "center", width: cellWidth);
                }
            }

            void CloseCurrentPageTable()
            {
                if (currentPage != null && !double.IsNaN(currentPage.TableTopY) && !double.IsNaN(currentPage.TableBottomY))
                {
                    // The line between a vendor's own Qty and Unit Price columns (the "minor" divider
                    // inside one pair) only runs through the item rows - past that, a vendor's total
                    // spans its whole pair with nothing drawn through it. Every other line (pair
                    // boundaries, Historical, the outer edges) runs the full table height as before.
                    var minorDividerCols = new HashSet<int>();
                    for (int i = 0; i < supplierCount; i++) minorDividerCols.Add(VendorPriceColIdx(i));

                    var minorTop = double.IsNaN(currentPage.MinorDividerTopY) ? currentPage.TableTopY : currentPage.MinorDividerTopY;
                    var minorBottom = double.IsNaN(currentPage.ItemRowsBottomY) ? minorTop : currentPage.ItemRowsBottomY;

                    for (int i = 1; i < colX.Count; i++)
                    {
                        var isMinor = minorDividerCols.Contains(i);
                        var top = isMinor ? minorTop : currentPage.TableTopY;
                        var bottom = isMinor ? minorBottom : currentPage.TableBottomY;
                        if (bottom < top)
                        {
                            DrawLine(colX[i], bottom, colX[i], top, width: 0.5);
                        }
                    }
                }
            }

            // Vendor/Historical header wrap - measured against each column's real width, so the
            // header row only grows when a name genuinely needs a second line; everyone else's
            // single line still ends up centered once DrawCenteredBlock sees the taller row.
            // No "(Partial)" marker here - an unquoted item already prints "-" in that vendor's
            // own price cell, so flagging it again on the header would be redundant.
            const double vendorHeaderLineHeight = 9.0;
            var vendorHeaderLines = new List<List<string>>();
            for (int i = 0; i < supplierCount; i++)
            {
                // Vendor name spans the whole pair (Qty + Unit Price), same as the merged header
                // cell the Excel version already draws.
                vendorHeaderLines.Add(WrapText(selectedRfqs[i].Vendor, "F2", 7.5, VendorPairW(i) - 6, maxLines: 2));
            }
            var historicalHeaderLines = WrapText("Historical Price", "F2", 7.5, historicalWidth - 6, maxLines: 2);

            var maxHeaderLines = Math.Max(
                vendorHeaderLines.Select(l => l.Count).DefaultIfEmpty(1).Max(),
                historicalHeaderLines.Count);
            if (maxHeaderLines > 1)
            {
                tableHeaderRowH1 += (maxHeaderLines - 1) * vendorHeaderLineHeight;
            }

            void StartNewPage(bool isFirstPage)
            {
                CloseCurrentPageTable();

                currentPage = new PdfPage();
                pages.Add(currentPage);
                int pageIdx = pages.Count;

                // Top-right corner page number placeholder
                DrawText($"##PAGE_{pageIdx}_PLACEHOLDER##", marginLeft + contentWidth - 120, pageHeight - 25, font: "F1", fontSize: 8.5, align: "right", width: 120);

                // Bottom center P.T.O. placeholder for continuation pages (bold subtle muted gray)
                DrawText($"##PTO_{pageIdx}_PLACEHOLDER##", marginLeft, 20, font: "F2", fontSize: 8, align: "center", width: contentWidth, colorHex: "888888");

                if (isFirstPage)
                {
                    curY = pageHeight - marginTop;

                    // Title
                    var title = $"PRICE COMPARISON-{plantCode}";
                    DrawText(title, marginLeft, curY, font: "F2", fontSize: 13, align: "center", width: contentWidth);
                    curY -= titleGap;

                    // Proportional to contentWidth (calibrated against A4's 770pt content area) rather
                    // than the fixed 460/490pt this used to be - those were tuned for A4 and, on a
                    // wider page (A3, Wide margins, ...), left the right-hand metadata block anchored
                    // well short of the table's actual right edge, reading as "shifted toward center".
                    const double referenceContentWidth = 770.0;
                    double leftColWidth = contentWidth * (460.0 / referenceContentWidth);
                    double rightColX = marginLeft + (contentWidth * (490.0 / referenceContentWidth));
                    double rightColWidth = (marginLeft + contentWidth) - rightColX;

                    // Row 1: Date (Left) & Collective Number (Right)
                    DrawText($"Date : {dateStr}", marginLeft, curY, font: "F2", fontSize: 8.5);
                    DrawFittedText($"Collective Number : {collectiveNo}", rightColX, curY, font: "F2", baseFontSize: 8.5, align: "left", maxWidth: rightColWidth);
                    curY -= metaLineGap;

                    // Row 2: Requested By (Left) & Requested For (Right)
                    DrawText($"Requested By : {pr.Requestor}", marginLeft, curY, font: "F2", fontSize: 8.5);
                    var reqFor = string.IsNullOrWhiteSpace(pr.Description) ? "E&I" : pr.Description.Trim();
                    DrawFittedText($"Requested For : {reqFor}", rightColX, curY, font: "F2", baseFontSize: 8.5, align: "left", maxWidth: rightColWidth);
                    curY -= metaLineGap;

                    // Row 3: PR Number (Left) & RFQ Number (Right)
                    var prDisplay = FormatPrNumbers(pr);
                    var rfqDisplay = FormatRfqNumbers(selectedRfqs);

                    DrawFittedText($"PR Number : {prDisplay}", marginLeft, curY, font: "F2", baseFontSize: 8.5, align: "left", maxWidth: leftColWidth);
                    DrawFittedText($"RFQ Number : {rfqDisplay}", rightColX, curY, font: "F2", baseFontSize: 8.5, align: "left", maxWidth: rightColWidth);
                    curY -= metaGapAfter;
                }
                else
                {
                    curY = pageHeight - (marginTop - 4);

                    // Running Header on continuation pages (ASCII hyphen to avoid font substitution)
                    var runningHeader = $"PRICE COMPARISON-{plantCode} - Requisition {pr.PrNo}";
                    DrawText(runningHeader, marginLeft, curY, font: "F2", fontSize: 9.5);
                    curY -= continuationHeaderGap;
                }

                // Table Header
                double rowH1 = tableHeaderRowH1;
                double rowH2 = tableHeaderRowH2;
                currentPage.TableTopY = curY;

                DrawRect(marginLeft, curY - rowH1 - rowH2, contentWidth, rowH1 + rowH2, lineWidth: 0.5, fillHex: "F2F4F7");

                DrawText("Sl No.", colX[0], curY - 14, font: "F2", fontSize: 8, align: "center", width: slNoWidth);
                DrawText("Item Description", colX[1], curY - 14, font: "F2", fontSize: 8, align: "center", width: descWidth);
                // Two lines at full header size instead of one line shrunk to fit - same size as
                // every other header on the sheet, just wrapped like a long vendor name already is.
                DrawText("PR", colX[2], curY - 13, font: "F2", fontSize: 8, align: "center", width: qtyWidth);
                DrawText("Quantity", colX[2], curY - 22, font: "F2", fontSize: 8, align: "center", width: qtyWidth);

                for (int i = 0; i < supplierCount; i++)
                {
                    // Vendor name centered across the pair's full width, no divider drawn through it -
                    // reads the same as Excel's merged header cell.
                    DrawCenteredBlock(vendorHeaderLines[i], colX[VendorQtyColIdx(i)], curY, rowH1, vendorHeaderLineHeight, "F2", 7.5, VendorPairW(i));
                    DrawText("Qty", colX[VendorQtyColIdx(i)], curY - rowH1 - 10, font: "F2", fontSize: 7, align: "center", width: vendorQtyW[i]);
                    DrawText("Unit Price", colX[VendorPriceColIdx(i)], curY - rowH1 - 10, font: "F2", fontSize: 7, align: "center", width: vendorPriceW[i]);
                }

                DrawCenteredBlock(historicalHeaderLines, colX[HistoricalColIdx()], curY, rowH1, vendorHeaderLineHeight, "F2", 7.5, historicalWidth);
                DrawText("Unit Price", colX[HistoricalColIdx()], curY - rowH1 - 10, font: "F2", fontSize: 7, align: "center", width: historicalWidth);

                DrawLine(colX[3], curY - rowH1, marginLeft + contentWidth, curY - rowH1, width: 0.5);

                // The Qty|Unit Price divider starts under the merged vendor-name row, so the name
                // stays uncut - the sub-header cells below it are real separate cells and do get it.
                currentPage.MinorDividerTopY = curY - rowH1;
                // Sub-header row alone to start with - a page whose item rows never arrive (an
                // all-summary continuation page) still divides its own Qty|Unit Price headings.
                currentPage.ItemRowsBottomY = curY - rowH1 - rowH2;

                curY -= (rowH1 + rowH2);
            }

            // Start Page 1
            StartNewPage(isFirstPage: true);

            var defaultCur = defaultCurrency;

            // RENDER LINE ITEMS (prItems fetched earlier, alongside the description-column sizing)
            int itemIndex = 1;

            // Per-item description wrap - measured against the actual description column width, so
            // only a row whose own item name genuinely needs a second line grows; every other row
            // keeps its normal single-line height. Replaces the old fixed 36-character truncation.
            const double itemDescLineHeight = 9.0;
            var itemDescLines = prItems
                .Select(item => WrapText(item.ItemName, "F1", 7.5, descWidth - 8, maxLines: 2))
                .ToList();
            var itemRowHeights = itemDescLines
                .Select(lines => itemRowH + Math.Max(0, lines.Count - 1) * itemDescLineHeight)
                .ToList();

            // Remarks are free text and can run long - wrap over the full content width with no
            // truncation, and let the real line count feed the footer budget below so the block
            // either scales or paginates rather than clipping at the page edge.
            double remarksLineH = shrink ? 9.5 : 11.0;
            var remarksBody = string.IsNullOrWhiteSpace(remarks) ? "None" : remarks.Trim();
            var remarksLines = WrapText($"Remarks : {remarksBody}", "F2", 8.5, contentWidth, int.MaxValue, truncate: false);
            double remarksBlockH = remarksLines.Count * remarksLineH;

            const int summaryRowsCount = 12;
            double footerBlockNeeded = (summaryRowsCount * summaryRowH) + afterTableGap + remarksBlockH + remarksGap
                + (options.IncludeSignatureBoxes ? signatureBoxHeight : 0);

            // Shrink-to-fit's tightened row heights above only buy back so much - a long item list
            // still overflows the tightened layout onto a second, near-empty page (the original bug
            // report). Rather than tune row heights further, work out exactly how much extra uniform
            // scale the WHOLE page needs to make everything fit, then apply it as a single PDF
            // transform at the end - the same "shrink the whole sheet" effect as a photocopier or
            // Word's own Fit-to-Page, and it always lands on exactly one page for any item count that
            // clears the legibility floor below.
            bool useAutoScale = false;
            double autoScale = 1.0;
            if (shrink && prItems.Count > 0)
            {
                double headerBlockHeight = titleGap + (2 * metaLineGap) + metaGapAfter + tableHeaderRowH1 + tableHeaderRowH2;
                double neededHeight = headerBlockHeight + itemRowHeights.Sum() + footerBlockNeeded;
                double availableForOnePage = pageHeight - marginTop - bottomLimit;

                if (neededHeight > availableForOnePage)
                {
                    var candidate = availableForOnePage / neededHeight;
                    // Below this the text stops being worth reading - a sheet this long paginates
                    // instead, exactly like it did before shrink-to-fit existed.
                    if (candidate >= 0.4)
                    {
                        autoScale = candidate;
                        useAutoScale = true;
                    }
                }
            }

            if (prItems.Count == 0)
            {
                double rowH = 18;
                DrawRect(marginLeft, curY - rowH, contentWidth, rowH, lineWidth: 0.5);
                DrawText("1", colX[0], curY - 12, font: "F1", fontSize: 8, align: "center", width: slNoWidth);
                DrawText(pr.Description, colX[1] + 4, curY - 12, font: "F1", fontSize: 8);
                DrawText(pr.ItemsCount.ToString(), colX[2], curY - 12, font: "F1", fontSize: 8, align: "center", width: qtyWidth);

                for (int i = 0; i < supplierCount; i++)
                {
                    var rfq = selectedRfqs[i];
                    var cur = string.IsNullOrWhiteSpace(rfq.Currency) ? "AED" : rfq.Currency.Trim();
                    var amt = rfq.BaseAmount > 0 ? rfq.BaseAmount : (rfq.QuoteAmount ?? 0m);
                    // No PrItem row exists in this fallback, so there's no matched RfqItem to read a
                    // quantity from.
                    DrawText("-", colX[VendorQtyColIdx(i)], curY - 12, font: "F1", fontSize: 8, align: "center", width: vendorQtyW[i]);
                    DrawMoneyCell(colX[VendorPriceColIdx(i)], vendorPriceW[i], curY - 12, cur, amt, fontSize: 8);
                }
                DrawMoneyCell(colX[HistoricalColIdx()], historicalWidth, curY - 12, defaultCur, 0.00m, fontSize: 8);
                curY -= rowH;
            }
            else
            {
                for (int itemPos = 0; itemPos < prItems.Count; itemPos++)
                {
                    var item = prItems[itemPos];
                    double rowH = itemRowHeights[itemPos];

                    // Check if current row exceeds printable space - skipped under auto-scale, which
                    // guarantees everything fits on the one page already started.
                    if (!useAutoScale && curY - rowH < bottomLimit + 10)
                    {
                        // This page's item rows run all the way to its table bottom - record it
                        // before the page closes, or its Qty|Unit Price divider never gets drawn.
                        currentPage.TableBottomY = curY;
                        currentPage.ItemRowsBottomY = curY;
                        StartNewPage(isFirstPage: false);
                    }

                    DrawRect(marginLeft, curY - rowH, contentWidth, rowH, lineWidth: 0.5);

                    // Same single-line vertical center DrawCenteredBlock computes for a 1-line
                    // block - shared here so the price cells line up with a wrapped 2-line
                    // description instead of sitting fixed near the top of a taller row.
                    double singleLineCenterY = curY - ((rowH - itemDescLineHeight) / 2.0) - (itemDescLineHeight * 0.75);

                    DrawCenteredBlock(new List<string> { itemIndex.ToString() }, colX[0], curY, rowH, itemDescLineHeight, "F1", 8, slNoWidth);
                    DrawCenteredBlock(itemDescLines[itemPos], colX[1] + 4, curY, rowH, itemDescLineHeight, "F1", 7.5, descWidth - 4, align: "left");

                    var qtyStr = $"{item.Quantity.ToString("G29", CultureInfo.InvariantCulture)} {item.Unit}";
                    DrawFittedText(qtyStr, colX[2] + 3, singleLineCenterY, font: "F1", baseFontSize: 7.5, align: "center", maxWidth: qtyWidth - 6, minFontSize: 5.0);

                    decimal rowLastPrice = item.EstimatedUnitPrice ?? 0m;
                    bool rowLastPriceFromQuote = false;

                    for (int i = 0; i < supplierCount; i++)
                    {
                        var rfq = selectedRfqs[i];
                        var cur = string.IsNullOrWhiteSpace(rfq.Currency) ? "AED" : rfq.Currency.Trim();
                        // Matched once up front, for the width pass and this loop both.
                        var rfqItem = matchedRfqItems[itemPos, i];
                        if (rfqItem?.QuotedUnitPrice != null && rfqItem.QuotedUnitPrice.Value > 0)
                        {
                            // Net of per-unit discount so qty x price reconciles with the totals.
                            var netUnitPrice = Math.Max(0m, rfqItem.QuotedUnitPrice.Value - (rfqItem.Discount ?? 0m));
                            // The vendor's OWN quoted quantity - independently editable per RfqItem,
                            // not necessarily the PR Quantity column two cells to the left - in its
                            // own real column now, not stacked into the price cell.
                            DrawFittedText(rfqItem.FormattedQuantity, colX[VendorQtyColIdx(i)] + 3, singleLineCenterY, font: "F1", baseFontSize: 7, align: "center", maxWidth: vendorQtyW[i] - 6, minFontSize: 5.0);
                            DrawMoneyCell(colX[VendorPriceColIdx(i)], vendorPriceW[i], singleLineCenterY, cur, netUnitPrice, fontSize: 7.5, showZeroAsDash: true);
                        }
                        else
                        {
                            DrawText("-", colX[VendorQtyColIdx(i)], singleLineCenterY, font: "F1", fontSize: 8, align: "center", width: vendorQtyW[i]);
                            DrawText("-", colX[VendorPriceColIdx(i)], singleLineCenterY, font: "F1", fontSize: 8, align: "center", width: vendorPriceW[i]);
                        }

                        // First supplier in fixed order wins; last-wins made the printed
                        // historical baseline depend on supplier position.
                        if (!rowLastPriceFromQuote && rfqItem?.LastPrice != null && rfqItem.LastPrice.Value > 0)
                        {
                            rowLastPrice = rfqItem.LastPrice.Value;
                            rowLastPriceFromQuote = true;
                        }
                    }

                    if (rowLastPrice > 0)
                    {
                        DrawMoneyCell(colX[HistoricalColIdx()], historicalWidth, singleLineCenterY, defaultCur, rowLastPrice, fontSize: 7.5, showZeroAsDash: true);
                    }
                    else
                    {
                        DrawText("-", colX[HistoricalColIdx()], singleLineCenterY, font: "F1", fontSize: 8, align: "center", width: historicalWidth);
                    }

                    curY -= rowH;
                    itemIndex++;
                }
            }

            // Marks where the item-rows portion of the table ends on this page - the Qty/Unit Price
            // divider inside each vendor's pair stops here; see CloseCurrentPageTable.
            currentPage.ItemRowsBottomY = curY;

            // CHECK SPACE FOR SUMMARY ROWS + REMARKS + SIGNATURE BOXES (footerBlockNeeded computed
            // above, alongside the auto-scale decision that already accounts for it)
            if (!useAutoScale && curY - footerBlockNeeded < bottomLimit)
            {
                currentPage.TableBottomY = curY;
                StartNewPage(isFirstPage: false);
            }

            // SUMMARY & FINANCIAL TERMS ROWS
            // Historical Price prints nothing at all from here down - it's a per-item reference
            // price, not a real quote carried through the same discount/VAT/total math as an actual
            // vendor, so neither helper takes a value for it any more.
            void DrawSummaryMoneyRow(string label, Func<RequestForQuotation, (decimal? amount, bool showZeroAsDash)> valFunc, bool isBold = false)
            {
                double rowH = summaryRowH;
                DrawRect(marginLeft, curY - rowH, contentWidth, rowH, lineWidth: 0.5);

                DrawText(label, colX[1] + 4, curY - 9.5, font: isBold ? "F2" : "F1", fontSize: 7.5);

                for (int i = 0; i < supplierCount; i++)
                {
                    var rfq = selectedRfqs[i];
                    var cur = string.IsNullOrWhiteSpace(rfq.Currency) ? "AED" : rfq.Currency.Trim();
                    var (amt, showDash) = valFunc(rfq);
                    // Spans the vendor's whole Qty+Price pair - no divider drawn through this band
                    // (see CloseCurrentPageTable), so the total reads as one merged figure.
                    DrawMoneyCell(colX[VendorQtyColIdx(i)], VendorPairW(i), curY - 9.5, cur, amt, isBold: isBold, fontSize: 7.5, showZeroAsDash: showDash);
                }

                curY -= rowH;
            }

            void DrawSummaryTextRow(string label, Func<RequestForQuotation, string> valFunc, bool isBold = false)
            {
                double rowH = summaryRowH;
                DrawRect(marginLeft, curY - rowH, contentWidth, rowH, lineWidth: 0.5);

                DrawText(label, colX[1] + 4, curY - 9.5, font: isBold ? "F2" : "F1", fontSize: 7.5);

                for (int i = 0; i < supplierCount; i++)
                {
                    var text = valFunc(selectedRfqs[i]);
                    DrawFittedText(text, colX[VendorQtyColIdx(i)] + 3, curY - 9.5, font: isBold ? "F2" : "F1", baseFontSize: 7.5, align: "center", maxWidth: VendorPairW(i) - 6, minFontSize: 5.0);
                }

                curY -= rowH;
            }

            // Unquoted vendors print "-" instead of a 0.00 that reads as a zero quote. Gated on
            // quote PRESENCE (null for no quote) with showZeroAsDash off, so a genuine zero or
            // negative figure from a quoted vendor still prints — matching the Excel exporter.
            static bool HasQuote(RequestForQuotation rf) => rf.IsQuoteReceived || rf.PricedItemsCount > 0;

            DrawSummaryMoneyRow("Total Price Excl. VAT",
                rf => (HasQuote(rf) ? (rf.BaseAmount > 0 ? rf.BaseAmount : (rf.QuoteAmount ?? 0m)) : (decimal?)null, false),
                isBold: true);

            DrawSummaryMoneyRow("Discount",
                rf => (rf.Discount, true));

            // Unclamped — the model clamps only the final NetTaxable; the early clamp made the
            // printed breakdown fail arithmetic checks in the discount-exceeds-base edge case.
            DrawSummaryMoneyRow("Total Price Excl. VAT After Discount",
                rf => (HasQuote(rf)
                    ? ((rf.BaseAmount > 0 ? rf.BaseAmount : (rf.QuoteAmount ?? 0m)) - (rf.Discount ?? 0m))
                    : (decimal?)null, false),
                isBold: true);

            DrawSummaryMoneyRow("Freight/Shipping Charges",
                rf => (rf.Freight, true));

            DrawSummaryMoneyRow("Other Charges",
                rf => (rf.OtherCharges, true));

            DrawSummaryTextRow("VAT",
                rf => (string.IsNullOrWhiteSpace(rf.VatType) ? "5%" : rf.VatType));

            DrawSummaryMoneyRow("Total Price Incl. VAT",
                rf => (HasQuote(rf) ? rf.TotalLandedCost : (decimal?)null, false),
                isBold: true);

            DrawSummaryTextRow("Payment Terms",
                rf => (string.IsNullOrWhiteSpace(rf.PaymentTerms) ? "30 Days Net" : rf.PaymentTerms));

            DrawSummaryTextRow("Delivery Terms",
                rf => (string.IsNullOrWhiteSpace(rf.Incoterms) ? "DDP" : rf.Incoterms));

            DrawSummaryTextRow("Lead Time (Days)",
                rf => (string.IsNullOrWhiteSpace(rf.DeliveryLeadTime) ? "-" : rf.DeliveryLeadTime));

            DrawSummaryTextRow("Warranty",
                rf => (string.IsNullOrWhiteSpace(rf.Warranty) ? "-" : rf.Warranty));

            // Blank means "not recorded" — substituting "Approved" fabricated approval status.
            DrawSummaryTextRow("Technical Approval",
                rf => (string.IsNullOrWhiteSpace(rf.TechnicalApproval) ? "-" : rf.TechnicalApproval));

            currentPage.TableBottomY = curY;
            CloseCurrentPageTable();

            curY -= afterTableGap;

            // REMARKS SECTION — wrapped over the full width (computed above). Each line drops to the
            // next; if it runs past the page under pagination it continues on a fresh page.
            foreach (var remarksLine in remarksLines)
            {
                if (!useAutoScale && curY - remarksLineH < bottomLimit)
                {
                    currentPage.TableBottomY = curY;
                    StartNewPage(isFirstPage: false);
                }
                DrawText(remarksLine, marginLeft, curY, font: "F2", fontSize: 8.5);
                curY -= remarksLineH;
            }
            curY -= (remarksGap - remarksLineH);

            // BOTTOM SIGNATURE / APPROVER BOXES — omitted entirely when the export is a quick
            // internal proof that isn't going for wet-ink signoff yet.
            if (options.IncludeSignatureBoxes)
            {
                // A long remark can end near the page bottom; keep the box row whole on the next page.
                if (!useAutoScale && curY - signatureBoxHeight - 6 < bottomLimit)
                {
                    currentPage.TableBottomY = curY;
                    StartNewPage(isFirstPage: false);
                }

                var approverRoles = new List<string> { "Buyer" };
                if (pcr.Approvals != null && pcr.Approvals.Count > 0)
                {
                    foreach (var app in pcr.Approvals)
                    {
                        var roleName = app.RoleDisplayName;
                        if (!approverRoles.Contains(roleName, StringComparer.OrdinalIgnoreCase))
                        {
                            approverRoles.Add(roleName);
                        }
                    }
                }
                else
                {
                    approverRoles.Add("Procurement Manager");
                    approverRoles.Add("Finance Controller");
                    approverRoles.Add("CFO");
                    approverRoles.Add("CEO");
                }

                int boxCount = approverRoles.Count;
                double boxGap = 10;
                double totalBoxWidth = contentWidth - ((boxCount - 1) * boxGap);
                double boxWidth = totalBoxWidth / boxCount;
                double boxHeight = signatureBoxHeight;
                double boxTitleH = 17;

                for (int b = 0; b < boxCount; b++)
                {
                    double bx = marginLeft + (b * (boxWidth + boxGap));
                    double by = curY - boxHeight;

                    DrawRect(bx, by, boxWidth, boxHeight, lineWidth: 0.8, fillHex: "FFFFFF");
                    DrawRect(bx, by + boxHeight - boxTitleH, boxWidth, boxTitleH, lineWidth: 0.5, fillHex: "F2F4F7");
                    DrawText(approverRoles[b], bx, by + boxHeight - 12, font: "F2", fontSize: 8, align: "center", width: boxWidth);
                }
            }

            // Apply the auto-scale computed before the item loop: a uniform PDF transform ahead of
            // every drawing operator on this (guaranteed single) page, so everything drawn afterward
            // renders smaller and stays anchored to the top-left content corner instead of spilling
            // past the bottom margin.
            if (useAutoScale)
            {
                double topY = pageHeight - marginTop;
                double tx = marginLeft * (1 - autoScale);
                double ty = topY * (1 - autoScale);
                pages[0].Stream.Insert(0, $"{autoScale:F4} 0 0 {autoScale:F4} {tx:F2} {ty:F2} cm\n");
            }

            // MULTI-PAGE PDF BINARY COMPILATION
            // Labels ("Page N of M", P.T.O.) come from the laid-out document; which pages are written
            // out is a separate, later decision (PagesToEmit), so a subset keeps its original numbering.
            int laidOutPages = pages.Count;
            var emit = options.PagesToEmit is { Count: > 0 }
                ? options.PagesToEmit.Where(i => i >= 0 && i < laidOutPages).Distinct().OrderBy(i => i).ToList()
                : Enumerable.Range(0, laidOutPages).ToList();
            if (emit.Count == 0) emit = Enumerable.Range(0, laidOutPages).ToList();
            int totalPages = emit.Count;
            var pageStreamBytes = new List<byte[]>();

            foreach (var pIdx in emit)
            {
                var text = pages[pIdx].Stream.ToString();
                // Replace page placeholder with final page count in top-right corner
                var pageNumberText = $"Page {pIdx + 1} of {laidOutPages}";
                text = text.Replace($"##PAGE_{pIdx + 1}_PLACEHOLDER##", pageNumberText);

                // Replace P.T.O. placeholder (show on all pages except the last page)
                var ptoText = (pIdx < laidOutPages - 1) ? "P.T.O." : "";
                text = text.Replace($"##PTO_{pIdx + 1}_PLACEHOLDER##", ptoText);

                pageStreamBytes.Add(EncodeWinAnsi(text));
            }

            using var pdfMs = new MemoryStream();
            using var pdfWriter = new StreamWriter(pdfMs, Encoding.ASCII);
            var offsets = new List<long>();

            // Header
            pdfWriter.WriteLine("%PDF-1.4");
            pdfWriter.WriteLine("%\xE2\xE3\xCF\xD3");
            pdfWriter.Flush();

            // Object 1: Catalog
            offsets.Add(pdfMs.Position);
            pdfWriter.WriteLine("1 0 obj");
            pdfWriter.WriteLine("<< /Type /Catalog /Pages 2 0 R >>");
            pdfWriter.WriteLine("endobj");
            pdfWriter.Flush();

            // Object 2: Pages Collection
            var kidsStr = string.Join(" ", Enumerable.Range(3, totalPages).Select(id => $"{id} 0 R"));
            offsets.Add(pdfMs.Position);
            pdfWriter.WriteLine("2 0 obj");
            pdfWriter.WriteLine($"<< /Type /Pages /Kids [{kidsStr}] /Count {totalPages} >>");
            pdfWriter.WriteLine("endobj");
            pdfWriter.Flush();

            int font1ObjId = 3 + totalPages;
            int font2ObjId = 4 + totalPages;
            int firstStreamObjId = 5 + totalPages;

            // Page Objects (IDs 3 to 2 + totalPages)
            for (int p = 0; p < totalPages; p++)
            {
                int pageObjId = 3 + p;
                int streamObjId = firstStreamObjId + p;

                offsets.Add(pdfMs.Position);
                pdfWriter.WriteLine($"{pageObjId} 0 obj");
                pdfWriter.WriteLine($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth:F0} {pageHeight:F0}] /Resources << /Font << /F1 {font1ObjId} 0 R /F2 {font2ObjId} 0 R >> >> /Contents {streamObjId} 0 R >>");
                pdfWriter.WriteLine("endobj");
                pdfWriter.Flush();
            }

            // Object font1 (Helvetica)
            offsets.Add(pdfMs.Position);
            pdfWriter.WriteLine($"{font1ObjId} 0 obj");
            pdfWriter.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            pdfWriter.WriteLine("endobj");
            pdfWriter.Flush();

            // Object font2 (Helvetica-Bold)
            offsets.Add(pdfMs.Position);
            pdfWriter.WriteLine($"{font2ObjId} 0 obj");
            pdfWriter.WriteLine("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
            pdfWriter.WriteLine("endobj");
            pdfWriter.Flush();

            // Stream Objects for each page
            for (int p = 0; p < totalPages; p++)
            {
                int streamObjId = firstStreamObjId + p;
                var bytes = pageStreamBytes[p];

                offsets.Add(pdfMs.Position);
                pdfWriter.WriteLine($"{streamObjId} 0 obj");
                pdfWriter.WriteLine($"<< /Length {bytes.Length} >>");
                pdfWriter.WriteLine("stream");
                pdfWriter.Flush();

                pdfMs.Write(bytes, 0, bytes.Length);
                pdfMs.Flush();

                pdfWriter.WriteLine();
                pdfWriter.WriteLine("endstream");
                pdfWriter.WriteLine("endobj");
                pdfWriter.Flush();
            }

            // XRef Table
            long startXref = pdfMs.Position;
            int totalObjects = 4 + (2 * totalPages);
            pdfWriter.WriteLine("xref");
            pdfWriter.WriteLine($"0 {totalObjects + 1}");
            pdfWriter.WriteLine("0000000000 65535 f ");
            foreach (var offset in offsets)
            {
                pdfWriter.WriteLine($"{offset:D10} 00000 n ");
            }

            // Trailer
            pdfWriter.WriteLine("trailer");
            pdfWriter.WriteLine($"<< /Size {totalObjects + 1} /Root 1 0 R >>");
            pdfWriter.WriteLine("startxref");
            pdfWriter.WriteLine(startXref);
            pdfWriter.WriteLine("%%EOF");
            pdfWriter.Flush();

            return pdfMs.ToArray();
        }

        // Adobe Core-14 AFM glyph widths (1/1000 em), ASCII 32-126 - real per-character widths instead
        // of the flat "fontSize * 0.52 per character" estimate this replaced. The flat estimate was
        // eyeballed against A4's narrower columns; the accumulated per-character error it carries
        // doesn't scale with column width, so it stayed a fixed few points off while the columns grew
        // on wider paper/margins, reading as "not centered" once there was more surrounding space to
        // judge the offset against.
        private static readonly short[] HelveticaWidths =
        {
            278,278,355,556,556,889,667,191,333,333,389,584,278,333,278,278,
            556,556,556,556,556,556,556,556,556,556,
            278,278,584,584,584,556,1015,
            667,667,722,722,667,611,778,722,278,500,667,556,833,722,778,667,778,722,667,611,722,667,944,667,667,611,
            278,278,278,469,556,333,
            556,556,500,556,556,278,556,556,222,222,500,222,833,556,556,556,556,333,500,278,556,500,722,500,500,500,
            334,260,334,584
        };

        private static readonly short[] HelveticaBoldWidths =
        {
            278,333,474,556,556,889,722,238,333,333,389,584,278,333,278,278,
            556,556,556,556,556,556,556,556,556,556,
            333,333,584,584,584,611,975,
            722,722,722,722,667,611,778,722,278,556,722,611,833,722,778,667,778,722,667,611,722,667,944,667,667,611,
            333,278,333,584,556,333,
            556,611,556,611,556,333,611,611,278,278,556,278,889,611,611,611,611,389,556,333,611,556,778,556,556,500,
            389,280,389,584
        };

        private static double MeasureTextWidth(string text, string font, double fontSize)
        {
            var table = font == "F2" ? HelveticaBoldWidths : HelveticaWidths;
            double units = 0;
            foreach (var ch in text)
            {
                var idx = ch - 32;
                units += (idx >= 0 && idx < table.Length) ? table[idx] : 556; // outside the table: a mid-width guess
            }
            return units * fontSize / 1000.0;
        }

        private static (double r, double g, double b) HexToRgb(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return (0, 0, 0);
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                int r = int.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                int g = int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
                int b = int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
                return (r / 255.0, g / 255.0, b / 255.0);
            }
            return (0, 0, 0);
        }

        // Encodes page-stream text as WinAnsi (cp1252) — the font objects declare
        // /WinAnsiEncoding to match — so accented and typographic characters survive instead of
        // degrading to '?' the way Encoding.ASCII rendered them. Characters outside cp1252
        // (e.g. Arabic, which the base-14 Helvetica cannot render anyway) still fall back to '?'.
        private static byte[] EncodeWinAnsi(string text)
        {
            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                byte b;
                if (c < 0x80 || (c >= 0xA0 && c <= 0xFF))
                {
                    b = (byte)c;
                }
                else
                {
                    b = c switch
                    {
                        '€' => 0x80, // €
                        '‚' => 0x82,
                        'ƒ' => 0x83,
                        '„' => 0x84,
                        '…' => 0x85, // …
                        '†' => 0x86,
                        '‡' => 0x87,
                        'ˆ' => 0x88,
                        '‰' => 0x89,
                        'Š' => 0x8A,
                        '‹' => 0x8B,
                        'Œ' => 0x8C,
                        'Ž' => 0x8E,
                        '‘' => 0x91, // '
                        '’' => 0x92, // '
                        '“' => 0x93, // "
                        '”' => 0x94, // "
                        '•' => 0x95, // •
                        '–' => 0x96, // –
                        '—' => 0x97, // —
                        '˜' => 0x98,
                        '™' => 0x99, // ™
                        'š' => 0x9A,
                        '›' => 0x9B,
                        'œ' => 0x9C,
                        'ž' => 0x9E,
                        'Ÿ' => 0x9F,
                        _ => (byte)'?'
                    };
                }
                bytes[i] = b;
            }
            return bytes;
        }

        private static string EscapePdfText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text
                .Replace("\\", "\\\\")
                .Replace("(", "\\(")
                .Replace(")", "\\)");
        }

        public static string FormatRfqNumbers(IReadOnlyList<RequestForQuotation> selectedRfqs)
        {
            var rawList = selectedRfqs
                .Select(rf => rf.RfqNo.Replace("RFQ-", "").Trim())
                .Where(num => !string.IsNullOrWhiteSpace(num))
                .ToList();

            if (rawList.Count == 0) return "-";
            if (rawList.Count == 1) return rawList[0];

            // Find longest common prefix across all RFQ numbers
            string prefix = rawList[0];
            foreach (var s in rawList.Skip(1))
            {
                int j = 0;
                while (j < prefix.Length && j < s.Length && prefix[j] == s[j])
                {
                    j++;
                }
                prefix = prefix.Substring(0, j);
                if (string.IsNullOrEmpty(prefix)) break;
            }

            if (prefix.Length >= 4)
            {
                var suffixes = rawList.Select(s => s.Substring(prefix.Length)).ToList();
                return $"{prefix}{string.Join("/", suffixes)}";
            }

            return string.Join("/", rawList);
        }

        public static string FormatPrNumbers(PurchaseRequisition pr)
        {
            if (!string.IsNullOrWhiteSpace(pr.ConsolidatedFrom))
            {
                var prs = pr.ConsolidatedFrom.Split(new[] { ',', ';', '/', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim().Replace("PR-", ""))
                    .Distinct()
                    .ToList();
                if (prs.Count > 0)
                {
                    return string.Join(" / ", prs);
                }
            }
            return string.IsNullOrWhiteSpace(pr.PrNo) ? "-" : pr.PrNo.Replace("PR-", "");
        }
    }
}
