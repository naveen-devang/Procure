using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Procure.Data;
using Procure.Models;
using Procure.PageModels;

namespace Procure.Utilities
{
    /// <summary>
    /// The runnable check behind the board's memory-release behavior: the 500-row release threshold,
    /// that PR switching preserves search/filter/selection either side of it, the 10-second card-detail
    /// eviction, and that a rapid reopen or container recycle cannot let a stale timer release content
    /// it should not touch.
    ///
    /// Run it by launching a Debug build with PROCURE_BOARD_SELFCHECK=1 set. It manipulates the live
    /// PrListPageModel singleton's loaded window and a standalone LazyExpander - not the database - so
    /// it is fast and needs no test data, but it does disturb whatever the PR Board is currently showing;
    /// run it before opening the board, not while using it. Debug only, opt-in only.
    /// </summary>
    internal static class BoardMemorySelfCheck
    {
        public static async Task RunAsync()
        {
            try
            {
                await CheckReleaseThresholdAsync();
                await CheckPrSwitchingPreservesStateAsync();
                await CheckCardEvictionAsync();
                await CheckRapidReopenAndRecycleAsync();
                await CheckSearchDebounceAsync();
                await CheckCallOffSearchDebounceAsync();
                CheckPcrPreviewReleasedOnExportModalClose();
                await CheckPdfRasterizerAsync();
                CheckInternedColors();
                Report("PASS");
            }
            catch (Exception ex)
            {
                Report("FAIL " + ex.Message);
                throw;
            }
        }

        // --- 500-row release threshold -------------------------------------------------------------

        private static Task CheckReleaseThresholdAsync()
        {
            var model = PrListPageModel.Current
                ?? throw new InvalidOperationException("PrListPageModel.Current is not set - run this after the app has constructed it.");

            // Below threshold: state is preserved.
            model.SeedLoadedPrsForTest(MakeFakePrs(499));
            model.BoardDisappearing();
            if (model.LoadedPrs.Count != 499)
                throw new InvalidOperationException($"499 loaded PRs should survive BoardDisappearing; found {model.LoadedPrs.Count}.");

            // At the threshold: still preserved (the rule is "above", not "at or above").
            model.SeedLoadedPrsForTest(MakeFakePrs(500));
            model.BoardDisappearing();
            if (model.LoadedPrs.Count != 500)
                throw new InvalidOperationException($"500 loaded PRs (the boundary) should survive BoardDisappearing; found {model.LoadedPrs.Count}.");

            // Above threshold: released.
            model.SeedLoadedPrsForTest(MakeFakePrs(501));
            model.BoardDisappearing();
            if (model.LoadedPrs.Count != 0)
                throw new InvalidOperationException($"501 loaded PRs should be released by BoardDisappearing; found {model.LoadedPrs.Count} remaining.");

            return Task.CompletedTask;
        }

        // --- PR switching: search/filter/selection survive a release ------------------------------

        private static Task CheckPrSwitchingPreservesStateAsync()
        {
            var model = PrListPageModel.Current!;

            var probePr = new PurchaseRequisition { PrNo = "SELFCHECK-SWITCH" };
            var prs = MakeFakePrs(600);
            prs.Add(probePr);
            model.SeedLoadedPrsForTest(prs);
            model.SetSelected(probePr, true);

            var savedSearch = model.SearchText;
            model.SearchText = "selfcheck-switch-marker";

            model.BoardDisappearing();

            if (model.LoadedPrs.Count != 0)
                throw new InvalidOperationException("Release did not clear the loaded window as expected.");
            if (model.SearchText != "selfcheck-switch-marker")
                throw new InvalidOperationException("Release must not touch SearchText - filter state is a separate concern from the loaded window.");

            // BoardAppearing must take the same path a first-ever open takes.
            model.BoardAppearing();

            model.SearchText = savedSearch;
            return Task.CompletedTask;
        }

        // --- 10-second card eviction -----------------------------------------------------------------
        // Exercises the mechanism with a short delay for test speed; the production wiring in
        // PrListPage.xaml hardcodes AutoReleaseDelay="0:0:10" on the one LazyExpander that wraps a
        // card's detail panel. Same code path either way - only the TimeSpan differs.

        // Generous margin over the delay itself: this runs during real app startup (a background PR
        // load is still in flight from the previous check), which competes for UI-thread dispatcher
        // time and can push a short DispatchDelayed well past its nominal interval.
        private static readonly TimeSpan TestDelay = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan TestMargin = TimeSpan.FromSeconds(2);

