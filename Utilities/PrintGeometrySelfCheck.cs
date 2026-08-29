using System;
using System.Collections.Generic;
using System.Diagnostics;
using Graphics = System.Drawing.Graphics;
using RectangleF = System.Drawing.RectangleF;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Procure.Data;
using Procure.Models;
using Procure.PageModels;
using Procure.Services.Export;

namespace Procure.Utilities
{
    /// <summary>
    /// The runnable check behind the PCR print path: that a rendered page measures its real physical
    /// size, that print rendering gets the DPI it asked for (and A0 gets capped), that the placement
    /// decision lands inside every installed driver's printable region in both orientations, that an
    /// A4 picture resolves to the driver's own A4 entry, that a page-range subset PDF keeps its
    /// original numbering, and that the Copies box parses defensively.
    ///
    /// Run it by launching a Debug build with PROCURE_PRINT_SELFCHECK=1 set. It talks to the installed
    /// printer drivers through measurement graphics only - nothing is spooled and no paper is used.
    /// Debug only, opt-in only.
    /// </summary>
    internal static class PrintGeometrySelfCheck
    {
        public static async Task RunAsync()
        {
            var log = new StringBuilder();
            try
            {
                await CheckRasterUnitsAsync(log);
                await CheckPrintDpiAndCapAsync(log);
                CheckPlacementAgainstInstalledDrivers(log);
                CheckPaperSizeResolution(log);
                await CheckPageSubsetKeepsNumberingAsync(log);
                CheckCopiesParsing(log);
                Report("PASS", log);
            }
            catch (Exception ex)
            {
                Report("FAIL " + ex.Message, log);
                throw;
            }
        }

        // A4 landscape is 842x595pt. Every expectation below derives from that one fact.
        private const double A4WidthPt = 842, A4HeightPt = 595;

        private static (PurchaseRequisition pr, PriceComparisonRequest pcr, RequestForQuotation rfq) MakeSheet(int items)
        {
            var pr = new PurchaseRequisition { PrNo = "PRINT-SELFCHECK", Description = "Print geometry check" };
            var pcr = new PriceComparisonRequest { PrId = pr.Id, PcrNo = "PCR-PRINT-SELFCHECK" };
            var rfq = new RequestForQuotation { PrId = pr.Id, Vendor = "Selfcheck Vendor", RfqNo = "RFQ-SELFCHECK" };
            for (var i = 0; i < items; i++)
            {
                pr.Items.Add(new PrItem { PrId = pr.Id, ItemName = $"Selfcheck line item {i + 1}", Quantity = i + 1, SortOrder = i });
                rfq.Items.Add(new RfqItem { RfqId = rfq.Id, ItemName = $"Selfcheck line item {i + 1}", Quantity = i + 1, QuotedUnitPrice = 10m + i, SortOrder = i });
            }
            return (pr, pcr, rfq);
        }

        private static System.Drawing.Size PngSize(byte[] png)
        {
            using var ms = new MemoryStream(png);
            using var img = System.Drawing.Image.FromStream(ms);
            return img.Size;
        }

        // --- Fix 1: a rendered page measures its real physical size -------------------------------
        // PdfPage.Size is in 96-per-inch DIPs; scaling it as 72-per-inch points rendered every page
        // 4/3 oversized, which the print path then read back as a 15.6x11in sheet.

        private static async Task CheckRasterUnitsAsync(StringBuilder log)
        {
            var (pr, pcr, rfq) = MakeSheet(5);
            var pdf = PcrPdfExporter.GeneratePdf(pr, pcr, new[] { rfq }, "selfcheck");

            var preview = await PcrPdfRasterizer.RenderPagesAsync(pdf);
            var size = PngSize(preview.Pages[0]);
            var expectW = A4WidthPt / 72.0 * PcrPdfRasterizer.DefaultDpi;
            var expectH = A4HeightPt / 72.0 * PcrPdfRasterizer.DefaultDpi;
            log.AppendLine($"raster: A4 landscape @{preview.Dpi} DPI = {size.Width}x{size.Height} (expect {expectW:0}x{expectH:0})");

            if (Math.Abs(size.Width - expectW) > 2 || Math.Abs(size.Height - expectH) > 2)
                throw new InvalidOperationException(
                    $"A4 landscape rendered {size.Width}x{size.Height} at {preview.Dpi} DPI, but 842x595pt at that DPI is {expectW:0}x{expectH:0}. " +
                    "The page is being scaled by the wrong unit, and the print path will ask the driver for the wrong paper size.");

            // The print path's own derivation, in inches, must come back as A4.
            var derivedW = size.Width / preview.Dpi;
            var derivedH = size.Height / preview.Dpi;
            if (Math.Abs(derivedW - 11.69) > 0.03 || Math.Abs(derivedH - 8.27) > 0.03)
                throw new InvalidOperationException($"Print path would derive a {derivedW:0.00}x{derivedH:0.00}in sheet from this render; A4 landscape is 11.69x8.27in.");
        }

