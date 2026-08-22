#!/usr/bin/env python3
"""Generates a Procure database of arbitrary size for capacity testing.

The schema is read out of Data/DatabaseConstants.cs rather than duplicated here, so a column added
to the app cannot silently drift from the test data.

    python Tools/generate-test-db.py --prs 20000 --out C:/temp/procure-20k

Point the app at the result without touching your real database:

    PROCURE_DB_DIR=C:/temp/procure-20k  Procure.exe

Defaults model a mature requisition - one that completed the whole lifecycle - because that is the
shape a database accumulates over years, and it is the shape that costs the most to load.
"""

import argparse
import datetime
import os
import random
import re
import sqlite3
import sys
import uuid

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

STATUSES = ["PR Raised", "RFQ Sent", "Quotes Received", "PCR Submitted", "PCR Approved",
            "PO Raised", "Partially Delivered", "Delivered", "Closed", "On Hold"]
PRIORITIES = ["Normal"] * 8 + ["Urgent"] * 2
PLANTS = ["RW01", "RW02", "RW03"]
PR_TYPES = ["Stores&Spares", "Capex", "Services"]
VENDORS = [f"Vendor {chr(65 + i % 26)}{i} Trading LLC" for i in range(400)]
ITEMS = [f"{a} {b}"
         for a in ("Bearing", "Gasket", "Valve", "Pump", "Motor", "Filter", "Seal", "Coupling",
                   "Sensor", "Cable")
         for b in ("SKF 6205", "DN50 PN16", "3in 150#", "5.5kW", "HEPA G4", "Viton", "Flexible",
                   "PT100", "4-core", "Stainless")]


def read_schema():
    """The CREATE statements straight out of the app, minus the PRAGMA lines sqlite3 runs itself."""
    source = open(os.path.join(REPO, "Data", "DatabaseConstants.cs"), encoding="utf-8-sig").read()
    match = re.search(r'public const string SqlCreateTables = @"(.*?)";', source, re.S)
    if not match:
        sys.exit("Could not find SqlCreateTables in Data/DatabaseConstants.cs")
    body = match.group(1).replace('""', '"')
    return [s.strip() for s in body.split(";") if s.strip() and not s.strip().startswith("PRAGMA")]


def insert(table, columns):
    return f"INSERT INTO {table} ({','.join(columns)}) VALUES ({','.join('?' * len(columns))})"


