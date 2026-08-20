# Procurement Chain Tracker — .NET MAUI / WinUI 3 / SQLite build spec

This document is a build plan for porting the web prototype into a native Windows desktop app using **.NET MAUI** (as the app shell/lifecycle) with **WinUI 3** controls for a modern Fluent UI, and **SQLite** for local storage. It covers data model, database schema, project structure, screen-by-screen UI direction, and a suggested build order.

---

## 1. Why this stack, and one decision you need to make first

.NET MAUI + WinUI 3 gives you a proper native Windows app (Mica backdrop, Fluent typography, system accent color, dark mode support) instead of a browser-hosted tool. SQLite gives you real relational storage instead of a flat key-value store.

The one thing the web prototype had "for free" that a desktop app doesn't: **shared, multi-user data**. SQLite on its own is a single local file. Before building, decide which of these you want:

| Option | Effort | Behavior |
|---|---|---|
| **A. Local-only SQLite** | Low | Each colleague has their own database. No sharing. Good for a solo pilot, not for a team. |
| **B. Shared SQLite file on a network drive / SharePoint sync folder** | Low–Medium | Everyone's app points at the same `.db` file over UNC path. Works for small teams, but SQLite file-locking over SMB is fragile with concurrent writers — fine for <5 people who rarely save at the exact same second. |
| **C. SQLite as local cache + lightweight sync backend (recommended for a real rollout)** | Medium–High | Each client has a local SQLite cache for offline/fast UI, and a small API (ASP.NET Core minimal API + a server-side SQL Server/Postgres/SQLite) is the source of truth. Client syncs on save/poll. |

This spec builds the schema and app so it works for **A/B immediately**, and is structured so **C** is a straightforward addition later (the repository layer is already abstracted behind an interface — see §5).

---

## 2. Domain model

Same entities as the web prototype, now as proper relational tables instead of nested JSON blobs.

```
PurchaseRequisition (PR)
 ├── 1:N  RequestForQuotation (RFQ)
 ├── 1:1  PriceComparisonRequest (PCR)
 │         └── 1:N  Approval  (one row per approver role)
 ├── 1:N  PurchaseOrder (PO)
 └── 1:N  CustomFieldValue  (one row per custom column defined by the team)

CustomColumnDefinition        — team-defined extra columns (name, data type)
```

### Entity: PurchaseRequisition
| Field | Type | Notes |
|---|---|---|
| Id | GUID (PK) | |
| PrNo | TEXT | Required, indexed |
| Description | TEXT | |
| Requestor | TEXT | |
| Priority | TEXT | `Normal` \| `Urgent` |
| Status | TEXT | See status list below |
| Notes | TEXT | |
| CreatedAt | DATETIME | |
| UpdatedAt | DATETIME | |

**Status values:** `PR Raised`, `RFQ Sent`, `Quotes Received`, `PCR Submitted`, `PCR Approved`, `PO Raised`, `Partially Delivered`, `Delivered`, `Closed`, `On Hold`, `Cancelled`

### Entity: RequestForQuotation
| Field | Type | Notes |
|---|---|---|
| Id | GUID (PK) | |
| PrId | GUID (FK → PurchaseRequisition) | |
| RfqNo | TEXT | |
| Vendor | TEXT | |
| Status | TEXT | `Sent` \| `Quote Received` |
| SentDate | DATE | |
| QuoteReceivedDate | DATE | nullable |
| QuoteAmount | DECIMAL | nullable — useful for comparing vendors before the PCR |

### Entity: PriceComparisonRequest (PCR)
| Field | Type | Notes |
|---|---|---|
| Id | GUID (PK) | |
| PrId | GUID (FK, unique) | one PCR per PR |
| PcrNo | TEXT | |
| CreatedAt | DATETIME | |

