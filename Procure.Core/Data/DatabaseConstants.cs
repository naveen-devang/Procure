using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Procure.Data
{
    public static class DatabaseConstants
    {
        public const string DatabaseFilename = "procure_tracker.db3";

        /// <summary>
        /// Stamped into PRAGMA user_version once the schema is current. Bump this whenever a table or
        /// column is added to SqlCreateTables or MigrateSchemaAsync, or existing databases will not be
        /// re-checked and the new column will be missing at runtime. Editing the script without
        /// changing its shape - as removing the per-connection PRAGMAs did - needs no bump.
        /// </summary>
        public const int SchemaVersion = 18;

        public static string DefaultDatabaseDirectory => AppPaths.AppData;

        /// <summary>
        /// Reads / writes the saved custom database directory. The host wires these up at startup
        /// (the MAUI app backs them with Preferences; a WinUI build with its own settings file).
        /// Left unset, there is no saved custom directory and <see cref="DefaultDatabaseDirectory"/>
        /// is used - which is also what a fresh install sees.
        /// </summary>
        public static Func<string?>? SavedDirectoryReader { get; set; }
        public static Action<string>? SavedDirectoryWriter { get; set; }

        /// <summary>
        /// PROCURE_DB_DIR points the whole app at another database for the length of one run, without
        /// touching the saved path. That is how the capacity tests in Tools/generate-test-db.py are run
        /// against a 20,000-PR database while your real one stays where it is.
        /// </summary>
        // Resolved once per process: every DB call routes through these.
        private static string? _cachedDirectory;
        private static string? _cachedConnectionString;

        public static string DatabaseDirectory
        {
            get
            {
                if (_cachedDirectory is null)
                {
                    _cachedDirectory = Environment.GetEnvironmentVariable("PROCURE_DB_DIR")
                                       ?? SavedDirectoryReader?.Invoke()
                                       ?? DefaultDatabaseDirectory;
                    Directory.CreateDirectory(_cachedDirectory); // no-op when it already exists
                }
                return _cachedDirectory;
            }
            set
            {
                SavedDirectoryWriter?.Invoke(value);
                _cachedDirectory = null;
                _cachedConnectionString = null;
            }
        }

        public static string DatabaseFilePath => Path.Combine(DatabaseDirectory, DatabaseFilename);

        /// <summary>
        /// ForeignKeys is set here rather than as a PRAGMA in <see cref="SqlCreateTables"/> because
        /// foreign_keys is per-connection and defaults to OFF: the create script runs only when the
        /// schema version changes, so every launch after the first left ON DELETE CASCADE unenforced.
        /// Microsoft.Data.Sqlite re-applies this on every open, pooled or not.
        /// </summary>
        public static string ConnectionString => _cachedConnectionString ??= new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFilePath,
            ForeignKeys = true
        }.ToString();

        /// <summary>Per-connection settings with no connection-string equivalent, applied on every open
        /// by <see cref="SqliteDatabase.CreateConnection"/>. synchronous=NORMAL is the documented
        /// pairing for WAL; the default FULL fsyncs every commit. busy_timeout defaults to 0 - with
        /// nothing set, a connection that finds the file locked (most commonly a second Procure
        /// process, now blocked by the single-instance guard, but any external contention counts)
        /// throws "database is locked" immediately instead of waiting a moment for the lock to
        /// clear.</summary>
        public const string SqlConnectionPragmas = "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA busy_timeout=5000;";

        /// <summary>
        /// Recomputes the denormalised search text for one PR. Search has to match item names, vendor
        /// names and RFQ/PO numbers as well as the PR's own fields; doing that with correlated EXISTS
        /// subqueries measured 241-330ms at 20,000 PRs, while a LIKE scan of this single column
        /// measures 4.7ms - faster than the in-memory scan it replaces, with identical substring
        /// semantics and no FTS5 tokenizer to reason about.
        ///
        /// Written as SQL rather than assembled in C# so the list of searched fields exists once - and
        /// the staleness check below is built from the same expression, so it can never drift from the
        /// rebuild it is checking.
        /// Custom field values are deliberately absent - search does not cover them today either.
        /// </summary>
        private const string SqlSearchBlobExpression = @"lower(
    COALESCE(PrNo,'') || ' ' || COALESCE(Description,'') || ' ' || COALESCE(Requestor,'') || ' ' || COALESCE(ConsolidatedFrom,'') || ' ' ||
    COALESCE((SELECT group_concat(COALESCE(ItemName,'') || ' ' || COALESCE(Notes,''), ' ') FROM PrItem WHERE PrId = PurchaseRequisition.Id), '') || ' ' ||
    COALESCE((SELECT group_concat(COALESCE(Vendor,'') || ' ' || COALESCE(RfqNo,''), ' ') FROM RequestForQuotation WHERE PrId = PurchaseRequisition.Id), '') || ' ' ||
    COALESCE((SELECT group_concat(COALESCE(Vendor,'') || ' ' || COALESCE(PoNo,''), ' ') FROM PurchaseOrder WHERE PrId = PurchaseRequisition.Id), '')
)";

        // ---- v18: ranked search ----------------------------------------------------------------
        //
        // The blob above answers "does this PR contain that substring" and nothing else: it cannot
        // be indexed (a leading wildcard scans every row - 148ms at 20,000 PRs and linear from
        // there) and it carries no notion of a better or worse match, so results could only ever be
        // ordered by date. An exact PR number ranked the same as a passing mention in an item note.
        //
        // FTS5 gives both: an index, and bm25() relevance. The columns are separate so they can be
        // weighted - a hit on the PR number means far more than one in a note.
        //
        // Standalone rather than external-content: the text is assembled across four tables, so
        // there is no single content table for FTS5 to point at.
        public const string SqlCreateSearchIndex = @"