def generate(path, n_prs, items_per_pr, rfqs_per_pr, items_per_rfq, pos_per_pr, items_per_po,
             pcr_share, approvals_per_pcr, custom_fields, seed):
    for suffix in ("", "-wal", "-shm"):
        if os.path.exists(path + suffix):
            os.remove(path + suffix)

    db = sqlite3.connect(path)
    db.execute("PRAGMA journal_mode=WAL")
    db.execute("PRAGMA synchronous=OFF")
    for statement in read_schema():
        db.execute(statement)

    # Stamped as the pre-SearchBlob schema so opening it exercises the real migration, exactly as
    # an existing user's database would. Bump to match DatabaseConstants.SchemaVersion to skip that.
    db.execute("PRAGMA user_version = 1")

    rnd = random.Random(seed)
    start = datetime.date(2023, 1, 1)

    columns = [str(uuid.uuid4()) for _ in range(custom_fields)]
    db.executemany(insert("CustomColumnDefinition", ["Id", "Name", "DataType", "SortOrder"]),
                   [(cid, f"Field{i}", "Text", i) for i, cid in enumerate(columns)])

    prs, pr_items, rfqs, rfq_items = [], [], [], []
    pcrs, approvals, pos, po_items, custom_values = [], [], [], [], []

    for n in range(n_prs):
        pr_id = str(uuid.uuid4())
        created = (start + datetime.timedelta(days=rnd.randint(0, 1000))).isoformat() + "T09:00:00"
        prs.append((pr_id, f"PR-{100000 + n}",
                    f"{rnd.choice(ITEMS)} replacement for line {rnd.randint(1, 40)}",
                    f"Requestor {rnd.randint(1, 60)}", rnd.choice(PLANTS), rnd.choice(PR_TYPES),
                    rnd.choice(PRIORITIES), rnd.choice(STATUSES), "", created, created, ""))

        for k in range(items_per_pr):
            pr_items.append((str(uuid.uuid4()), pr_id, rnd.choice(ITEMS), rnd.randint(1, 50), "pcs",
                             rnd.randint(10, 5000), "", k))

        for k in range(custom_fields):
            custom_values.append((str(uuid.uuid4()), pr_id, columns[k], f"val{rnd.randint(1, 99)}"))

        for r in range(rfqs_per_pr):
            rfq_id = str(uuid.uuid4())
            rfqs.append((rfq_id, pr_id, f"RFQ-{n}-{r}", rnd.choice(VENDORS), "Quote Received",
                         created, created, rnd.randint(500, 90000), "30 Days Net", "5%", 0, 0, 0,
                         "DDP", "2 weeks", "AED", "", "12 months", "Approved"))
            for k in range(items_per_rfq):
                rfq_items.append((str(uuid.uuid4()), rfq_id, rnd.choice(ITEMS), rnd.randint(1, 50),
                                  "pcs", 1, rnd.randint(10, 5000), 0, 0, "", k))

        if rnd.random() < pcr_share:
            pcr_id = str(uuid.uuid4())
            pcrs.append((pcr_id, pr_id, f"PCR-{n}", created, ""))
            for k in range(approvals_per_pcr):
                approvals.append((str(uuid.uuid4()), pcr_id, f"Role {k}", f"Signer {k}",
                                  rnd.randint(0, 1), created, created, created, k, 1))

        for p in range(pos_per_pr):
            po_id = str(uuid.uuid4())
            pos.append((po_id, pr_id, f"PO-{n}-{p}", rnd.choice(VENDORS), rnd.randint(500, 90000),
                        "Raised", created, "", "AED", 0, 0, 0, 0, "5%"))
            for k in range(items_per_po):
                po_items.append((str(uuid.uuid4()), po_id, rnd.choice(ITEMS), rnd.randint(1, 50),
                                 "pcs", rnd.randint(10, 5000), 0, 0, k))

    batches = [
        ("PurchaseRequisition", "Id PrNo Description Requestor Plant PrType Priority Status Notes CreatedAt UpdatedAt ConsolidatedFrom", prs),
        ("PrItem", "Id PrId ItemName Quantity Unit EstimatedUnitPrice Notes SortOrder", pr_items),
        ("RequestForQuotation", "Id PrId RfqNo Vendor Status SentDate QuoteReceivedDate QuoteAmount PaymentTerms VatType Freight OtherCharges Discount Incoterms DeliveryLeadTime Currency SharedPrs Warranty TechnicalApproval", rfqs),
        ("RfqItem", "Id RfqId ItemName Quantity Unit IsQuoted QuotedUnitPrice Discount LastPrice Notes SortOrder", rfq_items),
        ("PriceComparisonRequest", "Id PrId PcrNo CreatedAt Remarks", pcrs),
        ("Approval", "Id PcrId Role SignedByName Signed SignedDate SentDate ReceivedDate SortOrder RequiresMultipleDates", approvals),
        ("PurchaseOrder", "Id PrId PoNo Vendor Value Status Date CombinedPrs Currency BaseAmount Freight OtherCharges Discount VatType", pos),
        ("PurchaseOrderItem", "Id PoId ItemName Quantity Unit UnitPrice Discount LineTotal SortOrder", po_items),
        ("CustomFieldValue", "Id PrId ColumnId Value", custom_values),
    ]
    total = 0
    for table, cols, rows in batches:
        db.executemany(insert(table, cols.split()), rows)
        total += len(rows)
        print(f"  {table:<24}{len(rows):>10,}")

    db.commit()
    db.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    db.close()
    return total


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--prs", type=int, default=20000)
    p.add_argument("--out", required=True, help="directory to write procure_tracker.db3 into")
    p.add_argument("--items-per-pr", type=int, default=3)
    p.add_argument("--rfqs-per-pr", type=int, default=5)
    p.add_argument("--items-per-rfq", type=int, default=3)
    p.add_argument("--pos-per-pr", type=int, default=2)
    p.add_argument("--items-per-po", type=int, default=3)
    p.add_argument("--pcr-share", type=float, default=0.8)
    p.add_argument("--approvals-per-pcr", type=int, default=4)
    p.add_argument("--custom-fields", type=int, default=5)
    p.add_argument("--seed", type=int, default=42, help="fixed so runs are comparable")
    args = p.parse_args()

    os.makedirs(args.out, exist_ok=True)
    path = os.path.join(args.out, "procure_tracker.db3")

    print(f"Generating {args.prs:,} PRs into {path}")
    total = generate(path, args.prs, args.items_per_pr, args.rfqs_per_pr, args.items_per_rfq,
                     args.pos_per_pr, args.items_per_po, args.pcr_share, args.approvals_per_pcr,
                     args.custom_fields, args.seed)
    size = os.path.getsize(path) / 1048576
    print(f"\n  {'TOTAL':<24}{total:>10,} rows   {size:.0f} MB")
    print(f"\nRun against it with:  PROCURE_DB_DIR={args.out}  Procure.exe")


if __name__ == "__main__":
    main()
