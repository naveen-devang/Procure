using System;
using System.Linq;
using System.Threading.Tasks;
using Procure.Data.Repositories;
using Procure.Models;

namespace Procure.Data
{
    // Proves a note survives the round trip through the real repository - create with an RTF body,
    // read the list (no body) and the full row (body), retitle, pin, reorder, delete.
    internal static class NoteRepositorySelfCheck
    {
        public static async Task RunAsync(INoteRepository repo)
        {
            var marker = "nrsc-" + Guid.NewGuid().ToString("N")[..8];
            var id = Guid.NewGuid();
            var rtf = @"{\rtf1\ansi Hello \b bold\b0  world.\par}";

            try
            {
                await repo.UpsertAsync(new Note
                {
                    Id = id, Title = marker, Body = rtf, Format = "rtf",
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, SortOrder = 5,
                }, "Hello bold world.");

                var full = await repo.GetAsync(id) ?? throw new InvalidOperationException("GetAsync returned null");
                Assert(full.Title == marker, "title round trip");
                Assert(full.Body.Contains(@"\b", StringComparison.Ordinal), "RTF body round trip keeps formatting");
                Assert(full.Format == "rtf", "format round trip");

                var list = await repo.GetListAsync();
                var row = list.FirstOrDefault(n => n.Id == id) ?? throw new InvalidOperationException("note missing from list");
                Assert(row.Snippet == "Hello bold world.", "snippet stored from plain text");
                Assert(row.Title == marker, "list carries the title");

                await repo.SetTitleAsync(id, marker + "-renamed");
                await repo.SetPinnedAsync(id, true);
                await repo.ReorderAsync(new[] { (id, 99) });
                full = await repo.GetAsync(id)!;
                Assert(full.Title == marker + "-renamed", "SetTitleAsync persists");
                Assert(full.Pinned, "SetPinnedAsync persists");
                Assert(full.SortOrder == 99, "ReorderAsync persists");

                await repo.DeleteAsync(id);
                Assert((await repo.GetAsync(id)) is null, "DeleteAsync removes the note");

                Report("PASS");
            }
            catch (Exception ex)
            {
                Report("FAIL " + ex.Message);
                try { await repo.DeleteAsync(id); } catch { }
                throw;
            }
        }

        private static void Assert(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException("failed: " + what);
        }

        private static void Report(string result) => NoteSelfCheckLog.Write("NoteRepositorySelfCheck: " + result);
    }
}
