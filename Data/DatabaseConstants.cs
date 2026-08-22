using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Maui.Storage;

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
        public const int SchemaVersion = 1;
        private const string CustomDbPathKey = "CustomDatabaseDirectory";

        public static string DefaultDatabaseDirectory => FileSystem.AppDataDirectory;

        public static string DatabaseDirectory
        {
            get => Preferences.Default.Get(CustomDbPathKey, DefaultDatabaseDirectory);
            set => Preferences.Default.Set(CustomDbPathKey, value);
        }

        public static string DatabaseFilePath => Path.Combine(DatabaseDirectory, DatabaseFilename);

        /// <summary>
        /// ForeignKeys is set here rather than as a PRAGMA in <see cref="SqlCreateTables"/> because
        /// foreign_keys is per-connection and defaults to OFF: the create script runs only when the
        /// schema version changes, so every launch after the first left ON DELETE CASCADE unenforced.
        /// Microsoft.Data.Sqlite re-applies this on every open, pooled or not.
        /// </summary>
        public static string ConnectionString => new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFilePath,
            ForeignKeys = true
        }.ToString();

        /// <summary>Per-connection settings with no connection-string equivalent, applied on every open
        /// by <see cref="SqliteDatabase.CreateConnection"/>. Both are pure configuration writes - no I/O.
        /// synchronous=NORMAL is the documented pairing for WAL; the default FULL fsyncs every commit.</summary>
        public const string SqlConnectionPragmas = "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;";

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
    ConsolidatedFrom TEXT
);
CREATE INDEX IF NOT EXISTS IX_PR_PrNo ON PurchaseRequisition(PrNo);
CREATE INDEX IF NOT EXISTS IX_PR_Status ON PurchaseRequisition(Status);
CREATE INDEX IF NOT EXISTS IX_PR_ParentPrId ON PurchaseRequisition(ParentPrId);

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
    VatType TEXT DEFAULT '5%'
);
CREATE INDEX IF NOT EXISTS IX_PO_PrId ON PurchaseOrder(PrId);

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
";
    }
}
