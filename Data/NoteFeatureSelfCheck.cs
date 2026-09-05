using System;
using System.Linq;
using System.Threading.Tasks;
using Procure.Data.Repositories;
using Procure.PageModels;
using Procure.Services;

namespace Procure.Data
{
    // Drives NotePageModel end to end: new note, edit (autosave), filter, pin, reorder, delete,
    // plus a rebuild heap probe.
    internal static class NoteFeatureSelfCheck
    {
        public static async Task RunAsync(INoteRepository repo, IErrorHandler errorHandler, ILinkTargetService linkTargets)
        {
            var marker = "nfsc-" + Guid.NewGuid().ToString("N")[..6] + "-";
            var vm = new NotePageModel(repo, errorHandler, linkTargets);

            try
            {
                await vm.LoadListAsync(force: true);

                // ---- new note lands selected ----
                await vm.NewNoteAsync();
                var first = vm.SelectedNote ?? throw new InvalidOperationException("NewNote did not select");
                first.Title = marker + "alpha";
                Assert(vm.Notes.Any(n => n.Id == first.Id), "new note is in the list");

                await vm.NewNoteAsync();
                var second = vm.SelectedNote!;
                second.Title = marker + "beta";

                // ---- edit -> autosave flush persists body + snippet ----
                vm.OnBodyEdited(@"{\rtf1\ansi first line\par}", "first line of beta");
                await vm.FlushPendingAsync();
                var reloaded = await repo.GetAsync(second.Id)!;
                Assert(reloaded!.Body.Contains("first line", StringComparison.Ordinal), "body autosaved");
                var list = await repo.GetListAsync();
                Assert(list.First(n => n.Id == second.Id).Snippet!.StartsWith("first line"), "snippet autosaved");

                // ---- filter ----
                vm.FilterText = marker + "alpha";
                await Task.Delay(900); // 250ms debounce plus room for a loaded UI thread - 320ms lost the race when the todo checks ran alongside
                Assert(vm.Notes.Count(n => n.Id == first.Id || n.Id == second.Id) == 1
                       && vm.Notes.Any(n => n.Id == first.Id), "filter narrows to the matching note");
                vm.FilterText = "";
                await Task.Delay(900); // 250ms debounce plus room for a loaded UI thread - 320ms lost the race when the todo checks ran alongside

                // ---- pin sorts to the top ----
                var betaRow = vm.Notes.First(n => n.Id == second.Id);
                await vm.TogglePinAsync(betaRow);
                Assert(vm.Notes[0].Id == second.Id, "pinned note is first");

                // ---- reorder among own notes ----
                await vm.TogglePinAsync(vm.Notes.First(n => n.Id == second.Id)); // unpin so both sort together
                await vm.SelectAsync(vm.Notes.First(n => n.Id == first.Id));
                var beforeOrder = vm.Notes.Select(n => n.Id).ToList();
                var idxFirst = beforeOrder.IndexOf(first.Id);
                if (idxFirst < vm.Notes.Count - 1)
                {
                    await vm.MoveAsync("Down");
                    Assert(vm.Notes.IndexOf(vm.Notes.First(n => n.Id == first.Id)) == idxFirst + 1, "move down shifts the note");
                }

                // ---- links (only if the test DB has any PR/RFQ/PO) ----
                await vm.SelectAsync(vm.Notes.First(n => n.Id == first.Id));
                vm.LinkQuery = "PR";
                if (vm.LinkResults.Count > 0)
                {
                    var target = vm.LinkResults[0];
                    await vm.PickLinkTargetAsync(target);
                    Assert(vm.SelectedNote!.HasLinks && vm.SelectedNote.Links.Any(l => l.Label == target.ChipLabel), "pick link adds it");
                    Assert(vm.LinkQuery == "" && !vm.ShowLinkResults, "picking clears the query");

                    await vm.PickLinkTargetAsync(target);
                    Assert(vm.SelectedNote.Links.Count == 1, "picking the same target twice does not duplicate");

                    var reopened = await repo.GetAsync(first.Id);
                    Assert(reopened!.Links.Count == 1, "link persisted");

                    await vm.RemoveLinkAsync(vm.SelectedNote.Links[0]);
                    Assert(!vm.SelectedNote.HasLinks, "remove link clears it");
                }

                // ---- delete ----
                await vm.DeleteInternalAsync(vm.Notes.First(n => n.Id == first.Id), confirm: false);
                await vm.DeleteInternalAsync(vm.Notes.First(n => n.Id == second.Id), confirm: false);
                Assert(vm.Notes.All(n => n.Id != first.Id && n.Id != second.Id), "delete removes notes from the list");

                // ---- heap probe: many list rebuilds must not grow unbounded ----
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var before = GC.GetTotalMemory(true);
                for (var i = 0; i < 400; i++)
                {
                    vm.FilterText = i % 2 == 0 ? marker : "";
                    await Task.Yield();
                }
                vm.FilterText = "";
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var delta = (GC.GetTotalMemory(true) - before) / 1024;
                NoteSelfCheckLog.Write($"NoteFeatureSelfCheck: heap delta over 400 rebuilds = {delta} KB");
                Assert(delta < 4096, "no unbounded heap growth");

                Report("PASS");
            }
            catch (Exception ex)
            {
                Report("FAIL " + ex);
                throw;
            }
        }

        private static void Assert(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException("failed: " + what);
        }

        private static void Report(string result) => NoteSelfCheckLog.Write("NoteFeatureSelfCheck: " + result);
    }
}