### Entity: Approval
| Field | Type | Notes |
|---|---|---|
| Id | GUID (PK) | |
| PcrId | GUID (FK → PriceComparisonRequest) | |
| Role | TEXT | `ProcurementManager` \| `CFO` \| `CEO` |
| SignedByName | TEXT | nullable — who actually signed |
| Signed | BOOLEAN | |
| SignedDate | DATE | nullable |

Computed (not stored): PCR is "Approved" when all three `Approval` rows for that PCR have `Signed = true`.

### Entity: PurchaseOrder
| Field | Type | Notes |
|---|---|---|
| Id | GUID (PK) | |
| PrId | GUID (FK → PurchaseRequisition) | |
| PoNo | TEXT | |
| Vendor | TEXT | |
| LinkedRfqId | GUID (FK, nullable) | which RFQ this PO was awarded from |
| Value | DECIMAL | |
| Status | TEXT | `Raised` \| `Delivered` \| `Closed` |
| Date | DATE | |

### Entity: CustomColumnDefinition
| Field | Type | Notes |
|---|---|---|
| Id | GUID (PK) | |
| Name | TEXT | e.g. "Cost Center" |
| DataType | TEXT | `Text` \| `Number` \| `Date` \| `Select` |
| SelectOptions | TEXT | nullable, comma-separated, only if DataType = Select |
| SortOrder | INTEGER | |

### Entity: CustomFieldValue
| Field | Type | Notes |
|---|---|---|
| Id | GUID (PK) | |
| PrId | GUID (FK) | |
| ColumnId | GUID (FK → CustomColumnDefinition) | |
| Value | TEXT | stored as text regardless of DataType, parsed on read |

Storing custom fields as name/value pairs (EAV pattern) keeps the schema stable as your team adds columns — no `ALTER TABLE` every time someone wants a new field. With realistic row counts (hundreds to low thousands of PRs) this performs fine in SQLite.

---

## 3. SQLite schema (DDL)

```sql
CREATE TABLE PurchaseRequisition (
    Id TEXT PRIMARY KEY,
    PrNo TEXT NOT NULL,
    Description TEXT,
    Requestor TEXT,
    Priority TEXT NOT NULL DEFAULT 'Normal',
    Status TEXT NOT NULL DEFAULT 'PR Raised',
    Notes TEXT,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE INDEX IX_PR_PrNo ON PurchaseRequisition(PrNo);
CREATE INDEX IX_PR_Status ON PurchaseRequisition(Status);

CREATE TABLE RequestForQuotation (
    Id TEXT PRIMARY KEY,
    PrId TEXT NOT NULL REFERENCES PurchaseRequisition(Id) ON DELETE CASCADE,
    RfqNo TEXT,
    Vendor TEXT,
    Status TEXT NOT NULL DEFAULT 'Sent',
    SentDate TEXT,
    QuoteReceivedDate TEXT,
    QuoteAmount REAL
);
CREATE INDEX IX_RFQ_PrId ON RequestForQuotation(PrId);

CREATE TABLE PriceComparisonRequest (
    Id TEXT PRIMARY KEY,
    PrId TEXT NOT NULL UNIQUE REFERENCES PurchaseRequisition(Id) ON DELETE CASCADE,
    PcrNo TEXT,
    CreatedAt TEXT NOT NULL
);

CREATE TABLE Approval (
    Id TEXT PRIMARY KEY,
    PcrId TEXT NOT NULL REFERENCES PriceComparisonRequest(Id) ON DELETE CASCADE,
    Role TEXT NOT NULL,
    SignedByName TEXT,
    Signed INTEGER NOT NULL DEFAULT 0,
    SignedDate TEXT
);
CREATE INDEX IX_Approval_PcrId ON Approval(PcrId);

CREATE TABLE PurchaseOrder (
    Id TEXT PRIMARY KEY,
    PrId TEXT NOT NULL REFERENCES PurchaseRequisition(Id) ON DELETE CASCADE,
    PoNo TEXT,
    Vendor TEXT,
    LinkedRfqId TEXT REFERENCES RequestForQuotation(Id),
    Value REAL DEFAULT 0,
    Status TEXT NOT NULL DEFAULT 'Raised',
    Date TEXT
);
CREATE INDEX IX_PO_PrId ON PurchaseOrder(PrId);

CREATE TABLE CustomColumnDefinition (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    DataType TEXT NOT NULL DEFAULT 'Text',
    SelectOptions TEXT,
    SortOrder INTEGER DEFAULT 0
);

CREATE TABLE CustomFieldValue (
    Id TEXT PRIMARY KEY,
    PrId TEXT NOT NULL REFERENCES PurchaseRequisition(Id) ON DELETE CASCADE,
    ColumnId TEXT NOT NULL REFERENCES CustomColumnDefinition(Id) ON DELETE CASCADE,
    Value TEXT
);
CREATE INDEX IX_CFV_PrId ON CustomFieldValue(PrId);
```

