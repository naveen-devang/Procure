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
        }

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
            const double qtyWidth = 45;

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
            const double vendorColWidthFloor = 90;
            const double descPadding = 16;

            double vendorColsCount = supplierCount + 1; // suppliers + historical price
            double availableForDescAndVendors = contentWidth - slNoWidth - qtyWidth;
            double maxDescWidthByVendorFloor = availableForDescAndVendors - (vendorColsCount * vendorColWidthFloor);

            double widestItemTextWidth = prItems.Count > 0
                ? prItems.Max(item => MeasureTextWidth(item.ItemName, "F1", 7.5))
                : 0;
            double idealDescWidth = widestItemTextWidth + descPadding;
            double effectiveDescCap = Math.Max(descWidthFloor, Math.Min(descWidthCap, maxDescWidthByVendorFloor));
            double descWidth = Math.Clamp(idealDescWidth, descWidthFloor, effectiveDescCap);

            double remainingWidth = availableForDescAndVendors - descWidth;
            double colWidth = remainingWidth / vendorColsCount; // suppliers + last price

            var colX = new List<double> { marginLeft, marginLeft + slNoWidth, marginLeft + slNoWidth + descWidth, marginLeft + slNoWidth + descWidth + qtyWidth };
            for (int i = 0; i <= supplierCount; i++)
            {
                colX.Add(colX[3] + ((i + 1) * colWidth));
            }

            var plantCode = string.IsNullOrWhiteSpace(pr.Plant) ? "RW01" : pr.Plant.Trim();
            var cleanRfqList = selectedRfqs
                .Select(rf => rf.RfqNo.Replace("RFQ-", "").Trim())
                .Where(num => !string.IsNullOrWhiteSpace(num))
                .ToList();
            var rfqNumsDisplay = cleanRfqList.Count > 0 ? string.Join("/", cleanRfqList) : "-";
            var collectiveNo = string.IsNullOrWhiteSpace(pcr.PcrNo) ? $"PCR-{pr.PrNo}" : pcr.PcrNo;
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

            void DrawFittedText(string text, double x, double y, string font = "F1", double baseFontSize = 8.5, string align = "left", double maxWidth = 350, string colorHex = "000000")
            {
                if (string.IsNullOrEmpty(text)) return;
                double approxWidth = MeasureTextWidth(text, font, baseFontSize);

                double finalFontSize = baseFontSize;
                if (approxWidth > maxWidth && maxWidth > 0)
                {
                    finalFontSize = Math.Max(6.5, baseFontSize * (maxWidth / approxWidth));
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
            List<string> WrapText(string text, string font, double fontSize, double maxWidth, int maxLines)
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
                    // Left side currency
                    DrawText(currency, cellX + 3, textY, font: font, fontSize: fontSize, align: "left");
                    // Right side amount
                    var amtStr = amount.Value.ToString("N2", CultureInfo.InvariantCulture);
                    DrawText(amtStr, cellX, textY, font: font, fontSize: fontSize, align: "right", width: cellWidth);
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
                    // Draw vertical column lines for the page's table portion
                    for (int i = 1; i < colX.Count; i++)
                    {
                        DrawLine(colX[i], currentPage.TableBottomY, colX[i], currentPage.TableTopY, width: 0.5);
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
                vendorHeaderLines.Add(WrapText(selectedRfqs[i].Vendor, "F2", 7.5, colWidth - 6, maxLines: 2));
            }
            var historicalHeaderLines = WrapText("Historical Price", "F2", 7.5, colWidth - 6, maxLines: 2);

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
                DrawText("Quantity", colX[2], curY - 14, font: "F2", fontSize: 8, align: "center", width: qtyWidth);

                for (int i = 0; i < supplierCount; i++)
                {
                    DrawCenteredBlock(vendorHeaderLines[i], colX[3 + i], curY, rowH1, vendorHeaderLineHeight, "F2", 7.5, colWidth);
                    DrawText("Unit Price", colX[3 + i], curY - rowH1 - 10, font: "F2", fontSize: 7, align: "center", width: colWidth);
                }

                DrawCenteredBlock(historicalHeaderLines, colX[3 + supplierCount], curY, rowH1, vendorHeaderLineHeight, "F2", 7.5, colWidth);
                DrawText("Unit Price", colX[3 + supplierCount], curY - rowH1 - 10, font: "F2", fontSize: 7, align: "center", width: colWidth);

                DrawLine(colX[3], curY - rowH1, marginLeft + contentWidth, curY - rowH1, width: 0.5);

                curY -= (rowH1 + rowH2);
            }

            // Start Page 1
            StartNewPage(isFirstPage: true);

            // Base currency for Last Price column
            var defaultCur = string.IsNullOrWhiteSpace(selectedRfqs.FirstOrDefault()?.Currency) ? "AED" : selectedRfqs.First().Currency.Trim();

            // RENDER LINE ITEMS (prItems fetched earlier, alongside the description-column sizing)
            int itemIndex = 1;
            decimal totalLastPriceSum = 0m;

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

            const int summaryRowsCount = 12;
            double footerBlockNeeded = (summaryRowsCount * summaryRowH) + afterTableGap + remarksGap
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
                    DrawMoneyCell(colX[3 + i], colWidth, curY - 12, cur, amt, fontSize: 8);
                }
                DrawMoneyCell(colX[3 + supplierCount], colWidth, curY - 12, defaultCur, 0.00m, fontSize: 8);
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
                        currentPage.TableBottomY = curY;
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
                    DrawCenteredBlock(new List<string> { qtyStr }, colX[2], curY, rowH, itemDescLineHeight, "F1", 7.5, qtyWidth);

                    decimal rowLastPrice = item.EstimatedUnitPrice ?? 0m;
                    bool rowLastPriceFromQuote = false;

                    for (int i = 0; i < supplierCount; i++)
                    {
                        var rfq = selectedRfqs[i];
                        var cur = string.IsNullOrWhiteSpace(rfq.Currency) ? "AED" : rfq.Currency.Trim();
                        // Exact PrItemId link wins; name matching only covers unlinked lines.
                        var rfqItem = rfq.Items?.FirstOrDefault(ri => ri.PrItemId.HasValue && ri.PrItemId.Value == item.Id)
                                   ?? rfq.Items?.FirstOrDefault(ri => !ri.PrItemId.HasValue && string.Equals(ri.ItemName, item.ItemName, StringComparison.OrdinalIgnoreCase));
                        if (rfqItem?.QuotedUnitPrice != null && rfqItem.QuotedUnitPrice.Value > 0)
                        {
                            // Net of per-unit discount so qty x price reconciles with the totals.
                            var netUnitPrice = Math.Max(0m, rfqItem.QuotedUnitPrice.Value - (rfqItem.Discount ?? 0m));
                            DrawMoneyCell(colX[3 + i], colWidth, singleLineCenterY, cur, netUnitPrice, fontSize: 7.5, showZeroAsDash: true);
                        }
                        else
                        {
                            DrawText("-", colX[3 + i], singleLineCenterY, font: "F1", fontSize: 8, align: "center", width: colWidth);
                        }

                        // First supplier in fixed order wins; last-wins made the printed
                        // historical baseline depend on supplier position.
                        if (!rowLastPriceFromQuote && rfqItem?.LastPrice != null && rfqItem.LastPrice.Value > 0)
                        {
                            rowLastPrice = rfqItem.LastPrice.Value;
                            rowLastPriceFromQuote = true;
                        }
                    }

                    totalLastPriceSum += (item.Quantity * rowLastPrice);

                    if (rowLastPrice > 0)
                    {
                        DrawMoneyCell(colX[3 + supplierCount], colWidth, singleLineCenterY, defaultCur, rowLastPrice, fontSize: 7.5, showZeroAsDash: true);
                    }
                    else
                    {
                        DrawText("-", colX[3 + supplierCount], singleLineCenterY, font: "F1", fontSize: 8, align: "center", width: colWidth);
                    }

                    curY -= rowH;
                    itemIndex++;
                }
            }

            // CHECK SPACE FOR SUMMARY ROWS + REMARKS + SIGNATURE BOXES (footerBlockNeeded computed
            // above, alongside the auto-scale decision that already accounts for it)
            if (!useAutoScale && curY - footerBlockNeeded < bottomLimit)
            {
                currentPage.TableBottomY = curY;
                StartNewPage(isFirstPage: false);
            }

            // SUMMARY & FINANCIAL TERMS ROWS
            void DrawSummaryMoneyRow(string label, Func<RequestForQuotation, (decimal? amount, bool showZeroAsDash)> valFunc, decimal? lastVal, bool isBold = false)
            {
                double rowH = summaryRowH;
                DrawRect(marginLeft, curY - rowH, contentWidth, rowH, lineWidth: 0.5);

                DrawText(label, colX[1] + 4, curY - 9.5, font: isBold ? "F2" : "F1", fontSize: 7.5);

                for (int i = 0; i < supplierCount; i++)
                {
                    var rfq = selectedRfqs[i];
                    var cur = string.IsNullOrWhiteSpace(rfq.Currency) ? "AED" : rfq.Currency.Trim();
                    var (amt, showDash) = valFunc(rfq);
                    DrawMoneyCell(colX[3 + i], colWidth, curY - 9.5, cur, amt, isBold: isBold, fontSize: 7.5, showZeroAsDash: showDash);
                }

                DrawMoneyCell(colX[3 + supplierCount], colWidth, curY - 9.5, defaultCur, lastVal, isBold: isBold, fontSize: 7.5, showZeroAsDash: lastVal == null || lastVal == 0);

                curY -= rowH;
            }

            void DrawSummaryTextRow(string label, Func<RequestForQuotation, string> valFunc, string lastVal, bool isBold = false)
            {
                double rowH = summaryRowH;
                DrawRect(marginLeft, curY - rowH, contentWidth, rowH, lineWidth: 0.5);

                DrawText(label, colX[1] + 4, curY - 9.5, font: isBold ? "F2" : "F1", fontSize: 7.5);

                for (int i = 0; i < supplierCount; i++)
                {
                    var text = valFunc(selectedRfqs[i]);
                    DrawText(text, colX[3 + i], curY - 9.5, font: isBold ? "F2" : "F1", fontSize: 7.5, align: "center", width: colWidth);
                }

                DrawText(lastVal, colX[3 + supplierCount], curY - 9.5, font: isBold ? "F2" : "F1", fontSize: 7.5, align: "center", width: colWidth);

                curY -= rowH;
            }

            // Unquoted vendors print "-" instead of a 0.00 that reads as a zero quote. Gated on
            // quote PRESENCE (null for no quote) with showZeroAsDash off, so a genuine zero or
            // negative figure from a quoted vendor still prints — matching the Excel exporter.
            static bool HasQuote(RequestForQuotation rf) => rf.IsQuoteReceived || rf.PricedItemsCount > 0;

            DrawSummaryMoneyRow("Total Price Excl. VAT",
                rf => (HasQuote(rf) ? (rf.BaseAmount > 0 ? rf.BaseAmount : (rf.QuoteAmount ?? 0m)) : (decimal?)null, false),
                totalLastPriceSum, isBold: true);

            DrawSummaryMoneyRow("Discount",
                rf => (rf.Discount, true),
                null);

            // Unclamped — the model clamps only the final NetTaxable; the early clamp made the
            // printed breakdown fail arithmetic checks in the discount-exceeds-base edge case.
            DrawSummaryMoneyRow("Total Price Excl. VAT After Discount",
                rf => (HasQuote(rf)
                    ? ((rf.BaseAmount > 0 ? rf.BaseAmount : (rf.QuoteAmount ?? 0m)) - (rf.Discount ?? 0m))
                    : (decimal?)null, false),
                totalLastPriceSum, isBold: true);

            DrawSummaryMoneyRow("Freight/Shipping Charges",
                rf => (rf.Freight, true),
                null);

            DrawSummaryMoneyRow("Other Charges",
                rf => (rf.OtherCharges, true),
                null);

            // Historical column VAT follows the compared quotes' predominant VatType rather than
            // a hardcoded 5% that inflated the baseline against RC/V0 quotes.
            var historicalVatType = selectedRfqs.Count > 0
                ? selectedRfqs.GroupBy(rf => string.IsNullOrWhiteSpace(rf.VatType) ? "5%" : rf.VatType)
                    .OrderByDescending(g => g.Count()).Select(g => g.Key).First()
                : "5%";

            DrawSummaryTextRow("VAT",
                rf => (string.IsNullOrWhiteSpace(rf.VatType) ? "5%" : rf.VatType),
                historicalVatType);

            decimal lastPriceInclVat = historicalVatType == "5%" ? totalLastPriceSum * 1.05m : totalLastPriceSum;
            DrawSummaryMoneyRow("Total Price Incl. VAT",
                rf => (HasQuote(rf) ? rf.TotalLandedCost : (decimal?)null, false),
                lastPriceInclVat, isBold: true);

            DrawSummaryTextRow("Payment Terms",
                rf => (string.IsNullOrWhiteSpace(rf.PaymentTerms) ? "30 Days Net" : rf.PaymentTerms),
                "-");

            DrawSummaryTextRow("Delivery Terms",
                rf => (string.IsNullOrWhiteSpace(rf.Incoterms) ? "DDP" : rf.Incoterms),
                "-");

            DrawSummaryTextRow("Lead Time (Days)",
                rf => (string.IsNullOrWhiteSpace(rf.DeliveryLeadTime) ? "-" : rf.DeliveryLeadTime),
                "-");

            DrawSummaryTextRow("Warranty",
                rf => (string.IsNullOrWhiteSpace(rf.Warranty) ? "-" : rf.Warranty),
                "-");

            // Blank means "not recorded" — substituting "Approved" fabricated approval status.
            DrawSummaryTextRow("Technical Approval",
                rf => (string.IsNullOrWhiteSpace(rf.TechnicalApproval) ? "-" : rf.TechnicalApproval),
                "-");

            currentPage.TableBottomY = curY;
            CloseCurrentPageTable();

            curY -= afterTableGap;

            // REMARKS SECTION
            var remarksDisplay = string.IsNullOrWhiteSpace(remarks) ? "None" : remarks.Trim();
            DrawText($"Remarks : {remarksDisplay}", marginLeft, curY, font: "F2", fontSize: 8.5);
            curY -= remarksGap;

            // BOTTOM SIGNATURE / APPROVER BOXES — omitted entirely when the export is a quick
            // internal proof that isn't going for wet-ink signoff yet.
            if (options.IncludeSignatureBoxes)
            {
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
            int totalPages = pages.Count;
            var pageStreamBytes = new List<byte[]>();

            for (int pIdx = 0; pIdx < totalPages; pIdx++)
            {
                var text = pages[pIdx].Stream.ToString();
                // Replace page placeholder with final page count in top-right corner
                var pageNumberText = $"Page {pIdx + 1} of {totalPages}";
                text = text.Replace($"##PAGE_{pIdx + 1}_PLACEHOLDER##", pageNumberText);

                // Replace P.T.O. placeholder (show on all pages except the last page)
                var ptoText = (pIdx < totalPages - 1) ? "P.T.O." : "";
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