        // --- Fix 4: print DPI passthrough and the A0 cap ----------------------------------------------

        private static async Task CheckPrintDpiAndCapAsync(StringBuilder log)
        {
            var (pr, pcr, rfq) = MakeSheet(5);

            var a4Pdf = PcrPdfExporter.GeneratePdf(pr, pcr, new[] { rfq }, "selfcheck");
            var a4 = await PcrPdfRasterizer.RenderPagesAsync(a4Pdf, PcrPdfRasterizer.PrintDpi);
            log.AppendLine($"print dpi: A4 rendered at {a4.Dpi} (asked {PcrPdfRasterizer.PrintDpi})");
            if (Math.Abs(a4.Dpi - PcrPdfRasterizer.PrintDpi) > 0.01)
                throw new InvalidOperationException($"A4 was rendered at {a4.Dpi} DPI instead of the requested {PcrPdfRasterizer.PrintDpi} - the edge cap is firing when it should not.");

            var a4Size = PngSize(a4.Pages[0]);
            var expectW = A4WidthPt / 72.0 * PcrPdfRasterizer.PrintDpi;
            if (Math.Abs(a4Size.Width - expectW) > 2)
                throw new InvalidOperationException($"A4 at print DPI rendered {a4Size.Width}px wide; expected {expectW:0}.");

            // A3 is the largest paper a PCR is realistically printed on; it must keep the full DPI too.
            var a3Pdf = PcrPdfExporter.GeneratePdf(pr, pcr, new[] { rfq }, "selfcheck", new PcrPdfOptions { PaperSize = PdfPaperSize.A3 });
            var a3 = await PcrPdfRasterizer.RenderPagesAsync(a3Pdf, PcrPdfRasterizer.PrintDpi);
            if (Math.Abs(a3.Dpi - PcrPdfRasterizer.PrintDpi) > 0.01)
                throw new InvalidOperationException($"A3 was capped to {a3.Dpi} DPI; its long edge at {PcrPdfRasterizer.PrintDpi} is under the {PcrPdfRasterizer.MaxRenderEdgePx}px budget and should not be.");

            // A0 at the print DPI would be 14042x9933 - a 558MB decoded bitmap GDI+ cannot take.
            var a0Pdf = PcrPdfExporter.GeneratePdf(pr, pcr, new[] { rfq }, "selfcheck", new PcrPdfOptions { PaperSize = PdfPaperSize.A0 });
            var a0 = await PcrPdfRasterizer.RenderPagesAsync(a0Pdf, PcrPdfRasterizer.PrintDpi);
            var a0Size = PngSize(a0.Pages[0]);
            log.AppendLine($"cap: A0 rendered at {a0.Dpi:0.#} DPI = {a0Size.Width}x{a0Size.Height}");
            if (a0.Dpi >= PcrPdfRasterizer.PrintDpi)
                throw new InvalidOperationException($"A0 was rendered at {a0.Dpi} DPI - the edge cap did not engage, and a 558MB bitmap is the next step.");
            if (Math.Max(a0Size.Width, a0Size.Height) > PcrPdfRasterizer.MaxRenderEdgePx)
                throw new InvalidOperationException($"A0 rendered {a0Size.Width}x{a0Size.Height} - past the {PcrPdfRasterizer.MaxRenderEdgePx}px edge budget the cap exists to hold.");

            // And the capped render must still describe A0 when divided by the DPI it reports.
            var derivedLongIn = Math.Max(a0Size.Width, a0Size.Height) / a0.Dpi;
            if (Math.Abs(derivedLongIn - 46.81) > 0.05)
                throw new InvalidOperationException($"Capped A0 derives a {derivedLongIn:0.00}in long edge; A0 is 46.81in. The reported DPI does not match the pixels.");
        }

        // --- Fix 2: placement lands inside every installed driver's printable region -----------------
        // CreateMeasurementGraphics gives the same Graphics a PrintPage handler would see - same
        // origin, same VisibleClipBounds - without spooling anything.

