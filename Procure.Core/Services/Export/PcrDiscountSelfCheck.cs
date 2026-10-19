using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Procure.Models;
using Procure.Utilities;

namespace Procure.Services.Export;

/// <summary>
/// The runnable check behind the discount display in the PCR exports: a discounted unit price
/// prints the struck list price, the price paid under it, and - for a foreign currency with the
/// local-equivalent switch on - the local amount of the price PAID, never of the list price.
///
/// Builds everything in memory and reads the generated spreadsheet and PDF back. Run it by
/// launching a Debug build with PROCURE_SELFCHECK=1. No database is touched.
/// </summary>
internal static class PcrDiscountSelfCheck
{
    // The grey the PDF draws the struck list price in (0x6B / 255), as the content stream prints it.
    private const string StruckFill = "0.420 0.420 0.420 rg";
    private const string StruckStroke = "0.420 0.420 0.420 RG";

    public static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;   // the sheet formats numbers with the current culture
        try
        {
            LineDiscountMaths();
            ExcelShowsListPriceStruckAndConvertsThePricePaid();
            ExcelWithoutConversion();
            NothingChangesWithoutDiscounts();
            PdfShowsListPriceStruckAndConvertsThePricePaid();
            PdfSurvivesEveryLayout();
            TotalsRows();
            TotalsRowsSurviveEveryLayout();
            HistoricalPriceWithoutACurrencyIsAed();
            Debug.WriteLine("PCR DISCOUNT SELF-CHECKS PASSED");
            CrashLog.Write("PCR DISCOUNT SELF-CHECKS PASSED");
        }
        catch (Exception ex)
        {
            Debug.WriteLine("PCR DISCOUNT SELF-CHECKS FAILED: " + ex);
            CrashLog.Write("PCR DISCOUNT SELF-CHECKS FAILED", ex);
            throw;
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    // ---- the maths ------------------------------------------------------------------------------

    private static void LineDiscountMaths()
    {
        // (price, discount, quoted) -> is there a list price to strike?
        var cases = new (decimal? Price, decimal? Disc, bool Quoted, bool Has)[]
        {
            (100m, 10m,   true,  true),    // the ordinary case
            (100m, null,  true,  false),   // no discount typed
            (100m, 0m,    true,  false),   // typed zero
            (100m, -5m,   true,  false),   // a negative number is not a discount
            (100m, 100m,  true,  false),   // wipes the price out: prints the dash, nothing to strike
            (100m, 150m,  true,  false),   // more than the price only ever removes the price
            (100m, 99.99m, true, true),    // one cent left is still a price
            (null, 10m,   true,  false),   // a discount on a line with no price
            (0m,   10m,   true,  false),   // price typed as zero
            (100m, 10m,   false, false),   // a line the vendor did not quote
        };
        foreach (var c in cases)
        {
            var item = new RfqItem { Quantity = 3, IsQuoted = c.Quoted, QuotedUnitPrice = c.Price, Discount = c.Disc };
            Assert(item.HasLineDiscount == c.Has, $"HasLineDiscount for price {c.Price} discount {c.Disc} quoted {c.Quoted}; got {item.HasLineDiscount}");
        }
    }

    // ---- the spreadsheet -------------------------------------------------------------------------

    private static void ExcelShowsListPriceStruckAndConvertsThePricePaid()
    {
        var (pr, rfqs) = Mixed();
        var sheet = ReadSheetXml(PcrExcelExporter.GenerateExcel(pr, Pcr(pr), rfqs, "remarks", null, AedAt(4m)));

        Assert(Count(sheet, "<strike/>") == 2, $"two lines are discounted, so two list prices are struck; got {Count(sheet, "<strike/>")}");
        Assert(sheet.Contains(">EUR 120.00<"), "the struck list price is printed");
        Assert(sheet.Contains("EUR 110.00\n~ AED 440.00"), "the price paid is printed with the AED equivalent of 110, not 120");
        Assert(!sheet.Contains("480.00"), "the list price is never converted");
        Assert(sheet.Contains(">AED 100.00<") && sheet.Contains("AED 80.00"), "a local-currency vendor gets struck list and price paid, with no conversion");
        Assert(!sheet.Contains("Item Discounts"), "the summary rows are unchanged: no extra discount row");
        Assert(sheet.Contains("~ AED 180.00"), "an undiscounted foreign price still converts as before");
        AssertWellFormed(sheet);
    }

    private static void ExcelWithoutConversion()
    {
        var (pr, rfqs) = Mixed();
        foreach (var conv in new PcrCurrencyConversionOptions?[] { null, AedAt(4m, show: false) })
        {
            var sheet = ReadSheetXml(PcrExcelExporter.GenerateExcel(pr, Pcr(pr), rfqs, "remarks", null, conv));
            Assert(Count(sheet, "<strike/>") == 2, "list prices are struck with the switch off");
            Assert(sheet.Contains("EUR 110.00") && !sheet.Contains("~ AED"), "and nothing is converted");
            Assert(sheet.Contains(">45<"), "an undiscounted price is still a plain number");
            AssertWellFormed(sheet);
        }

        // Discount that wipes the price out, and one larger than the price: nothing to strike, no throw.
        var p2 = NewPr(("A", 2m));
        var wipe = Rfq(p2, "Wipe", "EUR", Line(p2.Items[0], 5m, 5m));
        var over = Rfq(p2, "Over", "EUR", Line(p2.Items[0], 5m, 8m));
        var sheet2 = ReadSheetXml(PcrExcelExporter.GenerateExcel(p2, Pcr(p2), new[] { wipe, over }, "x", null, AedAt(4m)));
        Assert(Count(sheet2, "<strike/>") == 0, "a fully discounted price has no list price to strike");
        AssertWellFormed(sheet2);
    }

    private static void NothingChangesWithoutDiscounts()
    {
        var pr = NewPr(("A", 10m), ("B", 4m));
        var rfqs = new List<RequestForQuotation>
        {
            Rfq(pr, "Plain", "EUR", Line(pr.Items[0], 120m, null), Line(pr.Items[1], 45m, 0m)),
        };
        var sheet = ReadSheetXml(PcrExcelExporter.GenerateExcel(pr, Pcr(pr), rfqs, "x", null, AedAt(4m)));
        Assert(Count(sheet, "<strike/>") == 0, "a sheet with no discounts has nothing struck");

        var pdf = Latin1(PcrPdfExporter.GeneratePdf(pr, Pcr(pr), rfqs, "x", null, null, AedAt(4m)));
        Assert(!pdf.Contains(StruckFill) && !pdf.Contains(StruckStroke), "and neither does the PDF");
    }

    // ---- the PDF ---------------------------------------------------------------------------------

    private static void PdfShowsListPriceStruckAndConvertsThePricePaid()
    {
        var (pr, rfqs) = Mixed();
        var pdf = Latin1(PcrPdfExporter.GeneratePdf(pr, Pcr(pr), rfqs, "x", null, null, AedAt(4m)));
        Assert(pdf.Contains(StruckFill), "the list price is drawn in grey");
        Assert(pdf.Contains(StruckStroke), "and has a line through it");
        Assert(pdf.Contains("(120.00) Tj") && pdf.Contains("(110.00) Tj"), "list price and price paid are both printed");
        Assert(pdf.Contains("(440.00) Tj"), "the AED equivalent is of the price paid");
        Assert(!pdf.Contains("(480.00) Tj"), "and the list price is never converted");
        Assert(!pdf.Contains("Item Discounts"), "the summary rows are unchanged: no extra discount row");

        var off = Latin1(PcrPdfExporter.GeneratePdf(pr, Pcr(pr), rfqs, "x", null, null, AedAt(4m, show: false)));
        Assert(off.Contains(StruckFill) && !off.Contains("(440.00) Tj"), "with the switch off the list price still shows, and nothing converts");

        // The discounted row is one line taller than it would be, never shorter.
        var plain = NewPr(("A", 10m), ("B", 4m));
        var noDisc = new List<RequestForQuotation> { Rfq(plain, "Alpha", "EUR", Line(plain.Items[0], 110m, null), Line(plain.Items[1], 45m, null)) };
        var withDisc = new List<RequestForQuotation> { Rfq(plain, "Alpha", "EUR", Line(plain.Items[0], 120m, 10m), Line(plain.Items[1], 45m, null)) };
        var flat = Latin1(PcrPdfExporter.GeneratePdf(plain, Pcr(plain), noDisc, "x"));
        var tall = Latin1(PcrPdfExporter.GeneratePdf(plain, Pcr(plain), withDisc, "x"));
        Assert(tall.Length > flat.Length, "the discounted sheet carries the extra line");
    }

    private static void PdfSurvivesEveryLayout()
    {
        var (pr, rfqs) = Mixed();

        // Small paper, scaled down.
        var a6 = Latin1(PcrPdfExporter.GeneratePdf(pr, Pcr(pr), rfqs, "x",
            new PcrPdfOptions { PaperSize = PdfPaperSize.A6, Orientation = PdfOrientation.Portrait }, null, AedAt(4m)));
        Assert(a6.Contains(StruckFill), "A6 portrait still prints the struck list price");

        // Fit to one page, with and without a long item list.
        var fit = Latin1(PcrPdfExporter.GeneratePdf(pr, Pcr(pr), rfqs, "x",
            new PcrPdfOptions { LayoutMode = PdfLayoutMode.ShrinkToFit }, null, AedAt(4m)));
        Assert(fit.Contains(StruckFill), "fit-to-one-page still prints the struck list price");

        // Seven vendors: the currency moves into the heading and the figure is centred in its cell.
        var many = NewPr(("A", 10m), ("B", 4m));
        var seven = Enumerable.Range(0, 7)
            .Select(i => Rfq(many, $"Vendor {i}", i % 2 == 0 ? "EUR" : "AED",
                Line(many.Items[0], 120m + i, 10m), Line(many.Items[1], 45m, i % 3 == 0 ? 5m : null)))
            .ToList();
        var wide = Latin1(PcrPdfExporter.GeneratePdf(many, Pcr(many), seven, "x", null, null, AedAt(4m)));
        Assert(wide.Contains(StruckFill) && wide.Contains(StruckStroke), "seven vendors still print the struck list price");

        // Eighty discounted lines are too long to shrink onto one page, so they paginate without
        // throwing, and every row still gets its line.
        var longPr = NewPr(Enumerable.Range(1, 80).Select(i => ($"ITEM {i}", 3m)).ToArray());
        var longRfq = Rfq(longPr, "Long", "EUR", longPr.Items.Select(p => Line(p, 50m, 5m)).ToArray());
        var longPdf = Latin1(PcrPdfExporter.GeneratePdf(longPr, Pcr(longPr), new[] { longRfq }, "x", null, null, AedAt(4m)));
        Assert(Count(longPdf, "/Type /Page ") > 1, "eighty discounted rows run onto more than one page");
        Assert(Count(longPdf, StruckStroke) >= 80, "every discounted row has its line");

        // A price too wide for its column and a wide currency code: shrinks, never overflows or throws.
        var big = NewPr(("A", 1000m));
        var bigRfq = Rfq(big, "Big", "KWD", Line(big.Items[0], 98765432.10m, 1234567.89m));
        var bigPdf = Latin1(PcrPdfExporter.GeneratePdf(big, Pcr(big), new[] { bigRfq }, "x", null, null, AedAt(11.9m)));
        Assert(bigPdf.Contains(StruckFill), "very large figures still print the struck list price");

        // Nothing quoted at all.
        var empty = NewPr(("A", 1m));
        var none = Rfq(empty, "None", "EUR");
        var nonePdf = Latin1(PcrPdfExporter.GeneratePdf(empty, Pcr(empty), new[] { none }, "x", null, null, AedAt(4m)));
        Assert(!nonePdf.Contains(StruckFill), "a vendor with no quote has nothing struck");
    }

    // ---- the totals: Taxable Amount row and the VAT cell -----------------------------------------

    private static void TotalsRows()
    {
        // Items 1,200 - discount 100 + freight 50 + other 30 = 1,180 taxable; 5% VAT = 59; total 1,239.
        var (pr, rfqs) = Charged("AED");
        var sheet = ReadSheetXml(PcrExcelExporter.GenerateExcel(pr, Pcr(pr), rfqs, "x"));
        int At(string s) => sheet.IndexOf(s, StringComparison.Ordinal);
        Assert(At(">Other Charges<") > 0 && At(">Other Charges<") < At(">Taxable Amount<")
               && At(">Taxable Amount<") < At(">VAT<") && At(">VAT<") < At(">Total Price Incl. VAT<"),
            "the spreadsheet runs Other Charges, Taxable Amount, VAT, Total Incl. VAT");
        Assert(sheet.Contains("<v>1180"), "the taxable amount is 1,180");
        Assert(sheet.Contains(">5%<") && sheet.Contains("<v>59"), "the VAT cell has the rate and the amount of 59");
        Assert(sheet.Contains("<v>1239"), "and the total is unchanged");
        AssertWellFormed(sheet);

        var pdf = Latin1(PcrPdfExporter.GeneratePdf(pr, Pcr(pr), rfqs, "x"));
        Assert(pdf.IndexOf("(Other Charges) Tj", StringComparison.Ordinal) < pdf.IndexOf("(Taxable Amount) Tj", StringComparison.Ordinal)
               && pdf.IndexOf("(Taxable Amount) Tj", StringComparison.Ordinal) < pdf.IndexOf("(VAT) Tj", StringComparison.Ordinal)
               && pdf.IndexOf("(VAT) Tj", StringComparison.Ordinal) < pdf.IndexOf("(Total Price Incl. VAT) Tj", StringComparison.Ordinal),
            "the PDF runs Other Charges, Taxable Amount, VAT, Total Incl. VAT");
        Assert(pdf.Contains("(1,180.00) Tj") && pdf.Contains("(59.00) Tj") && pdf.Contains("(5%) Tj") && pdf.Contains("(1,239.00) Tj"),
            "the PDF prints the taxable amount, the rate and amount of VAT in one cell, and the unchanged total");

        // A foreign vendor with AED equivalents on: taxable and VAT each get the local line.
        var (prE, rfqsE) = Charged("EUR");
        var conv = Latin1(PcrPdfExporter.GeneratePdf(prE, Pcr(prE), rfqsE, "x", null, null, AedAt(4m)));
        Assert(conv.Contains("(4,720.00) Tj") && conv.Contains("(236.00) Tj"), "taxable and VAT convert to AED");
        var convSheet = ReadSheetXml(PcrExcelExporter.GenerateExcel(prE, Pcr(prE), rfqsE, "x", null, AedAt(4m)));
        Assert(convSheet.Contains("EUR 1,180.00\n~ AED 4,720.00") && convSheet.Contains("EUR 59.00\n~ AED 236.00"),
            "and so do the spreadsheet's");
        AssertWellFormed(convSheet);

        // No freight or other charges: the taxable row would repeat the row above, so it is left out;
        // the VAT cell still carries its amount.
        var (pm, rm) = Mixed();
        var plainSheet = ReadSheetXml(PcrExcelExporter.GenerateExcel(pm, Pcr(pm), rm, "x"));
        Assert(!plainSheet.Contains("Taxable Amount"), "no charges, no Taxable Amount row in the spreadsheet");
        Assert(plainSheet.Contains("<v>64") && plainSheet.Contains("<v>266"), "but the VAT cells hold 64 and 266");
        var plainPdf = Latin1(PcrPdfExporter.GeneratePdf(pm, Pcr(pm), rm, "x"));
        Assert(!plainPdf.Contains("Taxable Amount") && plainPdf.Contains("(64.00) Tj") && plainPdf.Contains("(266.00) Tj"),
            "and in the PDF");

        // Reverse charge: the VAT is worked out and shown in brackets, but never added - the total
        // is the taxable amount, exactly as before.
        var (prR, rfqsR) = Charged("AED", vat: "RC");
        var rc = ReadSheetXml(PcrExcelExporter.GenerateExcel(prR, Pcr(prR), rfqsR, "x"));
        Assert(rc.Contains(">RC<") && rc.Contains(">(59.00)<") && !rc.Contains(">5%<") && !rc.Contains("<v>59"),
            "a reverse-charge quote shows RC and its VAT in brackets");
        Assert(rc.Contains("<v>1180") && !rc.Contains("<v>1239"), "and the total stays at the taxable amount");
        var rcPdf = Latin1(PcrPdfExporter.GeneratePdf(prR, Pcr(prR), rfqsR, "x"));
        Assert(rcPdf.Contains("(RC) Tj") && rcPdf.Contains("(\\(59.00\\)) Tj") && rcPdf.Contains("(1,180.00) Tj") && !rcPdf.Contains("(1,239.00) Tj"),
            "and so does the PDF");
        AssertWellFormed(rc);

        var (prRE, rfqsRE) = Charged("EUR", vat: "RC");
        var rcConv = Latin1(PcrPdfExporter.GeneratePdf(prRE, Pcr(prRE), rfqsRE, "x", null, null, AedAt(4m)));
        Assert(rcConv.Contains("(\\(59.00\\)) Tj") && rcConv.Contains("(\\(236.00\\)) Tj"), "the reverse-charge VAT converts to AED, in brackets");
        var rcConvSheet = ReadSheetXml(PcrExcelExporter.GenerateExcel(prRE, Pcr(prRE), rfqsRE, "x", null, AedAt(4m)));
        Assert(rcConvSheet.Contains("(59.00)\n~ AED (236.00)"), "and so does the spreadsheet's");
        AssertWellFormed(rcConvSheet);

        // Zero-rated: no VAT to show or report, the text stands alone.
        var (prZ, rfqsZ) = Charged("AED", vat: "V0");
        var zero = ReadSheetXml(PcrExcelExporter.GenerateExcel(prZ, Pcr(prZ), rfqsZ, "x"));
        Assert(zero.Contains(">V0<") && !zero.Contains("(59.00)") && !zero.Contains("<v>59"), "a zero-rated quote shows V0 and no VAT amount");
        var zeroPdf = Latin1(PcrPdfExporter.GeneratePdf(prZ, Pcr(prZ), rfqsZ, "x"));
        Assert(zeroPdf.Contains("(V0) Tj") && !zeroPdf.Contains("(59.00)"), "and so does the PDF");

        // A vendor who has not quoted: dashes in the money rows, the rate text in the VAT row.
        var prN = NewPr(("A", 10m));
        var quoted = Rfq(prN, "Quoted", "AED", Line(prN.Items[0], 100m, null));
        quoted.Freight = 50m;
        var silent = new RequestForQuotation { Id = Guid.NewGuid(), PrId = prN.Id, RfqNo = "RFQ-DSC-Silent", Vendor = "Silent", Currency = "AED", VatType = "5%", Freight = 123m };
        Assert(!silent.IsQuoteReceived, "the fixture vendor really has not quoted");
        var both = new List<RequestForQuotation> { quoted, silent };
        var mixSheet = ReadSheetXml(PcrExcelExporter.GenerateExcel(prN, Pcr(prN), both, "x"));
        // ">5%<" three times: the quoted vendor's rate, the silent vendor's text, the historical column.
        Assert(Count(mixSheet, ">5%<") == 3 && mixSheet.Contains("<v>52.5"),
            "the unquoted vendor shows the rate text only; the quoted one shows 5% and 52.50");
        Assert(Count(mixSheet, "<v>123") == 1 && !mixSheet.Contains("<v>6.15"),
            "the unquoted vendor's freight shows once and no taxable amount or VAT is worked out for it");
        // The silent vendor's rate sits in the Qty column with a dash in the price column - not
        // merged and centred - so the rates stay in one column.
        var vatRow = System.Text.RegularExpressions.Regex.Match(mixSheet, "<c r=\"F(\\d+)\" s=\"10\" t=\"inlineStr\"><is><t>5%</t>");
        Assert(vatRow.Success, "the silent vendor's VAT rate is in its Qty column");
        var vr = vatRow.Groups[1].Value;
        Assert(!mixSheet.Contains($"<mergeCell ref=\"F{vr}:G{vr}\"/>") && mixSheet.Contains($"<c r=\"G{vr}\" s=\"9\" t=\"inlineStr\"><is><t>-</t>"),
            "with a dash where the amount would be, and no merge");
        AssertWellFormed(mixSheet);
        var mixPdf = Latin1(PcrPdfExporter.GeneratePdf(prN, Pcr(prN), both, "x"));
        Assert(Count(mixPdf, "(5%) Tj") == 2 && mixPdf.Contains("(1,050.00) Tj") && Count(mixPdf, "(123.00) Tj") == 1 && !mixPdf.Contains("(6.15) Tj"),
            "and in the PDF the unquoted vendor's taxable amount is a dash, not a figure");
    }

    private static void TotalsRowsSurviveEveryLayout()
    {
        var (pr, rfqs) = Charged("EUR");
        string[] label = { "(Taxable Amount) Tj" };

        var a6 = Latin1(PcrPdfExporter.GeneratePdf(pr, Pcr(pr), rfqs, "x",
            new PcrPdfOptions { PaperSize = PdfPaperSize.A6, Orientation = PdfOrientation.Portrait }, null, AedAt(4m)));
        Assert(a6.Contains(label[0]), "A6 portrait keeps the taxable row");

        var fit = Latin1(PcrPdfExporter.GeneratePdf(pr, Pcr(pr), rfqs, "x",
            new PcrPdfOptions { LayoutMode = PdfLayoutMode.ShrinkToFit }, null, AedAt(4m)));
        Assert(fit.Contains(label[0]), "fit-to-one-page keeps it");

        // Seven vendors: the currency moves into the headings, but the VAT cell still leads with its rate.
        var many = NewPr(("A", 10m), ("B", 4m));
        var seven = Enumerable.Range(0, 7).Select(i =>
        {
            var r = Rfq(many, $"Vendor {i}", i % 2 == 0 ? "EUR" : "AED", Line(many.Items[0], 100m, null), Line(many.Items[1], 50m, null));
            r.Freight = 50m; r.Discount = 100m; r.VatType = i == 3 ? "RC" : "5%";
            return r;
        }).ToList();
        var wide = Latin1(PcrPdfExporter.GeneratePdf(many, Pcr(many), seven, "x", null, null, AedAt(4m)));
        Assert(wide.Contains(label[0]) && wide.Contains("(5%) Tj") && wide.Contains("(RC) Tj"), "seven vendors keep the taxable row and the VAT cells");

        // Eighty rows paginate; the summary still lands, taller, on the last page.
        var longPr = NewPr(Enumerable.Range(1, 80).Select(i => ($"ITEM {i}", 3m)).ToArray());
        var longRfq = Rfq(longPr, "Long", "EUR", longPr.Items.Select(p => Line(p, 50m, null)).ToArray());
        longRfq.Freight = 25m; longRfq.OtherCharges = 5m;
        var longPdf = Latin1(PcrPdfExporter.GeneratePdf(longPr, Pcr(longPr), new[] { longRfq }, "x", null, null, AedAt(4m)));
        Assert(Count(longPdf, "/Type /Page ") > 1 && longPdf.Contains(label[0]), "a long list paginates and still ends with the taxable row");

        // Charges only on the second of two vendors: the first shows dashes in that row.
        var two = NewPr(("A", 2m));
        var plain = Rfq(two, "Plain", "AED", Line(two.Items[0], 10m, null));
        var charged = Rfq(two, "Charged", "AED", Line(two.Items[0], 10m, null)); charged.OtherCharges = 7m;
        var twoSheet = ReadSheetXml(PcrExcelExporter.GenerateExcel(two, Pcr(two), new[] { plain, charged }, "x"));
        Assert(Count(twoSheet, ">Taxable Amount<") == 1 && twoSheet.Contains("<v>27"),
            "charges on one vendor add one row, and the charged vendor's taxable amount is 27");
        AssertWellFormed(twoSheet);
    }

    // ---- fixtures --------------------------------------------------------------------------------

    /// <summary>One vendor: items 1,200 (10 x 100 + 4 x 50), discount 100, freight 50, other 30.</summary>
    private static (PurchaseRequisition, List<RequestForQuotation>) Charged(string currency, string vat = "5%")
    {
        var pr = NewPr(("A", 10m), ("B", 4m));
        var rfq = Rfq(pr, "Gamma", currency, Line(pr.Items[0], 100m, null), Line(pr.Items[1], 50m, null));
        rfq.Discount = 100m; rfq.Freight = 50m; rfq.OtherCharges = 30m; rfq.VatType = vat;
        return (pr, new List<RequestForQuotation> { rfq });
    }

    /// <summary>Alpha (EUR) discounts line A by 10 a unit (10 NOS: 100 off); Beta (AED) discounts line B
    /// by 20 a unit (4 NOS: 80 off). Rate EUR to AED is 4 in every check.</summary>
    // ---- a historical price with no currency saved -------------------------------------------------

    /// <summary>No currency saved on a historical price means AED - on screen, in Excel and in the PDF. The
    /// PDF used to print it in the first selected quote's currency, so a EUR quote turned the same figure
    /// into "EUR 86.75" while Excel and the board said AED.</summary>
    private static void HistoricalPriceWithoutACurrencyIsAed()
    {
        var pr = NewPr(("A", 2m), ("B", 3m));
        pr.Items[0].EstimatedUnitPrice = 86.75m;            // the requisition's own historical price, no currency
        var eur = Rfq(pr, "Eur Vendor", "EUR", Line(pr.Items[0], 100m, null), Line(pr.Items[1], 200m, null));
        eur.Items[1].LastPrice = 70m;                       // a quote line's last price, no currency
        var rfqs = new List<RequestForQuotation> { eur };

        var sheet = ReadSheetXml(PcrExcelExporter.GenerateExcel(pr, Pcr(pr), rfqs, "x", null, null));
        Assert(sheet.Contains("86.75 AED") && sheet.Contains("70.00 AED"), "Excel prints both historical prices in AED");
        Assert(!sheet.Contains("86.75 EUR") && !sheet.Contains("70.00 EUR"), "and never in the quote's currency");

        var pdf = Latin1(PcrPdfExporter.GeneratePdf(pr, Pcr(pr), rfqs, "x"));
        Assert(pdf.Contains("(86.75) Tj") && pdf.Contains("(70.00) Tj"), "the PDF prints both historical figures");
        Assert(pdf.Contains("(AED) Tj"), "and in AED, like Excel");

        // a price that does carry its currency keeps it
        pr.Items[0].EstimatedCurrency = "USD";
        var usd = Latin1(PcrPdfExporter.GeneratePdf(pr, Pcr(pr), rfqs, "x"));
        Assert(usd.Contains("(USD) Tj"), "a historical price with its own currency prints that currency");
    }

    private static (PurchaseRequisition, List<RequestForQuotation>) Mixed()
    {
        var pr = NewPr(("A", 10m), ("B", 4m));
        var alpha = Rfq(pr, "Alpha", "EUR", Line(pr.Items[0], 120m, 10m), Line(pr.Items[1], 45m, null));
        var beta = Rfq(pr, "Beta", "AED", Line(pr.Items[0], 500m, null), Line(pr.Items[1], 100m, 20m));
        return (pr, new List<RequestForQuotation> { alpha, beta });
    }

    private static PcrCurrencyConversionOptions AedAt(decimal eurRate, bool show = true) =>
        new(show, "AED", new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["EUR"] = eurRate,
            ["KWD"] = eurRate,
        });