---

## 4. Project structure

```
ProcurementTracker/
├── ProcurementTracker.sln
├── ProcurementTracker.Core/              # .NET class library, no UI dependencies
│   ├── Models/
│   │   ├── PurchaseRequisition.cs
│   │   ├── RequestForQuotation.cs
│   │   ├── PriceComparisonRequest.cs
│   │   ├── Approval.cs
│   │   ├── PurchaseOrder.cs
│   │   └── CustomColumnDefinition.cs
│   ├── Data/
│   │   ├── AppDbContext.cs               # if using EF Core
│   │   └── DatabaseInitializer.cs
│   ├── Repositories/
│   │   ├── IPurchaseRequisitionRepository.cs   # abstraction — swap SQLite-only for API-backed sync later
│   │   └── PurchaseRequisitionRepository.cs
│   └── Services/
│       ├── DashboardMetricsService.cs    # computes overdue counts, PCR-awaiting counts, etc.
│       └── CsvExportService.cs
│
└── ProcurementTracker.WinUI/             # .NET MAUI head targeting Windows, WinUI 3 controls
    ├── App.xaml / App.xaml.cs
    ├── MainWindow.xaml
    ├── Views/
    │   ├── DashboardPage.xaml
    │   ├── PrListPage.xaml
    │   ├── PrDetailPage.xaml (or PrDetailControl as an in-place expander)
    │   ├── AddEditPrDialog.xaml
    │   ├── ManageColumnsDialog.xaml
    │   └── SettingsPage.xaml
    ├── ViewModels/                       # MVVM, CommunityToolkit.Mvvm
    │   ├── DashboardViewModel.cs
    │   ├── PrListViewModel.cs
    │   └── PrDetailViewModel.cs
    └── Converters/, Styles/, Assets/
```