        private static void CheckPlacementAgainstInstalledDrivers(StringBuilder log)
        {
            var a4LandscapePx = new System.Drawing.Size(3508, 2480); // 842x595pt at 300 DPI
            var a4PortraitPx = new System.Drawing.Size(2480, 3508);
            var checkedAny = false;

            foreach (string name in PrinterSettings.InstalledPrinters)
            {
                var settings = new PrinterSettings { PrinterName = name };
                if (!settings.IsValid) continue;

                foreach (var landscape in new[] { true, false })
                {
                    var page = settings.DefaultPageSettings;
                    page.Landscape = landscape;
                    page.PaperSize = PcrExportService.ResolvePaperSize(settings, 827, 1169);
                    var image = landscape ? a4LandscapePx : a4PortraitPx;

                    Graphics g;
                    try { g = settings.CreateMeasurementGraphics(page); }
                    catch (Exception ex)
                    {
                        log.AppendLine($"placement: {name} {(landscape ? "L" : "P")} - driver refused a measurement graphics ({ex.GetType().Name}); skipped");
                        continue;
                    }

                    using (g)
                    {
                        var clip = g.VisibleClipBounds;
                        var dest = PcrExportService.PlacePage(g, image.Width, image.Height);
                        checkedAny = true;
                        log.AppendLine($"placement: {name} {(landscape ? "L" : "P")} clip={clip.Width:0.#}x{clip.Height:0.#} dest={dest.X:0.#},{dest.Y:0.#} {dest.Width:0.#}x{dest.Height:0.#}");

                        // The region has to be shaped like the page: a landscape job on a portrait-
                        // shaped clip means the wrong box (PrintableArea, which never rotates) was used.
                        if (landscape ? clip.Width < clip.Height : clip.Width > clip.Height)
                            throw new InvalidOperationException($"{name}: printable region is {clip.Width:0}x{clip.Height:0} for a {(landscape ? "landscape" : "portrait")} page - the placement is reading a box that does not rotate with the page.");

                        const float eps = 0.5f;
                        if (dest.Left < clip.Left - eps || dest.Top < clip.Top - eps || dest.Right > clip.Right + eps || dest.Bottom > clip.Bottom + eps)
                            throw new InvalidOperationException($"{name}: placed page {dest} spills outside the printable region {clip} - that is the clipped right/bottom edge.");

                        var aspectImage = (double)image.Width / image.Height;
                        var aspectDest = dest.Width / dest.Height;
                        if (Math.Abs(aspectImage - aspectDest) > 0.002)
                            throw new InvalidOperationException($"{name}: placement distorted the page (image {aspectImage:0.000} vs placed {aspectDest:0.000}).");

                        // Largest fit: it must touch the region on at least one axis, and be centred.
                        var touchesW = Math.Abs(dest.Width - clip.Width) < eps;
                        var touchesH = Math.Abs(dest.Height - clip.Height) < eps;
                        if (!touchesW && !touchesH)
                            throw new InvalidOperationException($"{name}: placed page {dest.Width:0}x{dest.Height:0} is smaller than the largest fit into {clip.Width:0}x{clip.Height:0}.");
                        var cx = dest.X + dest.Width / 2 - (clip.X + clip.Width / 2);
                        var cy = dest.Y + dest.Height / 2 - (clip.Y + clip.Height / 2);
                        if (Math.Abs(cx) > eps || Math.Abs(cy) > eps)
                            throw new InvalidOperationException($"{name}: placed page is off-centre by ({cx:0.#},{cy:0.#}).");
                    }
                }
            }

            if (!checkedAny)
                throw new InvalidOperationException("No installed printer yielded a measurement graphics - nothing was checked.");

            // The arithmetic on a region with a real hard margin, independent of whatever drivers
            // happen to be installed here (this machine's PDF writers all report zero margins).
            var offset = new RectangleF(0, 0, 1129.7f, 793.3f);
            var fit = PcrExportService.FitInto(3508, 2480, offset);
            if (fit.Right > offset.Right + 0.01f || fit.Bottom > offset.Bottom + 0.01f)
                throw new InvalidOperationException($"FitInto overshoots a 1129.7x793.3 printable region: {fit}.");
            // That region is a touch wider than A4's ratio, so the fit is height-limited: full height,
            // 1122.1 wide, centred with ~3.8 either side.
            if (Math.Abs(fit.Height - 793.3f) > 0.5f || Math.Abs(fit.Width - 1122.1f) > 0.5f)
                throw new InvalidOperationException($"FitInto into 1129.7x793.3 should give 1122.1x793.3; got {fit.Width:0.#}x{fit.Height:0.#}.");
        }

        // --- Fix 3: A4 resolves to the driver's own A4, not a Custom sheet --------------------------