CREATE VIRTUAL TABLE IF NOT EXISTS PrSearch USING fts5(
    prno, refs, description, requestor, items, vendors, meta,
    prid UNINDEXED,
    tokenize = 'unicode61 remove_diacritics 2'
);";

        /// <summary>The searchable text, one column per weightable field. Same four tables the blob
        /// reads, plus the fields it never covered: status, priority, plant and PR type, so "urgent"
        /// and "capex" become searchable words.</summary>
        private const string SqlSearchRowExpression = @"
SELECT
    lower(COALESCE(PrNo,'')),
    lower(COALESCE(ConsolidatedFrom,'') || ' ' ||
        COALESCE((SELECT group_concat(COALESCE(RfqNo,''), ' ') FROM RequestForQuotation WHERE PrId = PurchaseRequisition.Id), '') || ' ' ||
        COALESCE((SELECT group_concat(COALESCE(PoNo,''), ' ') FROM PurchaseOrder WHERE PrId = PurchaseRequisition.Id), '')),
    lower(COALESCE(Description,'')),
    lower(COALESCE(Requestor,'')),
    lower(COALESCE((SELECT group_concat(COALESCE(ItemName,'') || ' ' || COALESCE(Notes,''), ' ') FROM PrItem WHERE PrId = PurchaseRequisition.Id), '')),
    lower(COALESCE((SELECT group_concat(COALESCE(Vendor,''), ' ') FROM RequestForQuotation WHERE PrId = PurchaseRequisition.Id), '') || ' ' ||
          COALESCE((SELECT group_concat(COALESCE(Vendor,''), ' ') FROM PurchaseOrder WHERE PrId = PurchaseRequisition.Id), '')),
    lower(COALESCE(Status,'') || ' ' || COALESCE(Priority,'') || ' ' || COALESCE(Plant,'') || ' ' || COALESCE(PrType,'')),
    Id,
    -- The index row is keyed to the requisition's rowid. prid is UNINDEXED - stored but not
    -- searchable - so deleting by it scanned all 20,000 rows and made re-indexing one PR cost
    -- 28ms on every save. By rowid it is a primary-key lookup.
    PurchaseRequisition.rowid
FROM PurchaseRequisition";

        /// <summary>Re-indexes one PR: delete then insert, which is how FTS5 updates a row.
        /// Append " WHERE Id = @Id" to both halves for a single PR.</summary>
        public const string SqlDeleteSearchRow =
            "DELETE FROM PrSearch WHERE rowid = (SELECT rowid FROM PurchaseRequisition WHERE Id = @SearchPrId);";

        public const string SqlInsertSearchRow =
            "INSERT INTO PrSearch (prno, refs, description, requestor, items, vendors, meta, prid, rowid) " +
            SqlSearchRowExpression + " WHERE Id = @SearchPrId;";

        /// <summary>Used on the version bump and by the restructure operations, which move rows in
        /// bulk. A plain DELETE, not the 'delete-all' command - that one is only accepted on a
        /// contentless or external-content table, and this one is standalone.</summary>
        public const string SqlRebuildSearchIndex =
            "DELETE FROM PrSearch;" +
            "INSERT INTO PrSearch (prno, refs, description, requestor, items, vendors, meta, prid, rowid) " +
            SqlSearchRowExpression + ";";

        /// <summary>Must be 0: one index row per requisition. Catches a write path that changed a PR
        /// without re-indexing it, the same way the blob's staleness count does.</summary>
        public const string SqlSearchIndexDriftCount = @"
