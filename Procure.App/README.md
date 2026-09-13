# Procure.App — Phase 0 spike

Throwaway. Answers one question: **is a native WinUI 3 PR board measurably smoother
than the MAUI build at 20k rows?** (MIGRATION-PLAN.md, Phase 0.)

## What it is

- A minimal WinUI 3 app: `NavigationView` + one page, the PR board.
- Uses the **real** `PurchaseRequisitionRepository` + `SqliteDatabase`, linked
  directly from `..\Data` / `..\Models` / `..\Utilities` (no `Procure.Core`
  extraction yet — that's Phase 1). `SpikeShims.cs` stubs the 2 MAUI platform
  types `DatabaseConstants` touches on its unused fallback path.
- Runs against `E:\Procure\Procure\TestData\procure-20k\procure_tracker.db3`
  (override with `PROCURE_DB_DIR`).
- `ListView` + `ISupportIncrementalLoading` — the WinUI equivalent of the MAUI
  board's `RemainingItemsThreshold` + `LoadMoreCommand`.

## Build & run

**Use MSBuild, not `dotnet build`** — the WinUI XAML compiler crashes (WMC9999)
under the dotnet CLI here.

```
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" `
  Procure.App\Procure.App.csproj -t:Restore,Build -p:Configuration=Debug -p:Platform=x64

.\Procure.App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\Procure.App.exe
```

Pinned to `Microsoft.WindowsAppSDK` **1.8.x** (not the MAUI project's 2.4.0 — 2.4.0's
WinUI sub-package isn't restored and 2.3.6 fell back with a broken markup compiler).

## What to look at

The bar above the list shows:
- **frame** — rolling avg frame time / fps while the app renders
- **worst/1s** — slowest single frame in the last second (the jank metric)
- **load** — ms to fetch + render the first page
- **Scroll stress test** button — flings through the whole loaded list twice and
  reports the worst frame

Compare side-by-side with the MAUI build scrolling the same board. Decision
criterion: WinUI worst-frame clearly lower, scrolling visibly smoother.