**NuGet packages:**
- `sqlite-net-pcl` (simplest ORM-lite for SQLite) **or** `Microsoft.EntityFrameworkCore.Sqlite` if you prefer full EF Core with migrations
- `CommunityToolkit.Mvvm` — `ObservableObject`, `RelayCommand`, source generators for ViewModels
- `CommunityToolkit.WinUI.Controls.DataGrid` — a proper data grid control (WinUI 3 doesn't ship one natively)
- `CommunityToolkit.WinUI.UI.Controls.Segmented` or `SettingsControls` — optional, for polished settings/filter UI
- `System.Text.Json` for CSV/export helpers if needed

---

## 5. Repository abstraction (so multi-user sync is a later add, not a rewrite)

```csharp
public interface IPurchaseRequisitionRepository
{
    Task<List<PurchaseRequisition>> GetAllAsync();
    Task<PurchaseRequisition?> GetByIdAsync(Guid id);
    Task SaveAsync(PurchaseRequisition pr);
    Task DeleteAsync(Guid id);
}
```

Implement `SqlitePurchaseRequisitionRepository` now. If you move to option C (shared backend) later, you add `ApiPurchaseRequisitionRepository` implementing the same interface and swap it in dependency injection — the ViewModels never change.

---

## 6. UI direction — modern WinUI 3 (replacing the ledger/monospace look)

You said you don't like the paper-ledger aesthetic from the web version — good call for a desktop tool people use daily. Here's the direction for the native app instead:

**Visual identity:** clean Fluent Design, not a themed skin. Let WinUI 3 do what it's good at — Mica backdrop on the main window, system accent color for primary actions, Segoe UI Variable typography, 8px corner radius, subtle elevation via `CardBackgroundFillColorDefault` — rather than a custom color palette layered on top. This is what makes it feel like a real Windows app instead of a web page in a frame.

- **Backdrop:** `Mica` (or `MicaAlt` for the nav pane) via `SystemBackdrop` on the window — gives the subtle frosted look Windows 11 apps have.
- **Typography:** default Segoe UI Variable ramp — `TitleLargeTextBlockStyle` for page titles, `BodyTextBlockStyle` for table content, `CaptionTextBlockStyle` for metadata like "5d ago". Don't hand-roll font sizes.
- **Color:** don't invent a custom palette. Use theme resources (`SystemAccentColor`, `TextFillColorPrimary`, `CardBackgroundFillColorDefault`, etc.) so the app auto-adapts to light/dark mode and to the user's Windows accent color.
- **Status color-coding:** use semantic `InfoBadge` variants — `Success` (green) for Delivered/Approved, `Caution` (amber) for PO Raised/Partially Delivered, `Critical` (red) for overdue/On Hold, `Informational` (blue) for RFQ Sent/PCR Submitted, neutral for PR Raised.
- **Corners:** 8px on cards, 4px on small controls (WinUI 3 defaults — don't override).

### Screen-by-screen

**1. Shell — `NavigationView`**
Left nav pane (can auto-collapse to icons-only) with sections: **Dashboard**, **PR Board**, **Settings**. `PaneDisplayMode="LeftCompact"`, `MicaAlt` backdrop on the pane itself for the layered look Windows apps use.

**2. Dashboard page**
Row of `InfoBadge` / card tiles at the top (Total PRs, RFQs awaiting quote, PCRs awaiting signature, POs raised, Total PO value, Overdue) — use a `Grid` with `ColumnDefinitions="*,*,*,*,*,*"` or wrap in `ItemsRepeater` for responsive reflow at narrower widths. Each tile is a plain `Border` with `CardBackgroundFillColorDefault`, no drop shadow, 8px radius — flat, quiet, Fluent-native.
Below: a compact recent-activity or "needs attention" list (top N overdue / PCR-waiting items) so the dashboard is actionable, not just decorative.

**3. PR Board page — the main table**
Use `CommunityToolkit.WinUI.Controls.DataGrid` (or `ListView` with a custom `DataTemplate` row if you want expandable rows, which the toolkit DataGrid doesn't support natively).

Recommended: **`ListView` with expandable rows**, not a rigid grid — this maps better to the PR → RFQ/PCR/PO hierarchy:
- Each row: PR No (bold), description (truncated with tooltip), an `InfoBadge` row showing RFQ count / PCR status / PO count, a status `InfoBadge`, and an age indicator.
- Clicking a row (or a chevron `Expander` control) reveals a nested panel — use an `Expander` control wrapping a `Grid` with three columns (RFQs / PCR / POs), each rendered as a small `ItemsRepeater` of chip-style rows. This directly mirrors the three-panel detail view from the web prototype, but as native `Expander` + `ItemsRepeater` instead of custom HTML.
- Top toolbar: `AutoSuggestBox` for search, a `ComboBox` for status filter, `ToggleButton`-style chips for "Overdue" / "PCR awaiting signature" quick filters, `CommandBar` on the right with "Manage columns", "Export CSV", and a primary `Button` (accent-styled) for "New PR".

**4. Add/Edit PR — `ContentDialog`**
Use a `ContentDialog` (not a full navigation page) for quick add/edit — matches how Windows apps handle short forms. Group fields with `TextBlock` section headers using `BodyStrongTextBlockStyle`. Use `TextBox`, `ComboBox` (priority, status), `TextBox` with `AcceptsReturn` for notes. Custom fields render dynamically at the bottom based on `CustomColumnDefinition` rows — same idea as the web version, just native controls.

**5. RFQ / PCR / PO quick-add — inline, not modal**
Keep these inline in the expanded row (as chip-style `ItemsRepeater` + a small add form at the bottom of each mini-panel), same as the web prototype — a modal-per-line-item would be too heavy for something added 3-4 times per PR.

**6. PCR approval panel**
Three rows, one per approver role, each a `CheckBox` + `TextBlock` for signed date, inside the `Expander`'s PCR column. When a `CheckBox` is checked, prompt for `SignedByName` via a small inline `TextBox` reveal (or a `TeachingTip`) rather than a separate dialog — keeps signing fast.

**7. Manage columns — `ContentDialog`**
List of existing custom columns with a `ComboBox` for data type (`Text`/`Number`/`Date`/`Select`), delete button per row, and an add-row form at the bottom. Straightforward `ListView` + form, same pattern as the web version.

**8. Settings page**
Where you'd eventually expose: database location / shared-file path (if going with option B), theme override (light/dark/system — though WinUI respects system by default), and default overdue-threshold-days (currently hardcoded at 5/10 in the web version — make it configurable here).

### Small native touches worth adding (things the web version couldn't do)
- **`InfoBar`** at the top of the PR Board for transient messages ("3 PRs are overdue") instead of the plain status line the web version used.
- **`TeachingTip`** on first run pointing at "Manage columns" and the PCR panel, since those are the two most SAP-specific, least-obvious features.
- **Windows notifications** (via `AppNotificationManager`) for PCR-awaiting-signature reminders — genuinely useful for a CFO/CEO who won't have the app open.
- **Keyboard shortcuts** (`Ctrl+N` for new PR, `Ctrl+F` to focus search) — cheap to add, expected in a native app.

---

## 7. Suggested build order

1. **Core project**: models, SQLite schema, repository layer, seed/migration script. Get data round-tripping with unit tests before touching UI.
2. **Shell + Dashboard**: `NavigationView`, Mica backdrop, dashboard tiles wired to `DashboardMetricsService`. Gets the "does this feel native" question answered early.
3. **PR Board — read-only list first**: get the `ListView` + `Expander` hierarchy rendering real data before wiring up editing.
4. **Add/Edit PR dialog** + delete.
5. **RFQ / PCR / PO inline CRUD** inside the expander panels.
6. **Custom columns** — definition CRUD, then wire into the Add/Edit dialog and the list columns.
7. **CSV export**, quick filters, search.
8. **Polish pass**: InfoBar messaging, TeachingTip onboarding, keyboard shortcuts, empty states.
9. **Decide on multi-user story** (§1) once the single-user app is solid — don't build sync speculatively before the core UX is validated with your team.

---

## 8. Open questions to settle before you start coding

- **Sharing model** (§1: A/B/C) — this affects the repository layer and whether you need a backend at all.
- **Who can sign a PCR approval?** Right now anyone with the app can check any approver's box. Worth deciding if you want role-based restriction (e.g. only a user logged in as "CFO" can check that box) even in a v1.
- **Do you need audit history** (who changed what, when) — not in this spec's schema yet. If yes, add an `AuditLog` table (`EntityType`, `EntityId`, `FieldChanged`, `OldValue`, `NewValue`, `ChangedBy`, `ChangedAt`) and write to it from the repository layer.
- **Windows-only, or eventually cross-platform?** MAUI supports it, but this spec assumes WinUI 3 controls specifically (per your ask), which are Windows-only. If cross-platform later matters, the Core project is already portable — only the `WinUI` head would need a parallel iOS/Android/Mac head with different UI.