SELECT (SELECT COUNT(*) FROM PurchaseRequisition) - (SELECT COUNT(*) FROM PrSearch);";

        /// <summary>Field weights for bm25(), in the column order above. A hit on the PR number or a
        /// document reference outranks one in a description, which outranks one buried in an item
        /// note. bm25 returns lower-is-better, so this sorts ascending.</summary>
        public const string SqlSearchRank = "bm25(PrSearch, 12.0, 8.0, 4.0, 3.0, 1.0, 3.0, 2.0)";

        /// <summary>Append "WHERE Id = @Id" for a single PR; run it bare to rebuild the whole table.</summary>
        public const string SqlRebuildSearchBlob =
            "UPDATE PurchaseRequisition SET SearchBlob = " + SqlSearchBlobExpression;

        /// <summary>The bulk rebuild, but touching only the rows whose text actually changed and
        /// naming them. The restructure operations move a handful of PRs; re-indexing all 20,000
        /// afterwards cost 1.7 seconds each. RETURNING makes the follow-up work proportional to
        /// what moved instead of to the size of the database.</summary>
        public const string SqlRebuildChangedSearchBlobs =
            "UPDATE PurchaseRequisition SET SearchBlob = " + SqlSearchBlobExpression +
            " WHERE COALESCE(SearchBlob,'') <> " + SqlSearchBlobExpression + " RETURNING Id;";

        /// <summary>Counts PRs whose stored search text no longer matches what it should be - i.e. rows
        /// some write path changed without refreshing the blob. Must always be 0; see DatabaseSelfCheck.</summary>
        public const string SqlStaleSearchBlobCount =
            "SELECT COUNT(*) FROM PurchaseRequisition WHERE COALESCE(SearchBlob,'') <> " + SqlSearchBlobExpression;

        // journal_mode is the one PRAGMA that persists in the database file, so setting it once at
        // creation is correct. The others that used to live here (synchronous, temp_store, foreign_keys)
        // moved above; cache_size and wal_autocheckpoint were set to their own defaults and are gone.
        public const string SqlCreateTables = @"
PRAGMA journal_mode = WAL;

CREATE TABLE IF NOT EXISTS PurchaseRequisition (
    Id TEXT PRIMARY KEY,
    PrNo TEXT NOT NULL,
    Description TEXT,
    Requestor TEXT,
    Plant TEXT DEFAULT 'RW01',
    PrType TEXT DEFAULT 'Stores&Spares',
    Priority TEXT NOT NULL DEFAULT 'Normal',
    Status TEXT NOT NULL DEFAULT 'PR Raised',
    Notes TEXT,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ParentPrId TEXT,
    ConsolidatedFrom TEXT,
    SearchBlob TEXT
);
CREATE INDEX IF NOT EXISTS IX_PR_PrNo ON PurchaseRequisition(PrNo);
CREATE INDEX IF NOT EXISTS IX_PR_Status ON PurchaseRequisition(Status);
CREATE INDEX IF NOT EXISTS IX_PR_ParentPrId ON PurchaseRequisition(ParentPrId);
-- The board's only sort order. Without it every page pays a full scan plus a temp B-tree.
CREATE INDEX IF NOT EXISTS IX_PR_CreatedAt ON PurchaseRequisition(CreatedAt DESC);
-- v12: the Raw & Packing tab filters PrType across two joins. Without this the planner had to
-- scan every PurchaseOrderItem and test the type through the joins on the far side.
CREATE INDEX IF NOT EXISTS IX_PR_PrType ON PurchaseRequisition(PrType);

CREATE TABLE IF NOT EXISTS PrItem (
    Id TEXT PRIMARY KEY,
    PrId TEXT NOT NULL REFERENCES PurchaseRequisition(Id) ON DELETE CASCADE,
    ItemName TEXT NOT NULL,
    Quantity REAL NOT NULL DEFAULT 1,
    Unit TEXT NOT NULL DEFAULT 'pcs',
    EstimatedUnitPrice REAL,
    Notes TEXT,
    SortOrder INTEGER DEFAULT 0
);
CREATE INDEX IF NOT EXISTS IX_PrItem_PrId ON PrItem(PrId);

CREATE TABLE IF NOT EXISTS RequestForQuotation (
    Id TEXT PRIMARY KEY,
    PrId TEXT NOT NULL REFERENCES PurchaseRequisition(Id) ON DELETE CASCADE,
    RfqNo TEXT,
    Vendor TEXT,
    Status TEXT NOT NULL DEFAULT 'Sent',
    SentDate TEXT,
    QuoteReceivedDate TEXT,
    QuoteAmount REAL,
    PaymentTerms TEXT,
    VatType TEXT,
    Freight REAL,
    OtherCharges REAL,
    Discount REAL,
    Incoterms TEXT,
    DeliveryLeadTime TEXT,
    Currency TEXT NOT NULL DEFAULT 'AED',
    SharedPrs TEXT,
    Warranty TEXT,
    TechnicalApproval TEXT
);
CREATE INDEX IF NOT EXISTS IX_RFQ_PrId ON RequestForQuotation(PrId);

CREATE TABLE IF NOT EXISTS RfqItem (
    Id TEXT PRIMARY KEY,
    RfqId TEXT NOT NULL REFERENCES RequestForQuotation(Id) ON DELETE CASCADE,
    PrItemId TEXT REFERENCES PrItem(Id) ON DELETE SET NULL,
    ItemName TEXT NOT NULL,
    Quantity REAL NOT NULL DEFAULT 1,
    Unit TEXT NOT NULL DEFAULT 'pcs',
    IsQuoted INTEGER NOT NULL DEFAULT 1,
    QuotedUnitPrice REAL,
    Discount REAL,
    LastPrice REAL,
    Notes TEXT,
    SortOrder INTEGER DEFAULT 0
);
CREATE INDEX IF NOT EXISTS IX_RfqItem_RfqId ON RfqItem(RfqId);
-- With foreign_keys=ON, every PrItem delete scans child tables for referencing rows unless the
-- FK column is indexed. Same for the other child-side FK indexes below.
CREATE INDEX IF NOT EXISTS IX_RfqItem_PrItemId ON RfqItem(PrItemId);