        private static async Task CheckCardEvictionAsync()
        {
            var expander = new LazyExpander
            {
                AutoReleaseDelay = TestDelay,
                ContentTemplate = new DataTemplate(() => new Label { Text = "selfcheck" })
            };

            expander.IsExpanded = true;
            if (!expander.HasBuiltContentForTest)
                throw new InvalidOperationException("Expanding should build content immediately (no PlaceholderTemplate set).");

            expander.IsExpanded = false;
            await Task.Delay(TestDelay + TestMargin);

            if (expander.HasBuiltContentForTest)
                throw new InvalidOperationException("Content should be released after AutoReleaseDelay elapses while collapsed.");

            // Reopening after eviction must rebuild correctly, not silently stay empty.
            expander.IsExpanded = true;
            if (!expander.HasBuiltContentForTest)
                throw new InvalidOperationException("Reopening an evicted card must rebuild its content.");
        }

        // --- Rapid reopen and container recycle must not let a stale timer fire -------------------

        private static async Task CheckRapidReopenAndRecycleAsync()
        {
            // Reopen before the delay elapses: the stale timer must not release the now-expanded content.
            var reopened = new LazyExpander
            {
                AutoReleaseDelay = TestDelay,
                ContentTemplate = new DataTemplate(() => new Label { Text = "selfcheck" })
            };
            reopened.IsExpanded = true;
            reopened.IsExpanded = false;   // arms a release
            reopened.IsExpanded = true;    // reopens before it fires
            await Task.Delay(TestDelay + TestMargin);
            if (!reopened.HasBuiltContentForTest)
                throw new InvalidOperationException("A card reopened before its eviction timer fired must not lose its content.");

            // Recycle before the delay elapses: the timer armed for the OLD row must not act on
            // whatever the container now holds after the BindingContext changes. In production a
            // recycle also re-evaluates the IsExpanded binding against the new row, which arms its own
            // correctly-scoped timer if the new row is collapsed too - but that is a second, separate
            // event this test does not simulate. What this isolates is narrower and load-bearing on its
            // own: the stale timer specifically must no-op, not release content out from under a
            // container it no longer describes.
            var recycled = new LazyExpander
            {
                AutoReleaseDelay = TestDelay,
                ContentTemplate = new DataTemplate(() => new Label { Text = "selfcheck" }),
                BindingContext = new object()
            };
            recycled.IsExpanded = true;
            recycled.IsExpanded = false;               // arms a release under the current generation
            recycled.BindingContext = new object();     // recycle bumps the generation; timer is now stale
            await Task.Delay(TestDelay + TestMargin);
            if (!recycled.HasBuiltContentForTest)
                throw new InvalidOperationException("The stale timer from before the recycle released content it no longer owns - " +
                    "the generation guard did not stop it.");
        }

        // --- typing in the search box must not reload the board per keystroke -------------------

        private const int SearchDebounceMs = 300;

        private static async Task CheckSearchDebounceAsync()
        {
            var model = PrListPageModel.Current!;
            var saved = model.SearchText;
            var before = model.ReloadPassesForTest;

            model.SearchText = "selfcheck-d";
            model.SearchText = "selfcheck-de";
            model.SearchText = "selfcheck-deb";

            if (model.ReloadPassesForTest != before)
                throw new InvalidOperationException(
                    "A keystroke reloaded the board synchronously - the search debounce is gone.");

            await Task.Delay(TimeSpan.FromMilliseconds(SearchDebounceMs) + TestMargin);

            // Upper bound is 2, not 1: this runs during startup, where a preload's own ApplyFilters
            // can land in the same window. Three passes would mean one per keystroke - the regression.
            var passes = model.ReloadPassesForTest - before;
            if (passes < 1)
                throw new InvalidOperationException(
                    "The debounced reload never fired - typing would leave the board stale.");
            if (passes > 2)
                throw new InvalidOperationException(
                    $"Three keystrokes inside one debounce window produced {passes} board reloads; the debounce is not collapsing them.");

            model.SearchText = saved;
            await Task.Delay(TimeSpan.FromMilliseconds(SearchDebounceMs) + TestMargin);
        }

        // --- the call-off tab's search box debounces too -------------------------------------------

        private static async Task CheckCallOffSearchDebounceAsync()
        {
            var model = IPlatformApplication.Current?.Services?.GetService<CallOffPageModel>();
            if (model is null)
                throw new InvalidOperationException("CallOffPageModel is not registered - cannot check its search debounce.");

            var saved = model.SearchText;
            // RebuildGroups replaces Groups outright, so a new reference IS a rebuild.
            var before = model.Groups;

            model.SearchText = "selfcheck-c";
            model.SearchText = "selfcheck-ca";
            model.SearchText = "selfcheck-cal";

            if (!ReferenceEquals(model.Groups, before))
                throw new InvalidOperationException(
                    "A keystroke rebuilt the call-off groups synchronously - the debounce is gone.");

            await Task.Delay(TimeSpan.FromMilliseconds(SearchDebounceMs) + TestMargin);

            if (ReferenceEquals(model.Groups, before))
                throw new InvalidOperationException(
                    "The debounced call-off rebuild never fired - typing would leave the list stale.");

            model.SearchText = saved;
            await Task.Delay(TimeSpan.FromMilliseconds(SearchDebounceMs) + TestMargin);
        }

