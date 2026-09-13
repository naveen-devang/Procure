# Procure — .NET MAUI → native WinUI 3 migration plan

**Goal:** remove the `Microsoft.Maui.Controls` wrapper layer, which is the confirmed
source of the app-wide input/layout lag (see `PERF-PLAN.md` — Mica removal made no
difference, which rules Mica out and points at the MAUI control tax).

**Non-goal:** rewriting business logic, the data layer, or the release pipeline.
Those are kept verbatim.

---

## 1. What is kept, unchanged

These move to a new UI-free `Procure.Core` assembly and compile with **zero** MAUI
reference:

| Area | Files | Notes |
|---|---|---|
| Domain models | `Models/*` (30 files) | 2 exceptions below |
| Database | `Data/SqliteDatabase.cs`, `Data/DatabaseConstants.cs`, `Data/*SelfCheck.cs` | schema, SQL, migrations, `MaterialAggregate` logic untouched |
| Repositories | `Data/Repositories/*` (all) | `PurchaseRequisitionRepository`, `CallOff`, `Note`, `Todo`, `CustomColumn`, `MaterialAggregateMaintenance` — no change |
| Business logic | `Utilities/PrLineMatcher.cs`, `MoneyFormat.cs`, `ClipboardItemParser.cs`, `RfqClipboardFormatter.cs`, `DiscountInput.cs`, `KeyboardShortcutRegistry.cs`, `DataChangeNotifier.cs`, `TodoChangeNotifier.cs` | already MAUI-free |
| Update system | `Services/UpdateService.cs`, `Utilities/Update*.cs` (Scheduler, Coordinator, StateStore) | pure Velopack + file I/O |
| Exports | `Services/CsvExportService.cs`, `Services/Export/*` (PCR Excel/PDF/rasterizer) | logic unchanged; one path helper swap |
| View models | `PageModels/*` (16 files, `CommunityToolkit.Mvvm`) | observable props + `RelayCommand` port as-is; only the 3 platform touchpoints below change |
| Metrics / links | `Services/DashboardMetricsService.cs`, `Services/LinkTargetService.cs` | MAUI-free already |
| Self-check harness | `Data/*SelfCheck.cs`, `Utilities/*SelfCheck.cs`, `BoardBench` | reused to prove parity between the two builds |

**Release pipeline (`.github/workflows/release.yml`): near-verbatim.** It already does
`dotnet publish -c Release -p:RuntimeIdentifier=win-x64 --self-contained
-p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true` and packs with
`vpk pack -u Procure -e Procure.exe`. A WinUI 3 unpackaged self-contained app
publishes with the *same* flags. The WindowsAppSDK auto-bump regex still matches.
Only change: the `publish` output path if the project layout moves.

---

## 2. What is replaced

### 2.1 Platform touchpoints in Core (small, enumerable)

| MAUI API | Count | Replacement |
|---|---|---|
| `Shell.Current.DisplayAlert` / `DisplayActionSheet` | 66 / 3 | `IDialogService` → WinUI `ContentDialog`. Mechanical find/replace. |
| `Shell.Current.GoToAsync("//route")` | 6 real targets (`prboard`, `todos`, `settings`, `main`) | `INavigationService` with an enum of destinations. |
| `MainThread.BeginInvokeOnMainThread` | 15 | `IUiDispatcher` → `DispatcherQueue.TryEnqueue`. |
| `Preferences.Default` | 25 | JSON settings file — reuse the proven pattern in `UpdateStateStore` (plain file in AppData; `ApplicationData` is unreliable unpackaged — already documented). |
| `Clipboard.Default` | 12 | `IClipboardService` → WinUI `Clipboard` + `DataPackage`. |
| `Launcher.Default.OpenAsync` | 4 | `Windows.System.Launcher.LaunchUriAsync`. |
| `AppInfo.VersionString` etc. | 10 | read from the executing assembly (`FileVersionInfo`). |
| `FileSystem.AppDataDirectory` | 5 (`DatabaseConstants`, `CrashLog`, `UpdateStateStore`, 2 export fallbacks) | `AppPaths.AppData` helper — **must resolve to the exact current path**, see §5. |
| `IValueConverter` (4 files) | 4 | WinUI signature: `Convert(object, Type, object, string language)`. Trivial. |
| `Microsoft.Maui.Graphics.Color` in color-converter VMs | ~12 refs | `Windows.UI.Color` / `Microsoft.UI` — or a tiny shim struct. |