CREATE TABLE IF NOT EXISTS PriceComparisonRequest (
    Id TEXT PRIMARY KEY,
    PrId TEXT NOT NULL UNIQUE REFERENCES PurchaseRequisition(Id) ON DELETE CASCADE,
    PcrNo TEXT,
    CreatedAt TEXT NOT NULL,
    Remarks TEXT
);

CREATE TABLE IF NOT EXISTS Approval (
    Id TEXT PRIMARY KEY,
    PcrId TEXT NOT NULL REFERENCES PriceComparisonRequest(Id) ON DELETE CASCADE,
    Role TEXT NOT NULL,
    SignedByName TEXT,
    Signed INTEGER NOT NULL DEFAULT 0,
    SignedDate TEXT,
    SentDate TEXT,
    ReceivedDate TEXT,
    SortOrder INTEGER DEFAULT 0,
    RequiresMultipleDates INTEGER DEFAULT 1
);
CREATE INDEX IF NOT EXISTS IX_Approval_PcrId ON Approval(PcrId);

CREATE TABLE IF NOT EXISTS PurchaseOrder (
    Id TEXT PRIMARY KEY,
    PrId TEXT NOT NULL REFERENCES PurchaseRequisition(Id) ON DELETE CASCADE,
    PoNo TEXT,
    Vendor TEXT,
    LinkedRfqId TEXT REFERENCES RequestForQuotation(Id) ON DELETE SET NULL,
    Value REAL DEFAULT 0,
    Status TEXT NOT NULL DEFAULT 'Raised',
    Date TEXT,
    CombinedPrs TEXT,
    Currency TEXT DEFAULT 'AED',
    BaseAmount REAL DEFAULT 0,
    Freight REAL DEFAULT 0,
    OtherCharges REAL DEFAULT 0,
    Discount REAL DEFAULT 0,
    VatType TEXT DEFAULT '5%',
    TransportContractNumber TEXT,
    TransporterName TEXT,
    TransportRatePerUnit REAL,
    TransportTotal REAL,
    -- v17: 'Order' (one contract for the whole PO, the four columns above) or 'Line'
    -- (each line carries its own, in PoItemTransport). Existing orders are 'Order'.
    TransportMode TEXT DEFAULT 'Order'
);
CREATE INDEX IF NOT EXISTS IX_PO_PrId ON PurchaseOrder(PrId);
CREATE INDEX IF NOT EXISTS IX_PO_LinkedRfqId ON PurchaseOrder(LinkedRfqId);

CREATE TABLE IF NOT EXISTS PurchaseOrderItem (
    Id TEXT PRIMARY KEY,
    PoId TEXT NOT NULL REFERENCES PurchaseOrder(Id) ON DELETE CASCADE,
    PrItemId TEXT REFERENCES PrItem(Id) ON DELETE SET NULL,
    RfqItemId TEXT REFERENCES RfqItem(Id) ON DELETE SET NULL,
    ItemName TEXT NOT NULL,
    Quantity REAL NOT NULL DEFAULT 1,
    Unit TEXT DEFAULT 'pcs',
    UnitPrice REAL DEFAULT 0,
    Discount REAL DEFAULT 0,
    LineTotal REAL DEFAULT 0,
    SortOrder INTEGER DEFAULT 0
);
CREATE INDEX IF NOT EXISTS IX_PoItem_PoId ON PurchaseOrderItem(PoId);
-- v12: Raw & Packing groups by material name and fetches one material's lines on expand; both
-- want the name ordered rather than scanned and sorted into a temp B-tree.
CREATE INDEX IF NOT EXISTS IX_PoItem_ItemName ON PurchaseOrderItem(ItemName);

-- v17: transport arranged per PO line. One row per contract a line's quantity moves under -
-- normally one, two when a quantity is split across contracts. Only populated for orders in
-- TransportMode 'Line'; a whole-order PO keeps using PurchaseOrder's four transport columns.
CREATE TABLE IF NOT EXISTS PoItemTransport (
    Id              TEXT PRIMARY KEY,
    PoItemId        TEXT NOT NULL REFERENCES PurchaseOrderItem(Id) ON DELETE CASCADE,
    Quantity        REAL NOT NULL DEFAULT 0,
    ContractNumber  TEXT,
    TransporterName TEXT,
    RatePerUnit     REAL,
    SortOrder       INTEGER DEFAULT 0
);
CREATE INDEX IF NOT EXISTS IX_PoItemTransport_PoItemId ON PoItemTransport(PoItemId);

