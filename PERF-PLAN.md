# Procure — performance investigation & plan

**Status:** 2026-09-08. Symptom: whole UI feels heavy/laggy from launch, on every
colleague's machine, regardless of database size. Not a regression — it has always
felt like this.

## What was ruled out

| Suspected cause | Evidence it's not this |
|---|---|
| Debug build used as daily driver | Rebuilt `-c Release --self-contained` (`bin/PerfTest`). User reports it feels identical. |
| Data volume | Slow with tens of PRs and with thousands; colleagues' DB sizes vary, all slow. |
| Memory leak / GC degradation | Same from launch, does not worsen the longer the app is open. |
| Naive app code | PR board already heavily optimised: bounded containers, guarded virtualization, compiled bindings (`x:DataType` on every page), lazy modal inflation, flattened layouts, no sync-over-async on the UI thread. |
| A specific slow screen | "Everything, from launch" — clicks, hover, resize, tab switch all feel heavy. |

## Working diagnosis

The cost is in the **.NET MAUI layer**, not this app. `Microsoft.Maui.Controls` on
Windows wraps every control over a native WinUI 3 control and runs its own
measure/arrange pass plus gesture bridging on top. For a dense desktop LOB app that
per-interaction overhead is constant and cannot be optimised away from application
code — the moves that would help have already been made.

## Plan

### Phase A — squeeze MAUI (days, ships incrementally, no structural change)

Each item is independently shippable. Measure after each.

1. **Drop the Mica backdrop.** ← *in progress*
   `MicaBackdrop` puts the window on the DWM backdrop composition path.
   `TitleBarHelper` already paints the window root grid with a solid opaque brush
   ("Mica never shows anywhere") and every page paints an opaque background, so Mica
   is currently invisible — removing it is a **pure perf test with no visual change**.
   Ship one build with it removed, have colleagues report back. This single
   experiment tells us how much of the lag is Mica vs the MAUI floor.

2. **Make "select any label" opt-in.** Today `LabelHandler.Mapper` runs on *every*
   `Label` in the app: a parent-tree walk (`HasTapGestureAncestor`) plus
   `IsTextSelectionEnabled = true` on hundreds of `TextBlock`s per page. Move it to
   the handful of labels that need copy (via `ClassId`).

3. **Trim `AppThemeBinding` churn / residual nested layouts** where cheap.

Expected combined gain: ~15–30%. Better, not "snappy."

### Phase B — drop the MAUI layer, go native WinUI 3 (weeks, same language)

Only if Phase A doesn't reach "acceptable."

**Keep unchanged:** all C#, MVVM (`CommunityToolkit.Mvvm`), `Microsoft.Data.Sqlite`,
every repository/service, business logic (`PrLineMatcher` etc.), the DB schema,
Velopack, the GitHub Actions release pipeline.

**Replace:** `Microsoft.Maui.Controls` → WinUI 3 `Page`s +
`CommunityToolkit.WinUI` DataGrid; the ~10 MAUI handler mappers; XAML dialect port
(~70% mechanical). This removes exactly the layer causing the lag. It is the
migration the original `procurement-tracker-winui3-spec.md` effectively intended,
and it is far cheaper and lower-risk than the Tauri rewrite (which was evaluated
and declined — see the port memo).

## Measurement

Phase A step 1 is measured by feel across the team (ship + ask). If we want numbers,
add interaction-timing logging (dispatcher-queue latency sampled on a timer, written
to the same file `CrashLog` uses) before Phase A step 2.
