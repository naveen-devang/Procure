using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Procure.Models;

namespace Procure.Services.Export
{
    public static class PcrExcelExporter
    {
        public static byte[] GenerateExcel(
            PurchaseRequisition pr,
            PriceComparisonRequest pcr,
            IReadOnlyList<RequestForQuotation> selectedRfqs,
            string remarks)
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                // 1. [Content_Types].xml
                AddContentTypes(archive);

                // 2. _rels/.rels
                AddRootRels(archive);

                // 3. xl/_rels/workbook.xml.rels
                AddWorkbookRels(archive);

                // 4. xl/styles.xml
                AddStyles(archive);

                // 5. xl/workbook.xml
                AddWorkbook(archive);

                // 6. xl/worksheets/sheet1.xml
                AddWorksheet(archive, pr, pcr, selectedRfqs, remarks);
            }

            return ms.ToArray();
        }

        private static void AddContentTypes(ZipArchive archive)
        {
            var entry = archive.CreateEntry("[Content_Types].xml");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
    <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>
    <Default Extension=""xml"" ContentType=""application/xml""/>
    <Override PartName=""/xl/workbook.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml""/>
    <Override PartName=""/xl/worksheets/sheet1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml""/>
    <Override PartName=""/xl/styles.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml""/>
</Types>");
        }

        private static void AddRootRels(ZipArchive archive)
        {
            var entry = archive.CreateEntry("_rels/.rels");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
    <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""xl/workbook.xml""/>
</Relationships>");
        }

        private static void AddWorkbookRels(ZipArchive archive)
        {
            var entry = archive.CreateEntry("xl/_rels/workbook.xml.rels");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
    <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"" Target=""worksheets/sheet1.xml""/>
    <Relationship Id=""rId2"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"" Target=""styles.xml""/>
</Relationships>");
        }

        private static void AddWorkbook(ZipArchive archive)
        {
            var entry = archive.CreateEntry("xl/workbook.xml");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<workbook xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"">
    <sheets>
        <sheet name=""Price Comparison"" sheetId=""1"" r:id=""rId1""/>
    </sheets>
</workbook>");
        }

        private static void AddStyles(ZipArchive archive)
        {
            var entry = archive.CreateEntry("xl/styles.xml");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<styleSheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">
    <numFmts count=""1"">
        <numFmt numFmtId=""164"" formatCode=""#,##0.00""/>
    </numFmts>
    <fonts count=""6"">
        <font><name val=""Calibri""/><sz val=""10""/></font>
        <font><b/><name val=""Calibri""/><sz val=""10""/></font>
        <font><b/><name val=""Calibri""/><sz val=""15""/></font>
        <font><b/><name val=""Calibri""/><sz val=""11""/></font>
        <font><b/><name val=""Calibri""/><sz val=""9.5""/></font>
        <font><i/><name val=""Calibri""/><sz val=""10""/></font>
    </fonts>
    <fills count=""4"">
        <fill><patternFill patternType=""none""/></fill>
        <fill><patternFill patternType=""gray125""/></fill>
        <fill><patternFill patternType=""solid""><fgColor rgb=""FFF2F4F7""/></patternFill></fill>
        <fill><patternFill patternType=""solid""><fgColor rgb=""FFE8EDF5""/></patternFill></fill>
    </fills>
    <borders count=""2"">
        <border>
            <left/><right/><top/><bottom/><diagonal/>
        </border>
        <border>
            <left style=""thin""><color rgb=""FF000000""/></left>
            <right style=""thin""><color rgb=""FF000000""/></right>
            <top style=""thin""><color rgb=""FF000000""/></top>
            <bottom style=""thin""><color rgb=""FF000000""/></bottom>
            <diagonal/>
        </border>
    </borders>
    <cellStyleXfs count=""1"">
        <xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0""/>
    </cellStyleXfs>
    <cellXfs count=""14"">
        <!-- 0: Default Normal -->
        <xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0"" xfId=""0""/>
        <!-- 1: Document Title Bold 15pt -->
        <xf numFmtId=""0"" fontId=""2"" fillId=""0"" borderId=""0"" xfId=""0""><alignment horizontal=""center"" vertical=""center""/></xf>
        <!-- 2: Meta Header Bold -->
        <xf numFmtId=""0"" fontId=""1"" fillId=""0"" borderId=""0"" xfId=""0""><alignment vertical=""center""/></xf>
        <!-- 3: Table Header Supplier Name (Bold 11pt, Light Fill, Border, Center) -->
        <xf numFmtId=""0"" fontId=""3"" fillId=""2"" borderId=""1"" xfId=""0"" applyFont=""1"" applyFill=""1"" applyBorder=""1""><alignment horizontal=""center"" vertical=""center"" wrapText=""1""/></xf>
        <!-- 4: Table Subheader (Bold 9.5pt, Light Fill, Border, Center) -->
        <xf numFmtId=""0"" fontId=""4"" fillId=""2"" borderId=""1"" xfId=""0"" applyFont=""1"" applyFill=""1"" applyBorder=""1""><alignment horizontal=""center"" vertical=""center""/></xf>
        <!-- 5: Table Text Cell (Regular, Border, Left) -->
        <xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""1"" xfId=""0"" applyBorder=""1""><alignment horizontal=""left"" vertical=""center"" wrapText=""1""/></xf>
        <!-- 6: Table Center Cell (Regular, Border, Center) -->
        <xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""1"" xfId=""0"" applyBorder=""1""><alignment horizontal=""center"" vertical=""center""/></xf>
        <!-- 7: Table Currency / Number Cell (Regular, Border, Right, #,##0.00) -->
        <xf numFmtId=""164"" fontId=""0"" fillId=""0"" borderId=""1"" xfId=""0"" applyNumberFormat=""1"" applyBorder=""1""><alignment horizontal=""right"" vertical=""center""/></xf>
        <!-- 8: Summary Row Label (Bold 10pt, Border, Left) -->
        <xf numFmtId=""0"" fontId=""1"" fillId=""0"" borderId=""1"" xfId=""0"" applyFont=""1"" applyBorder=""1""><alignment horizontal=""left"" vertical=""center""/></xf>
        <!-- 9: Summary Row Value (Bold 10pt, Border, Right, #,##0.00) -->
        <xf numFmtId=""164"" fontId=""1"" fillId=""0"" borderId=""1"" xfId=""0"" applyNumberFormat=""1"" applyFont=""1"" applyBorder=""1""><alignment horizontal=""right"" vertical=""center""/></xf>
        <!-- 10: Summary Row Center Value (Bold 10pt, Border, Center) -->
        <xf numFmtId=""0"" fontId=""1"" fillId=""0"" borderId=""1"" xfId=""0"" applyFont=""1"" applyBorder=""1""><alignment horizontal=""center"" vertical=""center""/></xf>
        <!-- 11: Signature Box Title (Bold 10pt, Fill, Border, Center) -->
        <xf numFmtId=""0"" fontId=""1"" fillId=""3"" borderId=""1"" xfId=""0"" applyFont=""1"" applyFill=""1"" applyBorder=""1""><alignment horizontal=""center"" vertical=""center""/></xf>
        <!-- 12: Signature Box Space (Border, Center) -->
        <xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""1"" xfId=""0"" applyBorder=""1""><alignment horizontal=""center"" vertical=""center""/></xf>
        <!-- 13: Remarks Cell (Font 1, Left) -->
        <xf numFmtId=""0"" fontId=""1"" fillId=""0"" borderId=""0"" xfId=""0""><alignment horizontal=""left"" vertical=""top"" wrapText=""1""/></xf>
    </cellXfs>
</styleSheet>");
        }

        // No live text measurement is available in this hand-written OOXML writer, so wrapping is
        // estimated from character count against the column's own fixed width (set in <cols>
        // above) rather than measured pixel-for-pixel - close enough to size the row, since Excel's
        // own wrapText does the real wrapping once the row is tall enough to show it.
        private static int EstimateWrappedLineCount(string text, int charsPerLine, int maxLines)
        {
            if (string.IsNullOrEmpty(text)) return 1;
            var lines = (int)Math.Ceiling(text.Length / (double)charsPerLine);
            return Math.Clamp(lines, 1, maxLines);
        }

        private static void AddWorksheet(
            ZipArchive archive,
            PurchaseRequisition pr,
            PriceComparisonRequest pcr,
            IReadOnlyList<RequestForQuotation> selectedRfqs,
            string remarks)
        {
            var entry = archive.CreateEntry("xl/worksheets/sheet1.xml");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);

            int supplierCount = selectedRfqs.Count;
            // Columns: A (Sl No), B (Item Description), C (Quantity), D.. (Suppliers), Last (Last Price)
            int totalCols = 3 + supplierCount + 1; // e.g. 3 + 5 + 1 = 9 cols (A through I)
            string lastColLetter = GetColumnLetter(totalCols);

            // Description column only widens as far as its own longest item name actually needs -
            // not simply however many (or few) vendor columns there are, which used to hand a short
            // name like "GYPSUM" the same oversized column as a genuinely long description. No live
            // text measurement is available in this hand-written writer, so character count against
            // the column's own width (set below) stands in for it - same idea as EstimateWrappedLineCount.
            // Still bounded both ways: never narrower than the old fixed 42, never so wide it crowds
            // vendor/price columns below a floor that would force a long vendor name into 5-6 wrapped lines.
            const int descColWidthFloor = 42;
            const int descColWidthCap = 70;
            const int vendorColWidth = 18;
            var widestItemNameLength = pr.Items?.Count > 0 ? pr.Items.Max(i => i.ItemName?.Length ?? 0) : 0;
            int descColWidth = Math.Clamp(widestItemNameLength + 4, descColWidthFloor, descColWidthCap);

            var sb = new StringBuilder();
            sb.Append(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<worksheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">
    <sheetViews>
        <sheetView tabSelected=""1"" workbookViewId=""0""/>
    </sheetViews>
    <sheetFormatPr defaultRowHeight=""18""/>
    <cols>
        <col min=""1"" max=""1"" width=""8"" customWidth=""1""/>
        <col min=""2"" max=""2"" width=""{descColWidth}"" customWidth=""1""/>
        <col min=""3"" max=""3"" width=""12"" customWidth=""1""/>");

            for (int i = 4; i <= totalCols; i++)
            {
                sb.Append($@"
        <col min=""{i}"" max=""{i}"" width=""{vendorColWidth}"" customWidth=""1""/>");
            }

            sb.Append(@"
    </cols>
    <sheetData>");

            int r = 2; // Start row
            var merges = new List<string>();

            // 1. Title: PRICE COMPARISON-{Plant}
            var plantCode = string.IsNullOrWhiteSpace(pr.Plant) ? "RW01" : pr.Plant.Trim();
            var title = $"PRICE COMPARISON-{plantCode}";
            sb.Append($@"
        <row r=""{r}"" ht=""28"">
            <c r=""B{r}"" s=""1"" t=""inlineStr""><is><t>{EscapeXml(title)}</t></is></c>
        </row>");
            merges.Add($"B{r}:{lastColLetter}{r}");
            r += 2;

            // 2. Metadata Header Rows (3 structured rows matching PDF layout)
            var collectiveNo = string.IsNullOrWhiteSpace(pcr.PcrNo) ? $"PCR-{pr.PrNo}" : pcr.PcrNo;
            var dateStr = DateTime.Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            // Raw string here — EscapeXml at the point of use escapes it exactly once.
            var reqFor = string.IsNullOrWhiteSpace(pr.Description) ? "E&I" : pr.Description.Trim();
            var prDisplay = PcrPdfExporter.FormatPrNumbers(pr);
            var rfqDisplay = PcrPdfExporter.FormatRfqNumbers(selectedRfqs);

            int rightCol = Math.Max(4, totalCols - 1);
            string rightColLetter = GetColumnLetter(rightCol);

            // Row 1: Date & Collective Number
            sb.Append($@"
        <row r=""{r}"" ht=""20"">
            <c r=""A{r}"" s=""2"" t=""inlineStr""><is><t>Date : {dateStr}</t></is></c>
            <c r=""{rightColLetter}{r}"" s=""2"" t=""inlineStr""><is><t>Collective Number : {EscapeXml(collectiveNo)}</t></is></c>
        </row>");
            if (totalCols > rightCol) merges.Add($"{rightColLetter}{r}:{lastColLetter}{r}");
            r++;

            // Row 2: Requested By & Requested For
            sb.Append($@"
        <row r=""{r}"" ht=""20"">
            <c r=""A{r}"" s=""2"" t=""inlineStr""><is><t>Requested By : {EscapeXml(pr.Requestor)}</t></is></c>
            <c r=""{rightColLetter}{r}"" s=""2"" t=""inlineStr""><is><t>Requested For : {EscapeXml(reqFor)}</t></is></c>
        </row>");
            if (totalCols > rightCol) merges.Add($"{rightColLetter}{r}:{lastColLetter}{r}");
            r++;

            // Row 3: PR Number & RFQ Number
            sb.Append($@"
        <row r=""{r}"" ht=""20"">
            <c r=""A{r}"" s=""2"" t=""inlineStr""><is><t>PR Number : {EscapeXml(prDisplay)}</t></is></c>
            <c r=""{rightColLetter}{r}"" s=""2"" t=""inlineStr""><is><t>RFQ Number : {EscapeXml(rfqDisplay)}</t></is></c>
        </row>");
            if (totalCols > rightCol) merges.Add($"{rightColLetter}{r}:{lastColLetter}{r}");
            r += 2;

            // 4. Table Main Header Row (Row r): Sl No, Item Description, Quantity, Supplier Names..., Last Price
            int tableHeaderRow1 = r;

            // Header texts are built up front so the row's own height can be sized to whichever
            // vendor's name is longest, before the <row> tag is written - the cell style already
            // has wrapText+vertical-center on, so once the row is tall enough, a short name centers
            // in it for free and only the long one actually needs the second line.
            var vendorHeaderTexts = new string[supplierCount];
            for (int i = 0; i < supplierCount; i++)
            {
                var rfqHeader = selectedRfqs[i];
                var vendorName = string.IsNullOrWhiteSpace(rfqHeader.Vendor) ? $"Supplier {i + 1}" : rfqHeader.Vendor;
                // The sheet's money cells are bare numbers, so the currency must ride on the column
                // header or mixed-currency quotes compare as equals. No partial-quote marker here -
                // an unquoted item already prints "-" in that vendor's own price cell.
                var vendorCur = string.IsNullOrWhiteSpace(rfqHeader.Currency) ? "AED" : rfqHeader.Currency;
                vendorHeaderTexts[i] = $"{vendorName} ({vendorCur})";
            }

            const int headerCharsPerLine = 16; // vendor columns are fixed at width="18"
            var headerLines = vendorHeaderTexts
                .Select(t => EstimateWrappedLineCount(t, headerCharsPerLine, maxLines: 2))
                .DefaultIfEmpty(1)
                .Max();
            var headerRowHeight = headerLines > 1 ? 44 : 28;

            sb.Append($@"
        <row r=""{r}"" ht=""{headerRowHeight}"">
            <c r=""A{r}"" s=""3"" t=""inlineStr""><is><t>Sl No.</t></is></c>
            <c r=""B{r}"" s=""3"" t=""inlineStr""><is><t>Item Description</t></is></c>
            <c r=""C{r}"" s=""3"" t=""inlineStr""><is><t>Quantity</t></is></c>");

            for (int i = 0; i < supplierCount; i++)
            {
                string colLetter = GetColumnLetter(4 + i);
                sb.Append($@"
            <c r=""{colLetter}{r}"" s=""3"" t=""inlineStr""><is><t>{EscapeXml(vendorHeaderTexts[i])}</t></is></c>");
            }

            string lastPriceColLetter = GetColumnLetter(totalCols);
            var historicalCur = selectedRfqs.Count > 0 && !string.IsNullOrWhiteSpace(selectedRfqs[0].Currency) ? selectedRfqs[0].Currency : "AED";
            sb.Append($@"
            <c r=""{lastPriceColLetter}{r}"" s=""3"" t=""inlineStr""><is><t>{EscapeXml($"Historical Price ({historicalCur})")}</t></is></c>
        </row>");
            r++;

            // 5. Table Sub-Header Row (Row r): Unit Price sub-headers
            int tableHeaderRow2 = r;
            sb.Append($@"
        <row r=""{r}"" ht=""20"">
            <c r=""A{r}"" s=""4"" t=""inlineStr""><is><t></t></is></c>
            <c r=""B{r}"" s=""4"" t=""inlineStr""><is><t></t></is></c>
            <c r=""C{r}"" s=""4"" t=""inlineStr""><is><t></t></is></c>");

            for (int i = 0; i < supplierCount; i++)
            {
                string colLetter = GetColumnLetter(4 + i);
                sb.Append($@"
            <c r=""{colLetter}{r}"" s=""4"" t=""inlineStr""><is><t>Unit Price</t></is></c>");
            }

            sb.Append($@"
            <c r=""{lastPriceColLetter}{r}"" s=""4"" t=""inlineStr""><is><t>Unit Price</t></is></c>
        </row>");
            merges.Add($"A{tableHeaderRow1}:A{tableHeaderRow2}");
            merges.Add($"B{tableHeaderRow1}:B{tableHeaderRow2}");
            merges.Add($"C{tableHeaderRow1}:C{tableHeaderRow2}");
            r++;

            // 6. Line Item Rows
            var prItems = pr.Items?.ToList() ?? new List<PrItem>();
            int itemIndex = 1;
            decimal totalLastPriceSum = 0m;

            if (prItems.Count == 0)
            {
                // Fallback for PRs with no item rows
                sb.Append($@"
        <row r=""{r}"" ht=""22"">
            <c r=""A{r}"" s=""6"" t=""inlineStr""><is><t>1</t></is></c>
            <c r=""B{r}"" s=""5"" t=""inlineStr""><is><t>{EscapeXml(pr.Description)}</t></is></c>
            <c r=""C{r}"" s=""6"" t=""inlineStr""><is><t>{pr.ItemsCount}</t></is></c>");

                for (int i = 0; i < supplierCount; i++)
                {
                    string colLetter = GetColumnLetter(4 + i);
                    var rfq = selectedRfqs[i];
                    var quoteAmt = rfq.BaseAmount > 0 ? rfq.BaseAmount : (rfq.QuoteAmount ?? 0);
                    sb.Append($@"
            <c r=""{colLetter}{r}"" s=""7""><v>{quoteAmt.ToString(CultureInfo.InvariantCulture)}</v></c>");
                }

                sb.Append($@"
            <c r=""{lastPriceColLetter}{r}"" s=""7""><v>0.00</v></c>
        </row>");
                r++;
            }
            else
            {
                var descCharsPerLine = descColWidth - 2; // tracks the column's own (now variable) width
                foreach (var item in prItems)
                {
                    // Sized per-row from this item's own name only, not uniformly across every
                    // item row - a five-word item three rows down growing its own row shouldn't
                    // change the height of every short one-word row around it.
                    var descLines = EstimateWrappedLineCount(item.ItemName, descCharsPerLine, maxLines: 3);
                    var itemRowHeight = 20 + (descLines * 16);

                    sb.Append($@"
        <row r=""{r}"" ht=""{itemRowHeight}"">
            <c r=""A{r}"" s=""6"" t=""inlineStr""><is><t>{itemIndex++}</t></is></c>
            <c r=""B{r}"" s=""5"" t=""inlineStr""><is><t>{EscapeXml(item.ItemName)}</t></is></c>
            <c r=""C{r}"" s=""6"" t=""inlineStr""><is><t>{item.Quantity.ToString("G29", CultureInfo.InvariantCulture)} {EscapeXml(item.Unit)}</t></is></c>");

                    decimal rowLastPrice = item.EstimatedUnitPrice ?? 0m;
                    bool rowLastPriceFromQuote = false;

                    for (int i = 0; i < supplierCount; i++)
                    {
                        string colLetter = GetColumnLetter(4 + i);
                        var rfq = selectedRfqs[i];
                        // Exact PrItemId link wins; name matching is only a fallback for unlinked
                        // lines — a flat OR let a name collision beat the correct link.
                        var rfqItem = rfq.Items?.FirstOrDefault(ri => ri.PrItemId.HasValue && ri.PrItemId.Value == item.Id)
                                   ?? rfq.Items?.FirstOrDefault(ri => !ri.PrItemId.HasValue && string.Equals(ri.ItemName, item.ItemName, StringComparison.OrdinalIgnoreCase));

                        if (rfqItem?.QuotedUnitPrice != null && rfqItem.QuotedUnitPrice.Value > 0)
                        {
                            // Net of the per-unit discount, so qty x printed price reconciles with
                            // the Total Price Excl. VAT row (which sums discounted line totals).
                            var netUnitPrice = Math.Max(0m, rfqItem.QuotedUnitPrice.Value - (rfqItem.Discount ?? 0m));
                            sb.Append($@"
            <c r=""{colLetter}{r}"" s=""7""><v>{netUnitPrice.ToString(CultureInfo.InvariantCulture)}</v></c>");
                        }
                        else if (rfq.BaseAmount > 0 && prItems.Count == 1)
                        {
                            sb.Append($@"
            <c r=""{colLetter}{r}"" s=""7""><v>{rfq.BaseAmount.ToString(CultureInfo.InvariantCulture)}</v></c>");
                        }
                        else
                        {
                            sb.Append($@"
            <c r=""{colLetter}{r}"" s=""6"" t=""inlineStr""><is><t>-</t></is></c>");
                        }

                        // First supplier (in fixed pr.Rfqs order) with a historical price wins;
                        // last-wins made the printed baseline depend on which vendor sat last.
                        if (!rowLastPriceFromQuote && rfqItem?.LastPrice != null && rfqItem.LastPrice.Value > 0)
                        {
                            rowLastPrice = rfqItem.LastPrice.Value;
                            rowLastPriceFromQuote = true;
                        }
                    }

                    totalLastPriceSum += (item.Quantity * rowLastPrice);

                    if (rowLastPrice > 0)
                    {
                        sb.Append($@"
            <c r=""{lastPriceColLetter}{r}"" s=""7""><v>{rowLastPrice.ToString(CultureInfo.InvariantCulture)}</v></c>");
                    }
                    else
                    {
                        sb.Append($@"
            <c r=""{lastPriceColLetter}{r}"" s=""6"" t=""inlineStr""><is><t>-</t></is></c>");
                    }

                    sb.Append(@"
        </row>");
                    r++;
                }
            }

            // 7. Financial Summary Rows
            // Helper for summary rows
            void AddSummaryRow(string label, Func<RequestForQuotation, (bool isNum, decimal numVal, string strVal)> valSelector, (bool isNum, decimal numVal, string strVal) lastPriceVal)
            {
                sb.Append($@"
        <row r=""{r}"" ht=""20"">
            <c r=""A{r}"" s=""8"" t=""inlineStr""><is><t></t></is></c>
            <c r=""B{r}"" s=""8"" t=""inlineStr""><is><t>{EscapeXml(label)}</t></is></c>
            <c r=""C{r}"" s=""8"" t=""inlineStr""><is><t></t></is></c>");
                merges.Add($"B{r}:C{r}");

                for (int i = 0; i < supplierCount; i++)
                {
                    string colLetter = GetColumnLetter(4 + i);
                    var val = valSelector(selectedRfqs[i]);
                    if (val.isNum)
                    {
                        sb.Append($@"
            <c r=""{colLetter}{r}"" s=""9""><v>{val.numVal.ToString(CultureInfo.InvariantCulture)}</v></c>");
                    }
                    else
                    {
                        sb.Append($@"
            <c r=""{colLetter}{r}"" s=""10"" t=""inlineStr""><is><t>{EscapeXml(val.strVal)}</t></is></c>");
                    }
                }

                if (lastPriceVal.isNum)
                {
                    sb.Append($@"
            <c r=""{lastPriceColLetter}{r}"" s=""9""><v>{lastPriceVal.numVal.ToString(CultureInfo.InvariantCulture)}</v></c>");
                }
                else
                {
                    sb.Append($@"
            <c r=""{lastPriceColLetter}{r}"" s=""10"" t=""inlineStr""><is><t>{EscapeXml(lastPriceVal.strVal)}</t></is></c>");
                }

                sb.Append(@"
        </row>");
                r++;
            }

            // An unquoted vendor prints "-" in the money rows — a numeric 0.00 read as a zero
            // quote. Gated on quote PRESENCE, not amount, so a genuine zero-net quote (e.g. fully
            // discounted lines) still prints its numbers.
            static (bool isNum, decimal numVal, string strVal) MoneyOrDash(RequestForQuotation rf, decimal value) =>
                (rf.IsQuoteReceived || rf.PricedItemsCount > 0) ? (true, value, "") : (false, 0m, "-");

            // Row 1: Total Price Excl. VAT
            AddSummaryRow("Total Price Excl. VAT",
                rf => MoneyOrDash(rf, rf.BaseAmount > 0 ? rf.BaseAmount : (rf.QuoteAmount ?? 0m)),
                (true, totalLastPriceSum, ""));

            // Row 2: Discount
            AddSummaryRow("Discount",
                rf => (rf.Discount.HasValue && rf.Discount.Value > 0) ? (true, rf.Discount.Value, "") : (false, 0m, "-"),
                (false, 0m, "-"));

            // Row 3: Total Price Excl. VAT After Discount (unclamped — the model clamps only the
            // final NetTaxable, and an early clamp made the printed rows fail arithmetic checks)
            AddSummaryRow("Total Price Excl. VAT After Discount",
                rf => MoneyOrDash(rf, (rf.BaseAmount > 0 ? rf.BaseAmount : (rf.QuoteAmount ?? 0m)) - (rf.Discount ?? 0m)),
                (true, totalLastPriceSum, ""));

            // Row 4: Freight/Shipping Charges
            AddSummaryRow("Freight/Shipping Charges",
                rf => (rf.Freight.HasValue && rf.Freight.Value > 0) ? (true, rf.Freight.Value, "") : (false, 0m, "-"),
                (false, 0m, "-"));

            // Row 5: Other Charges
            AddSummaryRow("Other Charges",
                rf => (rf.OtherCharges.HasValue && rf.OtherCharges.Value > 0) ? (true, rf.OtherCharges.Value, "") : (false, 0m, "-"),
                (false, 0m, "-"));

            // The historical column's VAT treatment follows the compared quotes' predominant
            // VatType instead of a hardcoded 5% (which inflated it against RC/V0 quotes).
            var historicalVatType = selectedRfqs.Count > 0
                ? selectedRfqs.GroupBy(rf => string.IsNullOrWhiteSpace(rf.VatType) ? "5%" : rf.VatType)
                    .OrderByDescending(g => g.Count()).Select(g => g.Key).First()
                : "5%";

            // Row 6: VAT
            AddSummaryRow("VAT",
                rf => (false, 0m, string.IsNullOrWhiteSpace(rf.VatType) ? "5%" : rf.VatType),
                (false, 0m, historicalVatType));

            // Row 7: Total Price Incl. VAT
            decimal lastPriceInclVat = historicalVatType == "5%" ? totalLastPriceSum * 1.05m : totalLastPriceSum;
            AddSummaryRow("Total Price Incl. VAT",
                rf => MoneyOrDash(rf, rf.TotalLandedCost),
                (true, lastPriceInclVat, ""));

            // Row 8: Payment Terms
            AddSummaryRow("Payment Terms",
                rf => (false, 0m, string.IsNullOrWhiteSpace(rf.PaymentTerms) ? "30 Days Net" : rf.PaymentTerms),
                (false, 0m, "-"));

            // Row 9: Delivery Terms
            AddSummaryRow("Delivery Terms",
                rf => (false, 0m, string.IsNullOrWhiteSpace(rf.Incoterms) ? "DDP" : rf.Incoterms),
                (false, 0m, "-"));

            // Row 10: Lead Time (Days)
            AddSummaryRow("Lead Time (Days)",
                rf => (false, 0m, string.IsNullOrWhiteSpace(rf.DeliveryLeadTime) ? "-" : rf.DeliveryLeadTime),
                (false, 0m, "-"));

            // Row 11: Warranty
            AddSummaryRow("Warranty",
                rf => (false, 0m, string.IsNullOrWhiteSpace(rf.Warranty) ? "-" : rf.Warranty),
                (false, 0m, "-"));

            // Row 12: Technical Approval — blank means "not recorded"; substituting "Approved"
            // fabricated approval status on a signature document.
            AddSummaryRow("Technical Approval",
                rf => (false, 0m, string.IsNullOrWhiteSpace(rf.TechnicalApproval) ? "-" : rf.TechnicalApproval),
                (false, 0m, "-"));

            r += 1; // Gap before remarks

            // 8. Remarks Section
            var remarksDisplay = string.IsNullOrWhiteSpace(remarks) ? "None" : remarks.Trim();
            sb.Append($@"
        <row r=""{r}"" ht=""28"">
            <c r=""A{r}"" s=""13"" t=""inlineStr""><is><t>Remarks : {EscapeXml(remarksDisplay)}</t></is></c>
        </row>");
            merges.Add($"A{r}:{lastColLetter}{r}");
            r += 2; // Gap before signature boxes

            // 9. Bottom Signature Boxes: Buyer first, then active Approvers
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

            int sigBoxTopRow = r;
            sb.Append($@"
        <row r=""{sigBoxTopRow}"" ht=""22"">");

            int boxCount = approverRoles.Count;
            // Distribute columns evenly across totalCols (from col A (1) to totalCols)
            // For example if 5 boxes and 9 cols: each box gets 1-2 columns
            int colsPerBox = Math.Max(1, totalCols / boxCount);

            for (int b = 0; b < boxCount; b++)
            {
                int startCol = 1 + (b * colsPerBox);
                int endCol = (b == boxCount - 1) ? totalCols : Math.Min(totalCols, startCol + colsPerBox - 1);
                string startColLet = GetColumnLetter(startCol);
                string endColLet = GetColumnLetter(endCol);

                sb.Append($@"
            <c r=""{startColLet}{sigBoxTopRow}"" s=""11"" t=""inlineStr""><is><t>{EscapeXml(approverRoles[b])}</t></is></c>");

                if (endCol > startCol)
                {
                    for (int c = startCol + 1; c <= endCol; c++)
                    {
                        string cLet = GetColumnLetter(c);
                        sb.Append($@"
            <c r=""{cLet}{sigBoxTopRow}"" s=""11""/>");
                    }
                    merges.Add($"{startColLet}{sigBoxTopRow}:{endColLet}{sigBoxTopRow}");
                }
            }

            sb.Append(@"
        </row>");

            // 4 rows of empty space for physical/digital signature
            for (int sRow = 1; sRow <= 4; sRow++)
            {
                int currRow = sigBoxTopRow + sRow;
                sb.Append($@"
        <row r=""{currRow}"" ht=""20"">");

                for (int b = 0; b < boxCount; b++)
                {
                    int startCol = 1 + (b * colsPerBox);
                    int endCol = (b == boxCount - 1) ? totalCols : Math.Min(totalCols, startCol + colsPerBox - 1);
                    for (int c = startCol; c <= endCol; c++)
                    {
                        string cLet = GetColumnLetter(c);
                        sb.Append($@"
            <c r=""{cLet}{currRow}"" s=""12""/>");
                    }
                    if (endCol > startCol)
                    {
                        merges.Add($"{GetColumnLetter(startCol)}{currRow}:{GetColumnLetter(endCol)}{currRow}");
                    }
                }

                sb.Append(@"
        </row>");
            }

            sb.Append(@"
    </sheetData>");

            // Merged cells list
            if (merges.Count > 0)
            {
                sb.Append($@"
    <mergeCells count=""{merges.Count}"">");
                foreach (var m in merges)
                {
                    sb.Append($@"
        <mergeCell ref=""{m}""/>");
                }
                sb.Append(@"
    </mergeCells>");
            }

            sb.Append(@"
</worksheet>");

            writer.Write(sb.ToString());
        }

        private static string GetColumnLetter(int colIndex)
        {
            int div = colIndex;
            string colLetter = string.Empty;
            while (div > 0)
            {
                int mod = (div - 1) % 26;
                colLetter = (char)(65 + mod) + colLetter;
                div = (div - mod) / 26;
            }
            return colLetter;
        }

        private static string EscapeXml(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return input
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }
    }
}