-- v13: the Raw & Packing tab's collapsed rows, denormalised. Computing them live meant one
-- GROUP BY over every eligible PO item on every open - 244ms at 20,000 PRs and linear from
-- there, so roughly 12 seconds at a million. Reading them from here is O(materials).
-- Kept in sync by MaterialAggregateMaintenance, alongside the SearchBlob refresh that every
-- PR/PO write path already performs, and asserted never to drift by DatabaseSelfCheck.
CREATE TABLE IF NOT EXISTS MaterialAggregate (
    MaterialKey    TEXT PRIMARY KEY,
    MaterialName   TEXT NOT NULL,
    LineCount      INTEGER NOT NULL,
    TotalOrdered   REAL NOT NULL,
    TotalCalledOff REAL NOT NULL,
    Unit           TEXT NOT NULL DEFAULT 'pcs',
    -- v15: the newest PO date the material appears on, so the tab can order by recency.
    -- Deliberately the PO date and not the last call-off: logging a delivery must not
    -- reshuffle the list under the person logging it.
    LastActivity   TEXT
);
CREATE INDEX IF NOT EXISTS IX_MaterialAggregate_Name ON MaterialAggregate(MaterialName COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS IX_PoItem_PrItemId ON PurchaseOrderItem(PrItemId);
CREATE INDEX IF NOT EXISTS IX_PoItem_RfqItemId ON PurchaseOrderItem(RfqItemId);

CREATE TABLE IF NOT EXISTS CustomColumnDefinition (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    DataType TEXT NOT NULL DEFAULT 'Text',
    SelectOptions TEXT,
    SortOrder INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS CustomFieldValue (
    Id TEXT PRIMARY KEY,
    PrId TEXT NOT NULL REFERENCES PurchaseRequisition(Id) ON DELETE CASCADE,
    ColumnId TEXT NOT NULL REFERENCES CustomColumnDefinition(Id) ON DELETE CASCADE,
    Value TEXT
);
CREATE INDEX IF NOT EXISTS IX_CFV_PrId ON CustomFieldValue(PrId);
CREATE INDEX IF NOT EXISTS IX_CFV_ColumnId ON CustomFieldValue(ColumnId);

CREATE TABLE IF NOT EXISTS PoItemCallOff (
    Id TEXT PRIMARY KEY,
    PoItemId TEXT NOT NULL REFERENCES PurchaseOrderItem(Id) ON DELETE CASCADE,
    CallOffDate TEXT NOT NULL,
    Quantity REAL NOT NULL,
    Note TEXT
);
CREATE INDEX IF NOT EXISTS IX_PoItemCallOff_PoItemId ON PoItemCallOff(PoItemId);

-- Personal task list (schema v7). New table only - the whole CREATE script re-runs on a
-- version bump, so existing databases pick this up with no MigrateSchemaAsync branch needed.
-- LinkedEntity* are legacy single-link columns (v7-v8); v9 moved links to TodoTaskLink. They stay
-- for the one-time backfill in MigrateSchemaAsync and are no longer read or written.
CREATE TABLE IF NOT EXISTS TodoTask (
    Id TEXT PRIMARY KEY,
    Title TEXT NOT NULL,
    Notes TEXT,
    Priority INTEGER NOT NULL DEFAULT 0,
    IsDone INTEGER NOT NULL DEFAULT 0,
    DueDate TEXT,
    CompletedAt TEXT,
    SortOrder INTEGER NOT NULL DEFAULT 0,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    ParentId TEXT REFERENCES TodoTask(Id) ON DELETE CASCADE,
    RecurrenceRule TEXT,
    PlannedForDate TEXT,
    LinkedEntityType TEXT,
    LinkedEntityId TEXT,
    LinkedEntityLabel TEXT
);
CREATE INDEX IF NOT EXISTS IX_TodoTask_Done_Due ON TodoTask(IsDone, DueDate);

-- v9: a task can link many PRs / RFQs / POs. One row per link; cascades when the task is deleted.
CREATE TABLE IF NOT EXISTS TodoTaskLink (
    TaskId TEXT NOT NULL REFERENCES TodoTask(Id) ON DELETE CASCADE,
    EntityType TEXT NOT NULL,
    EntityId TEXT NOT NULL,
    EntityLabel TEXT,
    PRIMARY KEY (TaskId, EntityId)
);
CREATE INDEX IF NOT EXISTS IX_TodoTaskLink_Entity ON TodoTaskLink(EntityId);

-- v10: freeform notes. Body is RTF (see NoteEditorHandler); Snippet is the first plain-text chars,
-- kept for the list so bodies never load until a note is opened. New table only - no migration.
CREATE TABLE IF NOT EXISTS Note (
    Id         TEXT PRIMARY KEY,
    Title      TEXT NOT NULL DEFAULT '',
    Body       TEXT NOT NULL DEFAULT '',
    Format     TEXT NOT NULL DEFAULT 'rtf',
    Snippet    TEXT,
    Pinned     INTEGER NOT NULL DEFAULT 0,
    SortOrder  INTEGER NOT NULL DEFAULT 0,
    CreatedAt  TEXT NOT NULL,
    UpdatedAt  TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Note_Order ON Note(Pinned, SortOrder);

-- v11: a note can link many PRs / RFQs / POs, same shape as TodoTaskLink.
CREATE TABLE IF NOT EXISTS NoteLink (
    NoteId TEXT NOT NULL REFERENCES Note(Id) ON DELETE CASCADE,
    EntityType TEXT NOT NULL,
    EntityId TEXT NOT NULL,
    EntityLabel TEXT,
    PRIMARY KEY (NoteId, EntityId)
);
CREATE INDEX IF NOT EXISTS IX_NoteLink_Entity ON NoteLink(EntityId);
";

        // Every linkable entity - the PR / RFQ / PO rows a task or a note can point at - unordered
        // and unfiltered. Never run on its own: the two queries below wrap it, and both bound their
        // result. Tasks and Notes each used to materialise this whole union and hold it for the
        // app's lifetime - measured at ~48MB per copy on a 20,000-PR database, for a picker that
        // shows a dozen rows at a time.
        private const string SqlLinkTargetUnion = @"
SELECT 'PR' AS T, Id, PrNo || ' — ' || COALESCE(NULLIF(Description,''), 'PR') AS L, CreatedAt AS Ord
FROM PurchaseRequisition WHERE ParentPrId IS NULL
UNION ALL
SELECT 'RFQ', r.Id, COALESCE(NULLIF(r.RfqNo,''), 'RFQ') || ' — ' || COALESCE(NULLIF(r.Vendor,''), 'vendor'),
       (SELECT CreatedAt FROM PurchaseRequisition WHERE Id = r.PrId)
FROM RequestForQuotation r
UNION ALL
SELECT 'PO', p.Id, COALESCE(NULLIF(p.PoNo,''), 'PO') || ' — ' || COALESCE(NULLIF(p.Vendor,''), 'vendor'),
       (SELECT CreatedAt FROM PurchaseRequisition WHERE Id = p.PrId)
FROM PurchaseOrder p";

        /// <summary>What the link picker types against: matches on the label, newest first, capped.</summary>
        public const string SqlLinkTargetSearch = @"
SELECT T, Id, L FROM (" + SqlLinkTargetUnion + @")
WHERE L LIKE @q ESCAPE '\'
ORDER BY Ord DESC
LIMIT @limit;";

        /// <summary>Labels for links a task or note already holds. The id parameters are built by the
        /// caller; never inlined as literals.</summary>
        public const string SqlLinkTargetsByIdsTemplate = @"
SELECT T, Id, L FROM (" + SqlLinkTargetUnion + @")
WHERE Id IN ({0});";
        /// <summary>The eligible-line join every material figure is computed from. Raw and Packing
        /// Material PRs only, matching what the tab shows.</summary>
        private const string SqlEligibleMaterialLines = @"
FROM PurchaseOrderItem poi
JOIN PurchaseOrder po ON poi.PoId = po.Id
JOIN PurchaseRequisition pr ON po.PrId = pr.Id
WHERE pr.PrType IN ('Raw Material', 'Packing Material')";

        /// <summary>One aggregate row per material, computed fresh. Append a key filter to scope it.</summary>
        private const string SqlComputeMaterialAggregates = @"
SELECT lower(TRIM(poi.ItemName)) AS K,
       TRIM(poi.ItemName) AS N,
       COUNT(*) AS C,
       COALESCE(SUM(poi.Quantity), 0) AS O,
       COALESCE(SUM((SELECT COALESCE(SUM(Quantity), 0) FROM PoItemCallOff WHERE PoItemId = poi.Id)), 0) AS CO,
       MIN(COALESCE(NULLIF(poi.Unit, ''), 'pcs')) AS U,
       MAX(COALESCE(po.Date, '')) AS LA
" + SqlEligibleMaterialLines;

        /// <summary>Rebuilds every material's aggregate. Used on first migration and by the
        /// restructure operations, which move POs between PRs in bulk.</summary>
        public const string SqlRebuildAllMaterialAggregates = @"
DELETE FROM MaterialAggregate;
INSERT INTO MaterialAggregate (MaterialKey, MaterialName, LineCount, TotalOrdered, TotalCalledOff, Unit, LastActivity)
" + SqlComputeMaterialAggregates + @"
GROUP BY K
" + SqlMaterialAggregateUpsert + ";";

        /// <summary>Last writer wins, instead of the insert failing.
        ///
        /// The refresh below is a DELETE followed by an INSERT, and most callers run it with no
        /// transaction around the pair. Two of them overlapping - which is all it takes for two
        /// save paths to fire at once - interleaves as delete, delete, insert, insert, and the
        /// second insert hit "UNIQUE constraint failed: MaterialAggregate.MaterialKey" and put an
        /// error dialog in front of the user for nothing. Every writer recomputes the same row from
        /// the same live data, so overwriting is not just safe here, it is the correct answer.</summary>
        private const string SqlMaterialAggregateUpsert = @"
ON CONFLICT(MaterialKey) DO UPDATE SET
    MaterialName   = excluded.MaterialName,
    LineCount      = excluded.LineCount,
    TotalOrdered   = excluded.TotalOrdered,
    TotalCalledOff = excluded.TotalCalledOff,
    Unit           = excluded.Unit,
    LastActivity   = excluded.LastActivity";

        /// <summary>The two halves of the refresh, kept separately so a test can run just the write
        /// half twice and prove the upsert above actually holds. Both take the caller's parameter
        /// list as `{0}`.</summary>
        public const string SqlRefreshMaterialAggregatesDeleteTemplate =
            "DELETE FROM MaterialAggregate WHERE MaterialKey IN ({0});";

        public const string SqlRefreshMaterialAggregatesInsertTemplate = @"
INSERT INTO MaterialAggregate (MaterialKey, MaterialName, LineCount, TotalOrdered, TotalCalledOff, Unit, LastActivity)
" + SqlComputeMaterialAggregates + @"
  AND lower(TRIM(poi.ItemName)) IN ({0})
GROUP BY K
" + SqlMaterialAggregateUpsert + ";";

        /// <summary>Recomputes only the given material keys - `{0}` is the caller's parameter list.
        /// A key with no eligible lines left simply loses its row, which is what the DELETE is for.</summary>
        public const string SqlRefreshMaterialAggregatesTemplate =
            SqlRefreshMaterialAggregatesDeleteTemplate + SqlRefreshMaterialAggregatesInsertTemplate;

        /// <summary>v14 repair: reattach quote lines that were never given a PR line to point at.
        ///
        /// Merging PRs used to copy quote lines without their PrItemId, so every quote on every
        /// merged PR was orphaned and could only be found again by matching its item text. On a
        /// merged PR that is exactly the case that breaks: two source PRs asking for the same item
        /// leave two PR lines with identical names, and the text match returned the first one to
        /// both quote lines - so a 12 NOS line and a 33 NOS line both read 12, and the PO wizard
        /// refused the order as over-allocated.
        ///
        /// The pairing rule matches PrLineMatcher exactly: a PR line already spoken for by a linked
        /// quote line on the same RFQ is off the table, and the rest are handed out one-to-one in
        /// SortOrder. Row numbers on both sides are compared, so the Nth unlinked quote line of a
        /// name takes the Nth free PR line of that name.
        ///
        /// The pairs are materialised into an indexed temp table first, and only then joined back.
        /// Expressing the same thing as a correlated subquery on the UPDATE re-evaluated both window
        /// functions over the whole table for every candidate row: 187 seconds at 20,000 PRs against
        /// 4 seconds this way, for byte-identical results - and that gap grows with the row count.</summary>
        public const string SqlRelinkOrphanedRfqItems = @"
CREATE TEMP TABLE IF NOT EXISTS _relink_rfq (RfqItemId TEXT PRIMARY KEY, PrItemId TEXT NOT NULL);
DELETE FROM _relink_rfq;

INSERT OR IGNORE INTO _relink_rfq (RfqItemId, PrItemId)
WITH free_pr AS (
    SELECT r.Id AS RfqId, p.Id AS PrItemId, lower(TRIM(p.ItemName)) AS Nm,
           ROW_NUMBER() OVER (PARTITION BY r.Id, lower(TRIM(p.ItemName)) ORDER BY p.SortOrder, p.Id) AS Rn
    FROM RequestForQuotation r
    JOIN PrItem p ON p.PrId = r.PrId
    WHERE NOT EXISTS (SELECT 1 FROM RfqItem x WHERE x.RfqId = r.Id AND x.PrItemId = p.Id)
),
loose AS (
    SELECT i.Id AS RfqItemId, i.RfqId, lower(TRIM(i.ItemName)) AS Nm,
           ROW_NUMBER() OVER (PARTITION BY i.RfqId, lower(TRIM(i.ItemName)) ORDER BY i.SortOrder, i.Id) AS Rn
    FROM RfqItem i
    WHERE i.PrItemId IS NULL AND TRIM(i.ItemName) <> ''
)
SELECT l.RfqItemId, f.PrItemId
FROM loose l
JOIN free_pr f ON f.RfqId = l.RfqId AND f.Nm = l.Nm AND f.Rn = l.Rn;

UPDATE RfqItem
SET PrItemId = (SELECT PrItemId FROM _relink_rfq WHERE RfqItemId = RfqItem.Id)
WHERE PrItemId IS NULL AND Id IN (SELECT RfqItemId FROM _relink_rfq);

DROP TABLE _relink_rfq;";

        /// <summary>v14 repair: the same reattachment for PO lines, which lost their link the same
        /// way (a PO raised from an orphaned quote copied the missing link straight through).</summary>
        public const string SqlRelinkOrphanedPoItems = @"
CREATE TEMP TABLE IF NOT EXISTS _relink_po (PoItemId TEXT PRIMARY KEY, PrItemId TEXT NOT NULL);
DELETE FROM _relink_po;

INSERT OR IGNORE INTO _relink_po (PoItemId, PrItemId)
WITH free_pr AS (
    SELECT po.Id AS PoId, p.Id AS PrItemId, lower(TRIM(p.ItemName)) AS Nm,
           ROW_NUMBER() OVER (PARTITION BY po.Id, lower(TRIM(p.ItemName)) ORDER BY p.SortOrder, p.Id) AS Rn
    FROM PurchaseOrder po
    JOIN PrItem p ON p.PrId = po.PrId
    WHERE NOT EXISTS (SELECT 1 FROM PurchaseOrderItem x WHERE x.PoId = po.Id AND x.PrItemId = p.Id)
),
loose AS (
    SELECT i.Id AS PoItemId, i.PoId, lower(TRIM(i.ItemName)) AS Nm,
           ROW_NUMBER() OVER (PARTITION BY i.PoId, lower(TRIM(i.ItemName)) ORDER BY i.SortOrder, i.Id) AS Rn
    FROM PurchaseOrderItem i
    WHERE i.PrItemId IS NULL AND TRIM(i.ItemName) <> ''
)
SELECT l.PoItemId, f.PrItemId
FROM loose l
JOIN free_pr f ON f.PoId = l.PoId AND f.Nm = l.Nm AND f.Rn = l.Rn;

UPDATE PurchaseOrderItem
SET PrItemId = (SELECT PrItemId FROM _relink_po WHERE PoItemId = PurchaseOrderItem.Id)
WHERE PrItemId IS NULL AND Id IN (SELECT PoItemId FROM _relink_po);

DROP TABLE _relink_po;";

        /// <summary>v14 repair: give combined POs the line items they were never written.
        ///
        /// A combined PO stored money and nothing else, so its PRs stayed "Unordered" for ever and
        /// the Raw &amp; Packing tab never saw the quantity. The lines are derived exactly as the
        /// fixed code now derives them - from the quote the PO is linked to - so a repaired PO and a
        /// newly raised one agree. Deliberately narrowed to combined POs (CombinedPrs non-empty)
        /// with no lines at all: a single-PO row with no items is a lump-sum order or one the user
        /// emptied on purpose, and must not have items invented for it.</summary>
        public const string SqlBackfillCombinedPoItems = @"
INSERT INTO PurchaseOrderItem (Id, PoId, PrItemId, RfqItemId, ItemName, Quantity, Unit, UnitPrice, Discount, LineTotal, SortOrder)
SELECT lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2)
            || '-a' || substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6))),
       po.Id,
       ri.PrItemId,
       ri.Id,
       ri.ItemName,
       ri.Quantity,
       COALESCE(NULLIF(ri.Unit, ''), 'pcs'),
       ri.QuotedUnitPrice,
       ri.Discount,
       ri.Quantity * MAX(0, COALESCE(ri.QuotedUnitPrice, 0) - COALESCE(ri.Discount, 0)),
       ri.SortOrder
