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
            public double TableTopY { get; set; }
            public double TableBottomY { get; set; }
        }

        public static byte[] GeneratePdf(
            PurchaseRequisition pr,
            PriceComparisonRequest pcr,
            IReadOnlyList<RequestForQuotation> selectedRfqs,
            string remarks)
        {
            const double pageWidth = 842; // A4 Landscape
            const double pageHeight = 595;
            const double marginLeft = 36;
            const double marginRight = 36;
            const double contentWidth = pageWidth - marginLeft - marginRight; // 770 pt
            const double bottomLimit = 36;

            int supplierCount = selectedRfqs.Count;
            const double slNoWidth = 30;
            const double descWidth = 170;
            const double qtyWidth = 45;
            double remainingWidth = contentWidth - slNoWidth - descWidth - qtyWidth; // ~525 pt
            double colWidth = remainingWidth / (supplierCount + 1); // suppliers + last price

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

                double charWidth = fontSize * 0.52;
                double approxWidth = text.Length * charWidth;

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
                double charWidth = baseFontSize * 0.52;
                double approxWidth = text.Length * charWidth;

                double finalFontSize = baseFontSize;
                if (approxWidth > maxWidth && maxWidth > 0)
                {
                    finalFontSize = Math.Max(6.5, baseFontSize * (maxWidth / approxWidth));
                }

                DrawText(text, x, y, font: font, fontSize: finalFontSize, align: align, width: maxWidth, colorHex: colorHex);
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
                if (currentPage != null && currentPage.TableTopY > 0 && currentPage.TableBottomY > 0)
                {
                    // Draw vertical column lines for the page's table portion
                    for (int i = 1; i < colX.Count; i++)
                    {
                        DrawLine(colX[i], currentPage.TableBottomY, colX[i], currentPage.TableTopY, width: 0.5);
                    }
                }
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
                    curY = pageHeight - 36;

                    // Title
                    var title = $"PRICE COMPARISON-{plantCode}";
                    DrawText(title, marginLeft, curY, font: "F2", fontSize: 13, align: "center", width: contentWidth);
                    curY -= 22;

                    const double leftColWidth = 460;
                    const double rightColX = marginLeft + 490; // 526 pt (anchored right, straight vertical alignment)
                    const double rightColWidth = contentWidth - 490; // 280 pt (up to table right edge)

                    // Row 1: Date (Left) & Collective Number (Right)
                    DrawText($"Date : {dateStr}", marginLeft, curY, font: "F2", fontSize: 8.5);
                    DrawFittedText($"Collective Number : {collectiveNo}", rightColX, curY, font: "F2", baseFontSize: 8.5, align: "left", maxWidth: rightColWidth);
                    curY -= 14;

                    // Row 2: Requested By (Left) & Requested For (Right)
                    DrawText($"Requested By : {pr.Requestor}", marginLeft, curY, font: "F2", fontSize: 8.5);
                    var reqFor = string.IsNullOrWhiteSpace(pr.Description) ? "E&I" : pr.Description.Trim();
                    DrawFittedText($"Requested For : {reqFor}", rightColX, curY, font: "F2", baseFontSize: 8.5, align: "left", maxWidth: rightColWidth);
                    curY -= 14;

                    // Row 3: PR Number (Left) & RFQ Number (Right)
                    var prDisplay = FormatPrNumbers(pr);
                    var rfqDisplay = FormatRfqNumbers(selectedRfqs);

                    DrawFittedText($"PR Number : {prDisplay}", marginLeft, curY, font: "F2", baseFontSize: 8.5, align: "left", maxWidth: leftColWidth);
                    DrawFittedText($"RFQ Number : {rfqDisplay}", rightColX, curY, font: "F2", baseFontSize: 8.5, align: "left", maxWidth: rightColWidth);
                    curY -= 18;
                }
                else
                {
                    curY = pageHeight - 32;

                    // Running Header on continuation pages (ASCII hyphen to avoid font substitution)
                    var runningHeader = $"PRICE COMPARISON-{plantCode} - Requisition {pr.PrNo}";
                    DrawText(runningHeader, marginLeft, curY, font: "F2", fontSize: 9.5);
                    curY -= 16;
                }

                // Table Header
                double rowH1 = 20;
                double rowH2 = 14;
                currentPage.TableTopY = curY;

                DrawRect(marginLeft, curY - rowH1 - rowH2, contentWidth, rowH1 + rowH2, lineWidth: 0.5, fillHex: "F2F4F7");

                DrawText("Sl No.", colX[0], curY - 14, font: "F2", fontSize: 8, align: "center", width: slNoWidth);
                DrawText("Item Description", colX[1], curY - 14, font: "F2", fontSize: 8, align: "center", width: descWidth);
                DrawText("Quantity", colX[2], curY - 14, font: "F2", fontSize: 8, align: "center", width: qtyWidth);

                for (int i = 0; i < supplierCount; i++)
                {
                    // Truncate the raw name first, then append the badge — appending first let the
                    // 22-char cap eat the badge for any vendor name longer than 12 characters.
                    var vendorName = selectedRfqs[i].Vendor;
                    var isPartial = selectedRfqs[i].IsPartialQuote;
                    var maxNameLen = isPartial ? 12 : 22;
                    if (vendorName.Length > maxNameLen) vendorName = vendorName.Substring(0, maxNameLen - 2) + "..";
                    if (isPartial) vendorName += " (Partial)";
                    DrawText(vendorName, colX[3 + i], curY - 13, font: "F2", fontSize: 7.5, align: "center", width: colWidth);
                    DrawText("Unit Price", colX[3 + i], curY - rowH1 - 10, font: "F2", fontSize: 7, align: "center", width: colWidth);
                }

                DrawText("Historical Price", colX[3 + supplierCount], curY - 13, font: "F2", fontSize: 7.5, align: "center", width: colWidth);
                DrawText("Unit Price", colX[3 + supplierCount], curY - rowH1 - 10, font: "F2", fontSize: 7, align: "center", width: colWidth);

                DrawLine(colX[3], curY - rowH1, marginLeft + contentWidth, curY - rowH1, width: 0.5);

                curY -= (rowH1 + rowH2);
            }

            // Start Page 1
            StartNewPage(isFirstPage: true);

            // Base currency for Last Price column
            var defaultCur = string.IsNullOrWhiteSpace(selectedRfqs.FirstOrDefault()?.Currency) ? "AED" : selectedRfqs.First().Currency.Trim();

            // RENDER LINE ITEMS
            var prItems = pr.Items?.ToList() ?? new List<PrItem>();
            int itemIndex = 1;
            decimal totalLastPriceSum = 0m;

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
                foreach (var item in prItems)
                {
                    double rowH = 20;

                    // Check if current row exceeds printable space
                    if (curY - rowH < bottomLimit + 10)
                    {
                        currentPage.TableBottomY = curY;
                        StartNewPage(isFirstPage: false);
                    }

                    DrawRect(marginLeft, curY - rowH, contentWidth, rowH, lineWidth: 0.5);

                    DrawText(itemIndex.ToString(), colX[0], curY - 13, font: "F1", fontSize: 8, align: "center", width: slNoWidth);

                    var desc = item.ItemName;
                    if (desc.Length > 36) desc = desc.Substring(0, 34) + "..";
                    DrawText(desc, colX[1] + 4, curY - 13, font: "F1", fontSize: 7.5);

                    var qtyStr = $"{item.Quantity.ToString("G29", CultureInfo.InvariantCulture)} {item.Unit}";
                    DrawText(qtyStr, colX[2], curY - 13, font: "F1", fontSize: 7.5, align: "center", width: qtyWidth);

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
                            DrawMoneyCell(colX[3 + i], colWidth, curY - 13, cur, netUnitPrice, fontSize: 7.5, showZeroAsDash: true);
                        }
                        else
                        {
                            DrawText("-", colX[3 + i], curY - 13, font: "F1", fontSize: 8, align: "center", width: colWidth);
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
                        DrawMoneyCell(colX[3 + supplierCount], colWidth, curY - 13, defaultCur, rowLastPrice, fontSize: 7.5, showZeroAsDash: true);
                    }
                    else
                    {
                        DrawText("-", colX[3 + supplierCount], curY - 13, font: "F1", fontSize: 8, align: "center", width: colWidth);
                    }

                    curY -= rowH;
                    itemIndex++;
                }
            }

            // CHECK SPACE FOR SUMMARY ROWS + REMARKS + SIGNATURE BOXES
            // Total needed height = 12 summary rows (12 * 13.5 = 162 pt) + gap (12) + remarks (20) + gap (14) + signature boxes (75) = ~283 pt
            const double footerBlockNeeded = 283;
            if (curY - footerBlockNeeded < bottomLimit)
            {
                currentPage.TableBottomY = curY;
                StartNewPage(isFirstPage: false);
            }

            // SUMMARY & FINANCIAL TERMS ROWS
            void DrawSummaryMoneyRow(string label, Func<RequestForQuotation, (decimal? amount, bool showZeroAsDash)> valFunc, decimal? lastVal, bool isBold = false)
            {
                double rowH = 13.5;
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
                double rowH = 13.5;
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

            curY -= 14;

            // REMARKS SECTION
            var remarksDisplay = string.IsNullOrWhiteSpace(remarks) ? "None" : remarks.Trim();
            DrawText($"Remarks : {remarksDisplay}", marginLeft, curY, font: "F2", fontSize: 8.5);
            curY -= 18;

            // BOTTOM SIGNATURE / APPROVER BOXES
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
            double boxHeight = 75;
            double boxTitleH = 17;

            for (int b = 0; b < boxCount; b++)
            {
                double bx = marginLeft + (b * (boxWidth + boxGap));
                double by = curY - boxHeight;

                DrawRect(bx, by, boxWidth, boxHeight, lineWidth: 0.8, fillHex: "FFFFFF");
                DrawRect(bx, by + boxHeight - boxTitleH, boxWidth, boxTitleH, lineWidth: 0.5, fillHex: "F2F4F7");
                DrawText(approverRoles[b], bx, by + boxHeight - 12, font: "F2", fontSize: 8, align: "center", width: boxWidth);
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
                pdfWriter.WriteLine($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 842 595] /Resources << /Font << /F1 {font1ObjId} 0 R /F2 {font2ObjId} 0 R >> >> /Contents {streamObjId} 0 R >>");
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
