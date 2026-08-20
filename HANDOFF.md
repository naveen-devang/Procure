# PR Board performance work — what changed and why

> Read `AGENTS.md` first. Deletion over addition, no orphaned code, no emoji, no unreleased
> subscriptions.

Baseline before this work: commit `bc3c89a`. Diff against it to see everything described here.

---

## The problem

Opening the PR Board was slow even at page size 10, simple edits wrote far more to disk than they
needed to, and several methods ran repeatedly on paths where nothing they computed could change.

It was never the database. The schema holds 336 rows in a 408 KB file and `GetAllAsync` runs 8 flat
queries with no N+1. The cost was in the visual tree.

## What was actually wrong

**The expanded detail panel was built for every card, collapsed or not.** ~830 lines of XAML —
three columns, nested `BindableLayout`s for RFQs, approvals, POs, items and custom fields — sat
inline in the card template gated only by `IsVisible`. Per Microsoft's docs, `IsVisible="false"`
*retains the element in the visual tree*, so the MAUI element, its handler and its native WinUI
control were all created. Nothing is expanded when the page opens.

**All ten modals were constructed with the page**, same reason.

**The list was torn down and rebuilt on every filter, page, theme and window resize.** `ApplyFilters`
assigned a brand-new `ObservableCollection`, which makes `BindableLayout` destroy and recreate every
child. `SettingsChanged` was one untyped event raised by every setter, and `AppShell` flips
`IsSidebarCompact` at the 1024 px breakpoint — so dragging the window edge rebuilt the whole board
repeatedly.

**`SaveAsync` rewrote every child row for scalar edits.** It unconditionally ran
`DELETE FROM PrItem WHERE PrId` and re-inserted, so flipping one status on a 29-item PR issued
1 UPDATE + 1 DELETE + 29 INSERTs. `SavePoAsync` did the same to `PurchaseOrderItem`.

**Settings getters hit the registry on every read.** `Preferences.Default.Get` with no cache, called
from inside a LINQ predicate, so the two overdue thresholds were read once per PR per filter pass.

**`MemoryOptimizerService` ran `EmptyWorkingSet` every 4 s of idle.** That does not free memory — it
evicts the process's pages to the pagefile so Task Manager shows a smaller number, and the next click
hard-faults all of it back. It made the app slower to improve a number that measures nothing.

## What changed

| Area | Change |
|---|---|
| `Utilities/LazyExpander.cs` | New ~45-line `ContentView`. Builds its `ContentTemplate` once on first expand, toggles `IsVisible` thereafter. |
| `Pages/PrListPage.xaml` | Detail panel and all ten modals wrapped in `LazyExpander`. The 830-line panel moved verbatim into a `DataTemplate` — no markup rewritten. 21 `x:Reference` bindings converted to `RelativeSource AncestorType`, which compiles (a `Source=` binding cannot). |
| `PrListPageModel` | `FilteredPrs` reconciled in place instead of replaced. Keyed settings event so only relevant keys repaint. Settings reads hoisted out of LINQ. Dead `NotifyHierarchyChanged` pass over unbound objects removed. `UpdateSelectionState` no longer runs on filter passes it cannot affect. Theme refresh coalesced (a theme click raises two events). Dead code removed. |
| `PurchaseRequisitionRepository` | New `SavePrFieldsAsync` UPSERTs the PR row only. `SaveAsync` reuses it so the SQL exists once. `SavePoAsync` deletes only departed rows and UPSERTs the rest. Interpolated-GUID deletes parameterised. |
| `SettingsService` | Every value cached in a field; `Preferences` read at most once per key per process. `SettingsChangedEventArgs` carries the changed key. |
| `MemoryOptimizerService` | Deleted, with every reference. |
| `ThemeHelper` | Result cached with an explicit `Invalidate()`, called from `ApplyThemeMode` and `Application.RequestedThemeChanged`. |
| `Procure.csproj` | `Microsoft.Maui.Controls` 10.0.100. `MauiStrictXamlCompilation` on. |

## Result

Measured by rebuilding the baseline from `bc3c89a` and comparing the XAML source generator's emitted
construction code.

| | Before | After |
|---|---|---|
| Elements built on first paint | 3,202 | **588** |
| Invisible-but-built | 85.9% | **23.0%** |
| `CalendarDatePicker`s at load | 36 | **0** |
| Modal elements at load | 475 | **0** |
| Status-only save, 29-item PR | 31 statements, 59 row mutations | **1 UPSERT, 1 row** |
| Registry reads per filter pass | ~34 | **0** |

Verified by running the app: Dashboard and PR Board render correctly, expanding a card builds the
full three-column panel with populated approval date pickers, and the heaviest modal (`AddPoModal`)
opens with correct live data.

## Trade-offs taken deliberately

- **First open of each modal now inflates it.** Startup is faster; the first click on a heavy modal
  costs its inflation once per session. Subsequent opens are instant.
- **`LazyExpander` never releases content.** The win is time-to-first-paint, not steady-state memory.
  A long session in which many cards and modals are opened drifts back up.
- **`PurchaseOrderItem` load order is now first-insert order**, not last-save order, because the
  delete-and-reinsert that rewrote it is gone. No data is affected. The upgrade path — add a
  `SortOrder` column, as `PrItem` and `RfqItem` already have — is named in a `ponytail:` comment.
- **Badge strings lost their leading status markers.** They bind to plain `Label`s with no icon font,
  so a Segoe Fluent glyph would render as tofu. The wording ("Complete", "Pending",
  "Over-allocated") plus the existing colour converters carry the meaning.

## Deliberately not done

**`BindableLayout` was not converted to `CollectionView`.** Bindable layouts have no UI
virtualization, so this is the remaining structural improvement — but it needs the page restructured
to `Grid RowDefinitions="Auto,*,Auto"` with no wrapping `ScrollView`, since a `CollectionView` nested
in a `ScrollView` gets infinite height and virtualization switches off. That is what originally
caused the "CollectionView freeze" this codebase moved away from. At page size 10 with lazy detail
panels it is not the bottleneck; it is what makes page size 50 or 100 viable later.

## Known remaining redundancy

Real, measured, and not fixed because the scale does not justify the diff on 17 PRs — revisit if the
data grows:

- `ApplyFilters` and `UpdateStatusBanner` each walk the PR list, and most paths call both, so the
  list is walked three times where one pass could produce the page, the overdue count and the pending
  count together.
- `NotifyHierarchyChanged` raises ~26 properties plus a walk of every item and approval. Scalar
  status and priority edits already raise their own notification from the setter, so most of that
  fan-out repaints things that did not change.

## Verifying a change here

```bash
dotnet build -f net10.0-windows10.0.19041.0 -v minimal
```

Close the running app and Visual Studio first — `MSB3021`/`MSB3027` copy errors mean the output
folder is locked, not that the code is broken.

Manual smoke test after touching the card template or a modal: expand a PR that has RFQs, a PCR with
approvals, POs, items and custom fields; collapse and re-expand; confirm approval date pickers still
write through; open and close all ten modals; confirm the WinUI paste hooks still fire in EditPr,
AddRfq, BatchCreate and BatchRfq — they attach on `Entry.Loaded`, which now runs later.