FROM PurchaseOrder po
JOIN RfqItem ri ON ri.RfqId = po.LinkedRfqId
WHERE COALESCE(po.CombinedPrs, '') <> ''
  AND po.LinkedRfqId IS NOT NULL
  AND ri.IsQuoted = 1
  AND TRIM(ri.ItemName) <> ''
  AND NOT EXISTS (SELECT 1 FROM PurchaseOrderItem x WHERE x.PoId = po.Id);";

        /// <summary>The material keys a PR's PO items currently name. Captured before a write so the
        /// materials a rename or delete moves rows *away from* are recomputed too.</summary>
        public const string SqlMaterialKeysForPr = @"
SELECT DISTINCT lower(TRIM(poi.ItemName))
FROM PurchaseOrderItem poi
JOIN PurchaseOrder po ON poi.PoId = po.Id
WHERE po.PrId = @PrId;";

        /// <summary>The material key one PO item names.</summary>
        public const string SqlMaterialKeyForPoItem =
            "SELECT lower(TRIM(ItemName)) FROM PurchaseOrderItem WHERE Id = @PoItemId;";

        /// <summary>Rows where the stored aggregate disagrees with a fresh computation, in either
        /// direction - a stale row, a missing one, or one that should no longer exist. Must always be
        /// 0; see DatabaseSelfCheck. This is the same guard SqlStaleSearchBlobCount provides for the
        /// search column, and for the same reason: a missed write path is otherwise silent.</summary>
        public const string SqlStaleMaterialAggregateCount = @"
SELECT
    (SELECT COUNT(*) FROM (
        SELECT K, N, C, O, CO FROM (" + SqlComputeMaterialAggregates + @" GROUP BY K)
        EXCEPT
        SELECT MaterialKey, MaterialName, LineCount, TotalOrdered, TotalCalledOff FROM MaterialAggregate
    ))
    +
    (SELECT COUNT(*) FROM (
        SELECT MaterialKey, MaterialName, LineCount, TotalOrdered, TotalCalledOff FROM MaterialAggregate
        EXCEPT
        SELECT K, N, C, O, CO FROM (" + SqlComputeMaterialAggregates + @" GROUP BY K)
    ));";
    }
}