    private static PurchaseRequisition NewPr(params (string Name, decimal Qty)[] items)
    {
        var pr = new PurchaseRequisition { Id = Guid.NewGuid(), PrNo = "PR-DISCOUNT-CHECK", Description = "discount check", Requestor = "check" };
        var sort = 0;
        foreach (var (name, qty) in items)
            pr.Items.Add(new PrItem { Id = Guid.NewGuid(), PrId = pr.Id, ItemName = name, Quantity = qty, Unit = "NOS", SortOrder = sort++ });
        return pr;
    }

    private static PriceComparisonRequest Pcr(PurchaseRequisition pr) =>
        new() { Id = Guid.NewGuid(), PrId = pr.Id, PcrNo = "PCR-DISCOUNT-CHECK", CreatedAt = DateTime.Now };

    private static RequestForQuotation Rfq(PurchaseRequisition pr, string vendor, string currency, params RfqItem[] lines)
    {
        var rfq = new RequestForQuotation
        {
            Id = Guid.NewGuid(), PrId = pr.Id, RfqNo = "RFQ-DSC-" + vendor.Replace(' ', '-'), Vendor = vendor,
            Currency = currency, VatType = "5%", Status = RfqStatus.QuoteReceived, QuoteReceivedDate = DateTime.Today
        };
        foreach (var line in lines) rfq.Items.Add(line);
        return rfq;
    }

    private static RfqItem Line(PrItem item, decimal? price, decimal? discount, bool quoted = true) => new()
    {
        Id = Guid.NewGuid(), PrItemId = item.Id, ItemName = item.ItemName, Quantity = item.Quantity, Unit = item.Unit,
        IsQuoted = quoted, QuotedUnitPrice = price, Discount = discount
    };

    private static string ReadSheetXml(byte[] xlsx)
    {
        using var ms = new MemoryStream(xlsx);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        return reader.ReadToEnd();
    }

    // Excel refuses a file it cannot parse, so well-formed XML is the floor for every sheet.
    private static void AssertWellFormed(string xml)
    {
        try { System.Xml.Linq.XDocument.Parse(xml); }
        catch (Exception ex) { throw new InvalidOperationException("PCR DISCOUNT: the worksheet is not well-formed XML: " + ex.Message); }
    }

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static int Count(string haystack, string needle)
    {
        int count = 0, at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { count++; at += needle.Length; }
        return count;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("PCR DISCOUNT: " + message);
    }
}