Models with MAUI: `PastelThemeOption.cs`, `ShortcutRowViewModel.cs` — both only pull
in `Color`; same shim.

### 2.2 The UI (the bulk of the work) — new `Procure.App` (WinUI 3)

| MAUI | WinUI 3 |
|---|---|
| `AppShell` (flyout, 7 tabs, routes) | `MainWindow` + `NavigationView` (7 items) + content `Frame`. ~6 programmatic tab-switches. |
| `ContentPage` × 7 | `Page` × 7 (`PrListPage`, `Dashboard`, `CallOff`, `Todo`, `Notes`, `Settings`, + detail panel) |
| `CollectionView` (PR board, card rows, incremental load) | `ListView` with `ContainerContentChanging` phased rendering + `ISupportIncrementalLoading` collection. `RemainingItemsThreshold` → `IncrementalLoadingThreshold` / `DataFetchSize`. |
| Tabular views (CallOff week grid, Materials) | `CommunityToolkit.WinUI.Controls.DataGrid` where a real grid helps |
| 13 in-page modals via `LazyExpander` overlay | `ContentDialog` (idiomatic; removes `LazyExpander` + the placeholder-frame hack). Keep the overlay approach only if a dialog needs to be non-blocking. |
| `Pages/Controls/*` (MetricCard, PrDetailPanel, PrListSkeleton) | `UserControl` × 3 |
| `Controls/NoteEditor` + `Platforms/Windows/NoteEditorHandler` | `UserControl` wrapping `RichEditBox` directly — **simpler**, the handler indirection disappears |
| **960× `{AppThemeBinding Light=… Dark=…}`** | `ResourceDictionary.ThemeDictionaries` (Light / Dark / Default) + `{ThemeResource X}`. The 960 collapse to ~65 theme-varying brushes referenced ~130 times. **Largest single XAML task.** |
| `Resources/Styles/{Colors,Styles,AppStyles}.xaml` (2263 `StaticResource`) | WinUI `ResourceDictionary` merge — mechanical, but feeds the theme-dictionary split above |
| `{Binding}` × 996, `x:DataType` × 105 | `{Binding}` keeps working in WinUI — port as-is. Drop `x:DataType` (it's for `x:Bind`). Optionally migrate hot lists to `{x:Bind}` later; the perf win is from dropping the wrapper, not from `x:Bind`. |
| `Converter=` × 149 | keep — WinUI supports converters |
| `TapGestureRecognizer` × 31 | `Tapped` event / `ListView.ItemClick` |
| `DataTrigger` / `Trigger` × 26, VSM × 4 | `VisualStateManager` + a few converter bindings |
| `ConfigureFonts(...)` | `/Assets/Fonts/*.ttf` + `FontFamily="/Assets/Fonts/X.ttf#Family"` |
| `MauiProgram` handler mappers × ~10 | most **disappear** (they only existed to reach the native control you now use directly): `FullyRoundedDatePicker`, `TightWinUICheckBox`, `KeyboardAccessibleCollectionView` → plain XAML. `SelectableLabelText` → `IsTextSelectionEnabled` where wanted (opt-in, per `PERF-PLAN.md` A.2). `NoteToolbarNoFocusSteal` → handled in the control. |

### 2.3 Host / bootstrap

| MAUI | WinUI 3 |
|---|---|
| `MauiProgram.CreateMauiApp()` — DI registrations | `App.xaml.cs` + `Microsoft.Extensions.Hosting` (or a bare `ServiceCollection`). Every `AddSingleton` line carries over verbatim. |
| `App.xaml.cs` (MAUI) — update loop, "what's new", self-check harness, theme-change hook | WinUI `App.OnLaunched` + a `StartupService`. |
| `Platforms/Windows/App.xaml.cs` — Velopack bootstrap, single-instance `Mutex`, `UnhandledException` log | merged into the one `App.xaml.cs` — **already WinUI code**, moves verbatim (`VelopackApp.Build().Run()`, the mutex, `CrashLog`). |
| `Platforms/Windows/app.manifest` (PerMonitorV2 DPI, longPathAware) | keep as-is |
| `.csproj`: `UseMaui=true`, `Microsoft.Maui.Controls`, `CommunityToolkit.Maui` | `<UseWinUI>true</UseWinUI>`, `<WindowsPackageType>None</WindowsPackageType>`, `OutputType=WinExe`. **Keep:** `Microsoft.WindowsAppSDK`, `Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw`, `Velopack`, `System.Drawing.Common`, `CommunityToolkit.Mvvm`. **Add:** `CommunityToolkit.WinUI.Controls.DataGrid`, `CommunityToolkit.WinUI.Extensions`. **Drop:** `CommunityToolkit.Maui` (used only for `UseMauiCommunityToolkit()` — no `toolkit:` XAML, no popups/behaviors). |

### 2.4 Theme transition — mostly survives

`Utilities/ThemeCurtain.cs`, `TitleBarHelper.cs`, `NativeTheme.cs` are **already
`Microsoft.UI.Xaml` code**. They port with edits, not rewrites:
- `TitleBarHelper` gets *simpler* — you own the title bar (`AppWindow.TitleBar` +
  `Window.SetTitleBar`), no more "walk MAUI's visual tree for the `AppTitle`
  TextBlock".
- `ThemeCurtain` overlay logic is unchanged; it attaches to your root grid directly.
- Budget real time to re-tune the lockstep timing — there is a lot of hard-won
  detail in the comments there.

---

## 3. Sequencing

### Phase 0 — Spike (≈1 week, gate the whole project on it)

New `Procure.App` WinUI project. `MainWindow` + `NavigationView`. Port **only** the
PR board page + `PrListPageModel` + the card list, wired to the **real** Core
repositories against the 20k-row test DB (`TestData/procure-20k`).

**Exit criteria:**
1. Scrolling / filtering / expanding at 20k rows is measurably smoother than the
   current MAUI build (side-by-side, same machine).
2. `ListView` phased rendering + incremental loading reproduces the current
   `RemainingItemsThreshold` UX.
3. No blocker found in the `{Binding}` + `CommunityToolkit.Mvvm` reuse.

If (1) fails, stop — the lag is not where we think and this plan is wrong.

### Phase 1 — Extract `Procure.Core`, add abstractions (MAUI app still ships)

- Move Models/Data/Services-logic/Utilities-logic/PageModels into `Procure.Core`
  (net10.0, no `UseMaui`). The compiler enumerates every remaining MAUI dependency.
- Introduce `IDialogService`, `INavigationService`, `IUiDispatcher`,
  `IClipboardService`, `ISettingsStore` (JSON). Implement them **over MAUI** so the
  current app keeps working.
- Replace the 66 `DisplayAlert` / 6 `GoToAsync` / 15 `MainThread` / 25 `Preferences`
  / 12 `Clipboard` call sites with the abstractions.
- Convert the 4 converters + `Color` shim.
- **Ship a normal release from here.** Zero user-visible change — pure refactor,
  fully covered by the existing self-check harness.

### Phase 2 — WinUI shell + theme + the 7 pages

- `App.xaml.cs` bootstrap (DI, startup service, Velopack, mutex).
- Theme-dictionary split (the 960 → ~65 brushes). Port `Colors/Styles/AppStyles`.
- `NavigationView` + nav service.
- Port the 7 pages + 3 user controls + NoteEditor. Modal buttons stubbed.
- `ThemeCurtain` / `TitleBarHelper` / `NativeTheme` port.

### Phase 3 — Modals

Port the 13 modals to `ContentDialog`. Order by risk: `EditPrModal`,
`AddPoModal` (step-2 lists), `BatchCreate/BatchRfq/BatchPo`, `MergePr`, `SplitPr`,
`ExportPcr` / `PcrPreview`, `ApprovalConfig`, `AddRfq`.

### Phase 4 — Polish

Keyboard shortcuts (`KeyboardAccelerator` / `AcceleratorKeyActivated`), the handler
replacements, DataGrid where tabular, export/print verification under
self-contained publish, empty/error states, focus visuals, reduced-motion.

### Phase 5 — Parity + data migration + dogfood

- Run `ProcurementFlowSelfCheck` + all repository self-checks against the WinUI
  build. Both builds must pass identically.
- Data-path continuity (§5).
- Internal dogfood on 2–3 colleagues' machines for a week.

### Phase 6 — Cutover

Ship as a major version (`v2.0.0`). Keep the MAUI code on a `maui-legacy` branch as
a rollback. Verify a real Velopack **in-place update** from the last MAUI release to
`v2.0.0` on an installed copy before tagging.

---

## 4. Effort

Rough, solo, part-time: **4–8 weeks.** Long poles: the 25-view XAML port and the
960-entry theme-binding collapse (Phase 2), and modal parity (Phase 3). Phase 1 is
low-risk and independently valuable even if the port is paused after it.

---

## 5. Risks

| # | Risk | Mitigation |
|---|---|---|
| 1 | **Data-loss perception.** `FileSystem.AppDataDirectory` currently resolves to `%LOCALAPPDATA%\User Name\com.companyname.procure\Data\`. A WinUI app computing a different path won't find existing users' `procure_tracker.db3`, settings, or update state. | `AppPaths.AppData` returns that **exact** string. Add a first-run check: if the new path is empty but the old exists, copy (not move) the DB + state files, and log it. Ship a backup copy of the DB on first `v2` launch. |
| 2 | Velopack treats `v2` as a different app → installs alongside instead of updating. | Pack with the same `-u Procure` and same `-e Procure.exe`. Test the update handoff on an installed copy in Phase 6 before tagging. |
| 3 | `ListView` can't match `CollectionView`'s recycling/scroll-offset behaviour. | Phase 0 exit criterion. Fallback: `ItemsRepeater` + manual virtualization. |
| 4 | Theme-transition polish regresses (curtain/title-bar lockstep). | Port the existing `ThemeTransitionSelfCheck`; budget tuning time in Phase 2. |
| 5 | `System.Drawing.Common` PDF rasterizer under self-contained WinUI. | Verify in Phase 4; it's GDI+, no MAUI dependency, low risk. |
| 6 | Scope creep — "redesign while we're in here". | Explicit rule: pixel-parity port first. Visual changes are a separate project (and the board facelift is off-limits — see memory). |

---

## 5a. Standing requirements

- **Perf HUD in every phase build until cutover.** The frame-time / fps /
  worst-frame-per-second / first-page-load readout from the Phase 0 spike stays
  visible in every intermediate WinUI build, so a regression is caught the moment
  it appears. Remove it only at Phase 6.
- **Build WinUI with MSBuild, not `dotnet build`** — the XAML markup compiler
  crashes (WMC9999) under the dotnet CLI here. Spike pins
  `Microsoft.WindowsAppSDK` to 1.8.x (2.4.0's WinUI sub-package isn't restored).

## Progress

- **Phase 0** — done. WinUI 3 board on the real 20k DB, smooth. Go.
- **Phase 1a** — done (`f2f2c96`). `Procure.Core` = models, data layer,
  repositories, `PrLineMatcher`/`MoneyFormat`. MAUI-free.
- **Phase 1b** — done (`525c5ed`). All 7 view models + the 5 abstractions in Core.
  MAUI impls `Services/MauiPlatformServices.cs`, headless test impls
  `Procure.Core/Abstractions/HeadlessPlatform.cs`. `Procure.Core` is now
  `net10.0-windows` (+ System.Drawing). Self-checks pass incl. ProcurementFlow 178.
- **Phase 2 kickoff** — done + verified (`9f82d31`). `Procure.App` (WinUI 3 head):
  full DI, WinUI abstraction impls in `Procure.App/Platform/`, `JsonSettingsService`,
  `NavigationView` shell (7 tabs) + perf HUD, `PrBoardPage` drives the real
  `PrListPageModel` from DI against the 20k DB — renders real cards, user confirmed.
  6 tabs are `StubPage`; placeholder services for updates/exports/shortcuts.
- **Phase 2 — done.** All 6 pages + `PrDetailPanel` + all 11 modals ported native.
  Theme dictionaries (`Themes/AppColors.xaml`), converters, accent picker. App-side
  services all real (`UpdateService`/`PcrExportService`/`CsvExportService`/
  `WinUiKeyboardShortcutService`; pure exporters moved to `Procure.Core`). Perf:
  board revisit 7 ms/144 fps, scroll 144 fps (MAUI was ~25 fps); pages are DI
  singletons. Theme switches live (incl. NavigationView pane + card tags).
  Modals close on Esc + backdrop. MAUI settings migrate on first WinUI launch
  (`preferences.dat` → `settings.json`). PcrPreview render path verified headless.

- **Phase 6 — cutover, NOT started. What it needs:**
  1. `Procure.App.csproj`: add `<AssemblyName>Procure</AssemblyName>` so the exe is
     `Procure.exe` (matches the MAUI releases + `vpk pack -e Procure.exe`), so an
     installed MAUI copy updates *in place* instead of installing alongside.
  2. Rewrite `.github/workflows/release.yml`: point the Publish step at
     `Procure.App/Procure.App.csproj` (keep the same self-contained flags).
     **Verified 2026-09-10:** `dotnet publish` on `Procure.App` does NOT hit the
     WMC9999 XAML-compiler crash (that's a `dotnet build` incremental-Debug issue
     only) — 222 MB self-contained folder, exe runs clean. So the CI step needs no
     MSBuild call, just the project path change + drop the WindowsAppSDK-bump step's
     `Procure.csproj` reference.
  3. Keep `vpk pack -u Procure -e Procure.exe` unchanged; verify the in-place update
     from the last MAUI release (v1.0.25) → v2.0.0 on a real installed copy
     (§5 Risk 2) before tagging for real.
  4. Move MAUI code to a `maui-legacy` branch; delete `Procure.csproj` from `main`.
  5. Dogfood a week on 2-3 machines first (§5, Phase 5).
- **Also outstanding:** ship Phase 1 as a no-op refactor release (tag) — independent.

### Build commands

- WinUI app (MSBuild only — the dotnet CLI crashes the XAML compiler here):
  `MSBuild.exe Procure.App\Procure.App.csproj -t:Build -p:Configuration=Debug -p:Platform=x64`
- MAUI app (still builds): `dotnet build Procure.csproj -c Debug -f net10.0-windows10.0.19041.0`
- WinUI pages must load from the `Loaded` event (MainWindow sets `Frame.Content`
  directly, so `OnNavigatedTo` never fires) and call `SqliteDatabase.InitializeAsync()`.

---

## 6. Decision checkpoints

- **After Phase 0:** go / no-go on the whole migration, based on measured perf.
- **After Phase 1:** the refactor has value on its own — can stop here and keep
  shipping MAUI if priorities change.
- **After Phase 5 dogfood:** ship / hold.