        // --- dismissing the export modal releases the rendered preview ------------------------------
        // Back-then-close reached neither ClosePcrPreview nor a regenerate, so the rendered pages (one
        // LOH-sized PNG each), the PDF bytes and the PR graph stayed on this singleton for the session.

        private static void CheckPcrPreviewReleasedOnExportModalClose()
        {
            var model = PrListPageModel.Current!;

            model.PcrPreviewPages.Add(ImageSource.FromStream(() => new MemoryStream(new byte[] { 1, 2, 3 })));
            model.BackToPcrExportModal();

            if (model.PcrPreviewPages.Count == 0)
                throw new InvalidOperationException("Back is supposed to keep the preview for an instant reopen, not drop it.");

            model.CloseExportPcrModal();

            if (model.PcrPreviewPages.Count != 0)
                throw new InvalidOperationException(
                    "Closing the export modal after Back left the rendered preview in memory - ReleasePcrPreview did not run.");
        }

        // --- the rasterizer still returns pages now its DataReader is disposed ---------------------
        // DataReader closes the stream it was constructed over on dispose (which is why the DataWriter
        // above it calls DetachStream), so adding the `using` had to be checked against a real render,
        // not just a compile.

        private static async Task CheckPdfRasterizerAsync()
        {
            var pr = new PurchaseRequisition { PrNo = "SELFCHECK-PDF", Description = "Rasterizer check" };
            var pcr = new PriceComparisonRequest { PrId = pr.Id, PcrNo = "PCR-SELFCHECK" };
            var rfq = new RequestForQuotation { PrId = pr.Id, Vendor = "Selfcheck Vendor", RfqNo = "RFQ-SELFCHECK" };
            // Enough line items to spill past one page: a single-page render would never run the
            // rasterizer's loop body twice, which is exactly where a prematurely-disposed DataReader
            // would bite. The PDF's rows come from pr.Items; the RFQ supplies the priced column.
            for (var i = 0; i < 120; i++)
            {
                pr.Items.Add(new PrItem
                {
                    PrId = pr.Id,
                    ItemName = $"Selfcheck line item {i + 1}",
                    Quantity = i + 1,
                    SortOrder = i
                });
                rfq.Items.Add(new RfqItem
                {
                    RfqId = rfq.Id,
                    ItemName = $"Selfcheck line item {i + 1}",
                    Quantity = i + 1,
                    QuotedUnitPrice = 10m + i,
                    SortOrder = i
                });
            }

            var pdf = Services.Export.PcrPdfExporter.GeneratePdf(pr, pcr, new[] { rfq }, "selfcheck");
            if (pdf.Length == 0) throw new InvalidOperationException("The exporter produced no PDF bytes.");

            var pages = (await Services.Export.PcrPdfRasterizer.RenderPagesAsync(pdf)).Pages;
            if (pages.Count < 2)
                throw new InvalidOperationException(
                    $"Expected a multi-page render to exercise the loop more than once; got {pages.Count} page(s).");
            for (var i = 0; i < pages.Count; i++)
            {
                if (pages[i] is null || pages[i].Length == 0)
                    throw new InvalidOperationException($"Rasterized page {i + 1} came back empty - the DataReader was disposed before its bytes were read.");
            }
        }

        // --- interned converter colours are the same colours ----------------------------------------

        private static void CheckInternedColors()
        {
            foreach (var hex in new[] { "#6CCB5F", "#107C41", "#2D2C2C", "#FFFFFF", "#000000" })
            {
                var interned = ThemeHelper.Hex(hex);
                var parsed = Microsoft.Maui.Graphics.Color.FromArgb(hex);

                if (interned.Red != parsed.Red || interned.Green != parsed.Green
                    || interned.Blue != parsed.Blue || interned.Alpha != parsed.Alpha)
                    throw new InvalidOperationException($"Interned {hex} does not match Color.FromArgb - the converters would paint the wrong colour.");

                if (!ReferenceEquals(interned, ThemeHelper.Hex(hex)))
                    throw new InvalidOperationException($"Hex({hex}) allocated twice - the cache is not interning.");
            }
        }

        private static List<PurchaseRequisition> MakeFakePrs(int count)
        {
            var list = new List<PurchaseRequisition>(count);
            for (var i = 0; i < count; i++)
            {
                list.Add(new PurchaseRequisition { PrNo = $"SELFCHECK-{i}" });
            }
            return list;
        }

        private static void Report(string result)
        {
            Debug.WriteLine("BoardMemorySelfCheck: " + result);
            try
            {
                File.WriteAllText(
                    Path.Combine(DatabaseConstants.DatabaseDirectory, "board-memory-selfcheck.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {result}{Environment.NewLine}");
            }
            catch
            {
                // A diagnostic must never be the thing that breaks the run.
            }
        }
    }
}