        private static void CheckPaperSizeResolution(StringBuilder log)
        {
            var settings = new PrinterSettings();
            if (!settings.IsValid) throw new InvalidOperationException("No valid default printer to resolve paper sizes against.");

            // 826x1169 is what a rounded A4 raster can come back as; both must hit the real entry.
            foreach (var w in new[] { 827, 826 })
            {
                var resolved = PcrExportService.ResolvePaperSize(settings, w, 1169);
                log.AppendLine($"paper: {w}x1169 on '{settings.PrinterName}' -> {resolved.PaperName} ({resolved.Kind})");
                if (resolved.Kind == PaperKind.Custom)
                    throw new InvalidOperationException($"{w}x1169 resolved to a Custom paper on '{settings.PrinterName}', which lists A4 - the driver may substitute its default sheet.");
                if (Math.Abs(resolved.Width - 827) > 5 || Math.Abs(resolved.Height - 1169) > 5)
                    throw new InvalidOperationException($"Resolved to {resolved.PaperName} {resolved.Width}x{resolved.Height} - not A4.");
            }

            // A size no driver lists must fall back to Custom rather than snap to the nearest real one.
            var odd = PcrExportService.ResolvePaperSize(settings, 1559, 1102);
            if (odd.Kind != PaperKind.Custom || odd.Width != 1559 || odd.Height != 1102)
                throw new InvalidOperationException($"An unlisted 1559x1102 sheet resolved to {odd.PaperName} {odd.Width}x{odd.Height}; expected a Custom fallback at the requested size.");
        }

        // --- Optional A: a page-range subset keeps the full document's numbering ---------------------

        private static async Task CheckPageSubsetKeepsNumberingAsync(StringBuilder log)
        {
            var (pr, pcr, rfq) = MakeSheet(120);
            var full = PcrPdfExporter.GeneratePdf(pr, pcr, new[] { rfq }, "selfcheck");
            var fullPages = (await PcrPdfRasterizer.RenderPagesAsync(full)).Pages.Count;
            if (fullPages < 3)
                throw new InvalidOperationException($"Need at least 3 laid-out pages to test a subset; got {fullPages}.");

            var subset = PcrPdfExporter.GeneratePdf(pr, pcr, new[] { rfq }, "selfcheck",
                new PcrPdfOptions { PagesToEmit = new[] { 2, 0 } }); // unordered on purpose
            var subsetPages = (await PcrPdfRasterizer.RenderPagesAsync(subset)).Pages.Count;
            log.AppendLine($"subset: {fullPages}-page sheet, emitted [2,0] -> {subsetPages} pages");
            if (subsetPages != 2)
                throw new InvalidOperationException($"Emitting pages [2,0] of {fullPages} produced {subsetPages} pages; expected 2.");

            // The labels must still read against the FULL page count, in document order, and the
            // dropped page's label must be absent.
            var text = Encoding.Latin1.GetString(subset);
            if (!text.Contains($"Page 1 of {fullPages}") || !text.Contains($"Page 3 of {fullPages}"))
                throw new InvalidOperationException($"Subset PDF lost its original numbering - expected 'Page 1 of {fullPages}' and 'Page 3 of {fullPages}'.");
            if (text.Contains($"Page 2 of {fullPages}"))
                throw new InvalidOperationException("Subset PDF still contains page 2, which was not requested.");
            if (text.IndexOf("Page 1 of", StringComparison.Ordinal) > text.IndexOf("Page 3 of", StringComparison.Ordinal))
                throw new InvalidOperationException("Subset pages were written out of document order.");

            // Out-of-range and empty selections must degrade to the whole document, never to nothing.
            var junk = PcrPdfExporter.GeneratePdf(pr, pcr, new[] { rfq }, "selfcheck", new PcrPdfOptions { PagesToEmit = new[] { 99 } });
            if ((await PcrPdfRasterizer.RenderPagesAsync(junk)).Pages.Count != fullPages)
                throw new InvalidOperationException("An entirely out-of-range PagesToEmit should fall back to every page.");
        }

        // --- Optional B: the Copies box never throws and always lands in 1-99 ------------------------

        private static void CheckCopiesParsing(StringBuilder log)
        {
            var cases = new (string? input, int expect)[]
            {
                ("1", 1), ("3", 3), (" 7 ", 7), ("99", 99), ("150", 99), ("0", 1), ("-4", 1),
                ("", 1), (null, 1), ("abc", 1), ("2.5", 1)
            };
            foreach (var (input, expect) in cases)
            {
                var got = PrListPageModel.ParseCopies(input);
                if (got != expect)
                    throw new InvalidOperationException($"ParseCopies(\"{input}\") returned {got}; expected {expect}.");
            }
            log.AppendLine($"copies: {cases.Length} inputs parsed as expected");
        }

        private static void Report(string result, StringBuilder log)
        {
            Debug.WriteLine("PrintGeometrySelfCheck: " + result);
            try
            {
                File.WriteAllText(
                    Path.Combine(DatabaseConstants.DatabaseDirectory, "print-geometry-selfcheck.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {result}{Environment.NewLine}{log}");
            }
            catch
            {
                // A diagnostic must never be the thing that breaks the run.
            }
        }
    }
}
