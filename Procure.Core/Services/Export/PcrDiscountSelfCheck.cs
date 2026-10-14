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
    private const string ItemDiscountsLabel = "Item Discounts (included above)";

    public static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;   // the sheet formats numbers with the current culture
        try
        {
            LineDiscountMaths();
            RfqTotals();
            ExcelShowsListPriceStruckAndConvertsThePricePaid();
            ExcelWithoutConversion();
            NothingChangesWithoutDiscounts();
            PdfShowsListPriceStruckAndConvertsThePricePaid();
            PdfSurvivesEveryLayout();
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
        // (price, discount, quoted) -> expected HasLineDiscount, expected total taken off.
        var cases = new (decimal? Price, decimal? Disc, bool Quoted, bool Has, decimal Off)[]
        {
            (100m, 10m,   true,  true,  30m),    // the ordinary case, quantity 3
            (100m, null,  true,  false, 0m),     // no discount typed
            (100m, 0m,    true,  false, 0m),     // typed zero
            (100m, -5m,   true,  false, 0m),     // a negative number is not a discount
            (100m, 100m,  true,  false, 300m),   // wipes the price out: prints the dash, nothing to strike
            (100m, 150m,  true,  false, 300m),   // more than the price only ever removes the price
            (100m, 99.99m, true, true,  299.97m),// one cent left is still a price
            (null, 10m,   true,  false, 0m),     // a discount on a line with no price
            (0m,   10m,   true,  false, 0m),     // price typed as zero
            (100m, 10m,   false, false, 0m),     // a line the vendor did not quote
        };
        foreach (var c in cases)
        {
            var item = new RfqItem { Quantity = 3, IsQuoted = c.Quoted, QuotedUnitPrice = c.Price, Discount = c.Disc };
            Assert(item.HasLineDiscount == c.Has, $"HasLineDiscount for price {c.Price} discount {c.Disc} quoted {c.Quoted}; got {item.HasLineDiscount}");
            Assert(item.DiscountTotal == c.Off, $"DiscountTotal for price {c.Price} discount {c.Disc} quoted {c.Quoted} should be {c.Off}; got {item.DiscountTotal}");
            // What is taken off plus what is charged is always the full list price, so the new row
            // can never disagree with the line totals it explains.
            if (c.Quoted && c.Price is > 0 && c.Disc is null or >= 0)
                Assert(item.DiscountTotal + item.LineTotal == item.Quantity * c.Price.Value,
                    $"taken off + charged equals the list total for price {c.Price} discount {c.Disc}");
        }
    }

    private static void RfqTotals()
    {
        var pr = NewPr(("A", 10m), ("B", 4m));
        var rfq = Rfq(pr, "Totals", "EUR",
            Line(pr.Items[0], 120m, 10m),                  // 100 off
            Line(pr.Items[1], 45m, null));
        Assert(rfq.ItemDiscountTotal == 100m, $"the quote's item discounts add up; got {rfq.ItemDiscountTotal}");
        Assert(rfq.BaseAmount == 10m * 110m + 4m * 45m, $"and are already out of the base amount; got {rfq.BaseAmount}");

        rfq.Items.Add(Line(pr.Items[0], 50m, 5m, quoted: false));
        Assert(rfq.ItemDiscountTotal == 100m, "a line the vendor did not quote adds nothing");

        var lump = new RequestForQuotation { Currency = "EUR", QuoteAmount = 500m };
        Assert(lump.ItemDiscountTotal == 0m, "a lump-sum quote with no lines has no item discounts");
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
        Assert(sheet.Contains(ItemDiscountsLabel), "the item discounts row is added");
        Assert(sheet.Contains("EUR 100.00\n~ AED 400.00"), "the item discounts row converts what the discounts took off");
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
            Assert(sheet.Contains(ItemDiscountsLabel), "the item discounts row is there");
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
        Assert(Count(sheet, "<strike/>") == 0 && !sheet.Contains(ItemDiscountsLabel),
            "a sheet with no discounts has no strike and no extra row");

        var pdf = Latin1(PcrPdfExporter.GeneratePdf(pr, Pcr(pr), rfqs, "x", null, null, AedAt(4m)));
        Assert(!pdf.Contains(StruckFill) && !pdf.Contains(StruckStroke) && !pdf.Contains("Item Discounts"),
            "and neither does the PDF");
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
        Assert(pdf.Contains("Item Discounts \\(included above\\)"), "the item discounts row is added");
        Assert(pdf.Contains("(400.00) Tj"), "and converts what the discounts took off");

        var off = Latin1(PcrPdfExporter.GeneratePdf(pr, Pcr(pr), rfqs, "x", null, null, AedAt(4m, show: false)));
        Assert(off.Contains(StruckFill) && !off.Contains("(440.00) Tj"), "with the switch off the list price still shows, and nothing converts");

        // The discounted row is one line taller than it would be, never shorter.
        var plain = NewPr(("A", 10m), ("B", 4m));
        var noDisc = new List<RequestForQuotation> { Rfq(plain, "Alpha", "EUR", Line(plain.Items[0], 110m, null), Line(plain.Items[1], 45m, null)) };
        var withDisc = new List<RequestForQuotation> { Rfq(plain, "Alpha", "EUR", Line(plain.Items[0], 120m, 10m), Line(plain.Items[1], 45m, null)) };
        var flat = PcrPdfExporter.GeneratePdf(plain, Pcr(plain), noDisc, "x");
        var tall = PcrPdfExporter.GeneratePdf(plain, Pcr(plain), withDisc, "x");
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

    // ---- fixtures --------------------------------------------------------------------------------

    /// <summary>Alpha (EUR) discounts line A by 10 a unit (10 NOS: 100 off); Beta (AED) discounts line B
    /// by 20 a unit (4 NOS: 80 off). Rate EUR to AED is 4 in every check.</summary>
    private static (PurchaseRequisition, List<RequestForQuotation>) Mixed()
    {
        var pr = NewPr(("A", 10m), ("B", 4m));
        var alpha = Rfq(pr, "Alpha", "EUR", Line(pr.Items[0], 120m, 10m), Line(pr.Items[1], 45m, null));
        var beta = Rfq(pr, "Beta", "AED", Line(pr.Items[0], 500m, null), Line(pr.Items[1], 100m, 20m));
        Assert(alpha.ItemDiscountTotal == 100m && beta.ItemDiscountTotal == 80m, "the fixture discounts what it says it does");
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
