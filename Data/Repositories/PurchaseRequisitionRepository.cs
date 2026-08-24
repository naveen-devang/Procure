using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Procure.Models;

namespace Procure.Data.Repositories
{
    // Read paths. Writes live in PurchaseRequisitionRepository.Writes.cs; multi-PR
    // restructuring lives in PurchaseRequisitionRepository.Restructure.cs.
    public partial class PurchaseRequisitionRepository : IPurchaseRequisitionRepository
    {
        private readonly SqliteDatabase _db;

        public PurchaseRequisitionRepository(SqliteDatabase db)
        {
            _db = db;
        }

        public async Task<List<PurchaseRequisition>> GetAllAsync()
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            var prs = new List<PurchaseRequisition>();

            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            // 1. Load PRs
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, PrNo, Description, Requestor, Plant, Priority, Status, Notes, CreatedAt, UpdatedAt, ParentPrId, ConsolidatedFrom, PrType
FROM PurchaseRequisition
ORDER BY CreatedAt DESC;";

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    prs.Add(ReadPr(reader));
                }
            }

            await LoadChildrenAsync(connection, prs, scoped: false).ConfigureAwait(false);
            return prs;
        }

        /// <summary>
        /// Loads every child collection for <paramref name="prs"/> and attaches it. Eight flat queries,
        /// no N+1. When <paramref name="scoped"/> is true each one is narrowed to the PRs actually
        /// passed in - that is what makes a page cost the same whether the table holds 50 rows or
        /// 20,000. GetAllAsync passes false and reads the lot, which only the CSV export still wants.
        ///
        /// NotifyHierarchyChanged is intentionally NOT called here: it must run on the UI thread after
        /// binding, or it fires PropertyChanged for every card from a background thread.
        /// </summary>
        private static async Task LoadChildrenAsync(SqliteConnection connection, List<PurchaseRequisition> prs, bool scoped)
        {
            if (prs.Count == 0) return;
            var prIds = scoped ? prs.Select(p => p.Id).ToList() : null;

            // 2. Load RFQs
            var rfqDict = new Dictionary<Guid, List<RequestForQuotation>>();
            var rfqById = new Dictionary<Guid, RequestForQuotation>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, PrId, RfqNo, Vendor, Status, SentDate, QuoteReceivedDate, QuoteAmount, PaymentTerms, VatType, Freight, OtherCharges, Incoterms, DeliveryLeadTime, Currency, SharedPrs, Warranty, TechnicalApproval, Discount
FROM RequestForQuotation" + Scope(cmd, "PrId", "@Pr", prIds) + ";";

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var prId = Guid.Parse(reader.GetString(1));
                    if (!rfqDict.TryGetValue(prId, out var rfqList))
                    {
                        rfqList = new List<RequestForQuotation>();
                        rfqDict[prId] = rfqList;
                    }

                    var rfq = new RequestForQuotation
                    {
                        Id = Guid.Parse(reader.GetString(0)),
                        PrId = prId,
                        RfqNo = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        Vendor = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                        Status = reader.IsDBNull(4) ? RfqStatus.Sent : reader.GetString(4),
                        SentDate = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5)),
                        QuoteReceivedDate = reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6)),
                        QuoteAmount = reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                        PaymentTerms = reader.IsDBNull(8) ? "30 Days Net" : reader.GetString(8),
                        VatType = reader.IsDBNull(9) ? "5%" : reader.GetString(9),
                        Freight = reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                        OtherCharges = reader.IsDBNull(11) ? null : reader.GetDecimal(11),
                        Incoterms = reader.IsDBNull(12) ? "DDP" : reader.GetString(12),
                        DeliveryLeadTime = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
                        Currency = reader.IsDBNull(14) ? "AED" : reader.GetString(14),
                        SharedPrs = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
                        Warranty = reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
                        TechnicalApproval = reader.IsDBNull(17) ? string.Empty : reader.GetString(17),
                        Discount = reader.IsDBNull(18) ? null : reader.GetDecimal(18)
                    };

                    rfqList.Add(rfq);
                    rfqById[rfq.Id] = rfq;
                }
            }

            // 2b. Load RfqItems
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, RfqId, PrItemId, ItemName, Quantity, Unit, IsQuoted, QuotedUnitPrice, Notes, SortOrder, Discount, LastPrice
FROM RfqItem" + Scope(cmd, "RfqId", "@Rfq", scoped ? rfqById.Keys : null) + @"
ORDER BY SortOrder ASC;";

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var rfqId = Guid.Parse(reader.GetString(1));
                    if (rfqById.TryGetValue(rfqId, out var parentRfq))
                    {
                        parentRfq.Items.Add(new RfqItem
                        {
                            Id = Guid.Parse(reader.GetString(0)),
                            RfqId = rfqId,
                            PrItemId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                            ItemName = reader.GetString(3),
                            Quantity = (decimal)reader.GetDouble(4),
                            Unit = reader.IsDBNull(5) ? "pcs" : reader.GetString(5),
                            IsQuoted = reader.GetInt32(6) != 0,
                            QuotedUnitPrice = reader.IsDBNull(7) ? null : (decimal)reader.GetDouble(7),
                            Notes = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                            SortOrder = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                            Discount = reader.IsDBNull(10) ? null : (decimal)reader.GetDouble(10),
                            LastPrice = reader.IsDBNull(11) ? null : (decimal)reader.GetDouble(11)
                        });
                    }
                }
            }

            // 3. Load PCRs and Approvals
            var pcrDict = new Dictionary<Guid, PriceComparisonRequest>();
            var pcrById = new Dictionary<Guid, PriceComparisonRequest>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT Id, PrId, PcrNo, CreatedAt, Remarks FROM PriceComparisonRequest" + Scope(cmd, "PrId", "@Pr", prIds) + ";";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var pcr = new PriceComparisonRequest
                    {
                        Id = Guid.Parse(reader.GetString(0)),
                        PrId = Guid.Parse(reader.GetString(1)),
                        PcrNo = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        CreatedAt = DateTime.Parse(reader.GetString(3)),
                        Remarks = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
                    };
                    pcrDict[pcr.PrId] = pcr;
                    pcrById[pcr.Id] = pcr;
                }
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT Id, PcrId, Role, SignedByName, Signed, SignedDate, SentDate, ReceivedDate, SortOrder, RequiresMultipleDates FROM Approval"
                                + Scope(cmd, "PcrId", "@Pcr", scoped ? pcrById.Keys : null) + " ORDER BY SortOrder ASC;";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var pcrId = Guid.Parse(reader.GetString(1));
                    if (pcrById.TryGetValue(pcrId, out var pcr))
                    {
                        pcr.Approvals.Add(new Approval
                        {
                            Id = Guid.Parse(reader.GetString(0)),
                            PcrId = pcrId,
                            Role = reader.GetString(2),
                            SignedByName = reader.IsDBNull(3) ? null : reader.GetString(3),
                            Signed = reader.GetInt32(4) != 0,
                            SignedDate = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5)),
                            SentDate = reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6)),
                            ReceivedDate = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7)),
                            SortOrder = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                            RequiresMultipleDates = reader.IsDBNull(9) || reader.GetInt32(9) != 0
                        });
                    }
                }
            }

            // 4. Load POs
            var poDict = new Dictionary<Guid, List<PurchaseOrder>>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, PrId, PoNo, Vendor, LinkedRfqId, Value, Status, Date, CombinedPrs, Currency, BaseAmount, Freight, OtherCharges, Discount, VatType
FROM PurchaseOrder" + Scope(cmd, "PrId", "@Pr", prIds) + ";";

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var prId = Guid.Parse(reader.GetString(1));
                    if (!poDict.TryGetValue(prId, out var poList))
                    {
                        poList = new List<PurchaseOrder>();
                        poDict[prId] = poList;
                    }

                    poList.Add(new PurchaseOrder
                    {
                        Id = Guid.Parse(reader.GetString(0)),
                        PrId = prId,
                        PoNo = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        Vendor = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                        LinkedRfqId = reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                        Value = reader.IsDBNull(5) ? 0 : reader.GetDecimal(5),
                        Status = reader.IsDBNull(6) ? PoStatus.Raised : reader.GetString(6),
                        Date = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7)),
                        CombinedPrs = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                        Currency = (reader.FieldCount > 9 && !reader.IsDBNull(9)) ? reader.GetString(9) : "AED",
                        BaseAmount = (reader.FieldCount > 10 && !reader.IsDBNull(10)) ? (decimal?)reader.GetDouble(10) : null,
                        Freight = (reader.FieldCount > 11 && !reader.IsDBNull(11)) ? (decimal?)reader.GetDouble(11) : null,
                        OtherCharges = (reader.FieldCount > 12 && !reader.IsDBNull(12)) ? (decimal?)reader.GetDouble(12) : null,
                        Discount = (reader.FieldCount > 13 && !reader.IsDBNull(13)) ? (decimal?)reader.GetDouble(13) : null,
                        VatType = (reader.FieldCount > 14 && !reader.IsDBNull(14)) ? reader.GetString(14) : "5%"
                    });
                }
            }

            // 4b. Load PO Items
            var poItemDict = new Dictionary<Guid, List<PurchaseOrderItem>>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, PoId, PrItemId, RfqItemId, ItemName, Quantity, Unit, UnitPrice, Discount, LineTotal, SortOrder
FROM PurchaseOrderItem" + Scope(cmd, "PoId", "@Po", scoped ? poDict.Values.SelectMany(l => l).Select(o => o.Id).ToList() : null) + @"
ORDER BY SortOrder ASC;";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var poId = Guid.Parse(reader.GetString(1));
                    if (!poItemDict.TryGetValue(poId, out var piList))
                    {
                        piList = new List<PurchaseOrderItem>();
                        poItemDict[poId] = piList;
                    }

                    piList.Add(new PurchaseOrderItem
                    {
                        Id = Guid.Parse(reader.GetString(0)),
                        PoId = poId,
                        PrItemId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                        RfqItemId = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                        ItemName = reader.GetString(4),
                        Quantity = (decimal)reader.GetDouble(5),
                        Unit = reader.IsDBNull(6) ? "pcs" : reader.GetString(6),
                        UnitPrice = reader.IsDBNull(7) ? null : (decimal)reader.GetDouble(7),
                        Discount = reader.IsDBNull(8) ? null : (decimal)reader.GetDouble(8),
                        SortOrder = reader.IsDBNull(10) ? 0 : reader.GetInt32(10)
                    });
                }
            }

            // Attach items to POs
            foreach (var poList in poDict.Values)
            {
                foreach (var po in poList)
                {
                    if (poItemDict.TryGetValue(po.Id, out var items))
                    {
                        po.Items = new ObservableCollection<PurchaseOrderItem>(items);
                    }
                }
            }

            // 5. Load Custom Values in a single bulk query with Column Definitions joined
            var customDict = new Dictionary<Guid, List<CustomFieldValue>>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT v.Id, v.PrId, v.ColumnId, v.Value, d.Name, d.DataType, d.SelectOptions
FROM CustomFieldValue v
LEFT JOIN CustomColumnDefinition d ON v.ColumnId = d.Id" + Scope(cmd, "v.PrId", "@Pr", prIds) + @"
ORDER BY d.SortOrder ASC, d.Name ASC;";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var prId = Guid.Parse(reader.GetString(1));
                    if (!customDict.TryGetValue(prId, out var valList))
                    {
                        valList = new List<CustomFieldValue>();
                        customDict[prId] = valList;
                    }

                    valList.Add(new CustomFieldValue
                    {
                        Id = Guid.Parse(reader.GetString(0)),
                        PrId = prId,
                        ColumnId = Guid.Parse(reader.GetString(2)),
                        Value = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                        ColumnName = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                        ColumnDataType = reader.IsDBNull(5) ? CustomFieldDataType.Text : reader.GetString(5),
                        SelectOptions = reader.IsDBNull(6) ? null : reader.GetString(6)
                    });
                }
            }

            // 6. Load PrItems
            var itemDict = new Dictionary<Guid, List<PrItem>>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, PrId, ItemName, Quantity, Unit, EstimatedUnitPrice, Notes, SortOrder
FROM PrItem" + Scope(cmd, "PrId", "@Pr", prIds) + @"
ORDER BY SortOrder ASC;";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var prId = Guid.Parse(reader.GetString(1));
                    if (!itemDict.TryGetValue(prId, out var itemList))
                    {
                        itemList = new List<PrItem>();
                        itemDict[prId] = itemList;
                    }

                    itemList.Add(new PrItem
                    {
                        Id = Guid.Parse(reader.GetString(0)),
                        PrId = prId,
                        ItemName = reader.GetString(2),
                        Quantity = (decimal)reader.GetDouble(3),
                        Unit = reader.IsDBNull(4) ? "pcs" : reader.GetString(4),
                        EstimatedUnitPrice = reader.IsDBNull(5) ? null : (decimal)reader.GetDouble(5),
                        Notes = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                        SortOrder = reader.IsDBNull(7) ? 0 : reader.GetInt32(7)
                    });
                }
            }

            // 7. Populate navigation properties in-memory
            //    Note: NotifyHierarchyChanged is intentionally NOT called here.
            //    It must be called by the ViewModel on the UI thread after binding,
            //    to avoid firing multiple PropertyChanged events on a background thread.
            foreach (var pr in prs)
            {
                if (itemDict.TryGetValue(pr.Id, out var itemList))
                    pr.Items = new ObservableCollection<PrItem>(itemList);

                if (rfqDict.TryGetValue(pr.Id, out var rfqList))
                    pr.Rfqs = new ObservableCollection<RequestForQuotation>(rfqList);

                if (pcrDict.TryGetValue(pr.Id, out var pcr))
                {
                    pcr.EnsureDefaultApprovals();
                    pr.Pcr = pcr;
                }

                if (poDict.TryGetValue(pr.Id, out var poList))
                    pr.Pos = new ObservableCollection<PurchaseOrder>(poList);

                if (customDict.TryGetValue(pr.Id, out var customVals))
                    pr.CustomValues = new ObservableCollection<CustomFieldValue>(customVals);

                pr.CalculateItemFulfillments();
            }

        }

        /// <summary>
        /// Reads one page of the board: the rows that match <paramref name="query"/>, their full child
        /// graph, and the unpaged match count for the footer. Every filter the board offers is expressed
        /// here in SQL, so the page costs the same whether the table holds 50 rows or 20,000.
        /// ponytail: OFFSET paging - page N scans N*Take index entries. Measured 4ms for a page of 50 at
        /// 20,000 PRs, so the simpler form wins; switch to a keyset cursor on (CreatedAt, Id) if the
        /// table ever grows far past that.
        /// </summary>
        public async Task<PrPage> GetPageAsync(PrQuery query)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            int total;
            using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM PurchaseRequisition" + BuildWhere(countCmd, query) + ";";
                total = Convert.ToInt32(await countCmd.ExecuteScalarAsync().ConfigureAwait(false));
            }

            var prs = new List<PurchaseRequisition>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, PrNo, Description, Requestor, Plant, Priority, Status, Notes, CreatedAt, UpdatedAt, ParentPrId, ConsolidatedFrom, PrType
FROM PurchaseRequisition" + BuildWhere(cmd, query) + @"
ORDER BY CreatedAt DESC
LIMIT @Take OFFSET @Skip;";
                cmd.Parameters.AddWithValue("@Take", query.Take);
                cmd.Parameters.AddWithValue("@Skip", query.Skip);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false)) prs.Add(ReadPr(reader));
            }

            await LoadChildrenAsync(connection, prs, scoped: true).ConfigureAwait(false);
            return new PrPage(prs, total);
        }

        /// <summary>Reads specific PRs with their full child graph. The board uses it to keep selected
        /// PRs loaded once they fall outside the visible window, so a selection survives scrolling and
        /// filtering and batch operations always act on all of it.</summary>
        public async Task<List<PurchaseRequisition>> GetByIdsAsync(IReadOnlyCollection<Guid> ids)
        {
            if (ids.Count == 0) return new List<PurchaseRequisition>();

            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            var prs = new List<PurchaseRequisition>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, PrNo, Description, Requestor, Plant, Priority, Status, Notes, CreatedAt, UpdatedAt, ParentPrId, ConsolidatedFrom, PrType
FROM PurchaseRequisition WHERE Id IN (" + BindIdList(cmd, "@Id", ids) + @")
ORDER BY CreatedAt DESC;";

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false)) prs.Add(ReadPr(reader));
            }

            await LoadChildrenAsync(connection, prs, scoped: true).ConfigureAwait(false);
            return prs;
        }

        /// <summary>
        /// The source PRs behind a merged master: those pointing at it through ParentPrId, plus any whose
        /// number appears in <paramref name="fallbackPrNos"/> (parsed from the master's ConsolidatedFrom).
        /// This has to reach the database rather than the loaded window - merged children carry status
        /// 'Merged', which the board hides by default, so they are almost never on screen.
        /// </summary>
        public async Task<List<PurchaseRequisition>> GetChildPrsAsync(Guid masterPrId, IReadOnlyCollection<string> fallbackPrNos)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            var prs = new List<PurchaseRequisition>();
            using (var cmd = connection.CreateCommand())
            {
                var byNumber = string.Empty;
                if (fallbackPrNos.Count > 0)
                {
                    var names = new List<string>(fallbackPrNos.Count);
                    var i = 0;
                    foreach (var no in fallbackPrNos)
                    {
                        var name = "@No" + i++;
                        names.Add(name);
                        cmd.Parameters.AddWithValue(name, no);
                    }

                    // Mirrors the two forms the numbers are matched in: as written, and with the "PR-"
                    // prefix stripped.
                    var list = string.Join(",", names);
                    byNumber = $" OR lower(PrNo) IN ({list}) OR lower(replace(PrNo, 'PR-', '')) IN ({list})";
                }

                cmd.CommandText = @"
SELECT Id, PrNo, Description, Requestor, Plant, Priority, Status, Notes, CreatedAt, UpdatedAt, ParentPrId, ConsolidatedFrom, PrType
FROM PurchaseRequisition
WHERE Id <> @MasterId AND (ParentPrId = @MasterId" + byNumber + @")
ORDER BY CreatedAt DESC;";
                cmd.Parameters.AddWithValue("@MasterId", masterPrId.ToString());

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false)) prs.Add(ReadPr(reader));
            }

            await LoadChildrenAsync(connection, prs, scoped: true).ConfigureAwait(false);
            return prs;
        }

        // PurchaseRequisition.IsOverdue in SQL: not in a terminal status, and older than the threshold
        // for its priority. CreatedAt is written with ToString("o"), so substr(...,1,10) is its date.
        private const string SqlOverdue = @"Status NOT IN ('Delivered', 'Closed', 'Cancelled', 'Merged')
      AND ((Priority = 'Urgent' AND substr(CreatedAt, 1, 10) <= @UrgentCutoff)
        OR (Priority <> 'Urgent' AND substr(CreatedAt, 1, 10) <= @NormalCutoff))";

        // PriceComparisonRequest.IsFullyApproved is "has approvals and every one IsReceived", so pending
        // is the negation: no approvals at all, or any one not received. Approval.IsReceived is
        // ReceivedDate set, or Signed with a SignedDate. Empty strings are treated as unset, matching
        // GetDashboardAggregatesAsync so the board and the dashboard never disagree.
        private const string SqlPcrPending = @"NOT EXISTS (SELECT 1 FROM Approval a WHERE a.PcrId = p.Id)
       OR EXISTS (SELECT 1 FROM Approval a WHERE a.PcrId = p.Id
                  AND (a.ReceivedDate IS NULL OR a.ReceivedDate = '')
                  AND (a.Signed = 0 OR a.SignedDate IS NULL OR a.SignedDate = ''))";

        private static void AddOverdueCutoffs(SqliteCommand cmd, int normalOverdueDays, int urgentOverdueDays)
        {
            cmd.Parameters.AddWithValue("@UrgentCutoff", DateTime.Today.AddDays(-urgentOverdueDays).ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("@NormalCutoff", DateTime.Today.AddDays(-normalOverdueDays).ToString("yyyy-MM-dd"));
        }

        /// <summary>Builds the board's filter clause and binds its parameters. Mirrors what
        /// PrListPageModel.ApplyFilters used to do in memory, including the rule that merged PRs are
        /// hidden by default but reappear once you search or pick a status.</summary>
        private static string BuildWhere(SqliteCommand cmd, PrQuery query)
        {
            var clauses = new List<string>();
            var hasSearch = !string.IsNullOrWhiteSpace(query.Search);
            var hasStatus = !string.IsNullOrWhiteSpace(query.Status) && query.Status != "All";

            if (hasSearch)
            {
                clauses.Add("SearchBlob LIKE @Search");
                cmd.Parameters.AddWithValue("@Search", "%" + query.Search!.Trim().ToLowerInvariant() + "%");
            }

            if (hasStatus)
            {
                clauses.Add("Status = @Status COLLATE NOCASE");
                cmd.Parameters.AddWithValue("@Status", query.Status);
            }
            else if (!hasSearch)
            {
                clauses.Add("Status <> @Merged");
                cmd.Parameters.AddWithValue("@Merged", ProcurementStatus.Merged);
            }

            if (query.OverdueOnly)
            {
                clauses.Add($"({SqlOverdue})");
                AddOverdueCutoffs(cmd, query.NormalOverdueDays, query.UrgentOverdueDays);
            }

            if (query.PcrPendingOnly)
                clauses.Add($"EXISTS (SELECT 1 FROM PriceComparisonRequest p WHERE p.PrId = PurchaseRequisition.Id AND ({SqlPcrPending}))");

            if (query.UrgentOnly)
                clauses.Add("Priority = 'Urgent' COLLATE NOCASE");

            return clauses.Count == 0 ? string.Empty : "\nWHERE " + string.Join("\n  AND ", clauses);
        }

        /// <summary>Materialises one PurchaseRequisition row. The column list appears once rather than
        /// in each of the three queries that read PR rows.</summary>
        private static PurchaseRequisition ReadPr(SqliteDataReader reader) => new()
        {
            Id = Guid.Parse(reader.GetString(0)),
            PrNo = reader.GetString(1),
            Description = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            Requestor = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            Plant = reader.IsDBNull(4) ? ProcurementPlant.RW01 : reader.GetString(4),
            Priority = reader.IsDBNull(5) ? ProcurementPriority.Normal : reader.GetString(5),
            Status = reader.IsDBNull(6) ? ProcurementStatus.PrRaised : reader.GetString(6),
            Notes = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            CreatedAt = DateTime.Parse(reader.GetString(8)),
            UpdatedAt = DateTime.Parse(reader.GetString(9)),
            ParentPrId = reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)),
            ConsolidatedFrom = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            PrType = reader.IsDBNull(12) ? ProcurementPrType.StoresAndSpares : reader.GetString(12)
        };

        /// <summary>Renders " WHERE col IN (...)" with every id bound as a parameter, or an empty string
        /// when <paramref name="ids"/> is null - which is how one query text serves both the paged read
        /// and the unbounded one.</summary>
        private static string Scope(SqliteCommand cmd, string column, string prefix, IReadOnlyCollection<Guid>? ids)
            => ids is null ? string.Empty : $" WHERE {column} IN ({BindIdList(cmd, prefix, ids)})";

        /// <summary>Binds <paramref name="ids"/> to <paramref name="cmd"/> as @prefix0, @prefix1... and
        /// returns the placeholder text for an IN (...) list. Interpolating the ids straight into the SQL
        /// gives every call a distinct statement text, so SQLite recompiles instead of reusing a cached
        /// plan - and it is an injection surface the moment a non-Guid ever flows through.
        /// ponytail: one parameter per id, so SQLite's 32766-parameter cap is the ceiling. Switch to a
        /// temp table of ids if a caller ever passes more than that.</summary>
        private static string BindIdList(SqliteCommand cmd, string prefix, IReadOnlyCollection<Guid> ids)
        {
            var names = new string[ids.Count];
            var i = 0;
            foreach (var id in ids)
            {
                names[i] = prefix + i;
                cmd.Parameters.AddWithValue(names[i], id.ToString());
                i++;
            }

            return string.Join(",", names);
        }

        public async Task<int> GetCountAsync()
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM PurchaseRequisition;";
            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return Convert.ToInt32(result);
        }


        public async Task<(int TotalPrs, decimal TotalPoValue, int PosRaised, int RfqsAwaitingQuote, int PcrsAwaitingSignature, int UrgentCount, int OverdueCount)> GetDashboardAggregatesAsync(int normalOverdueDays, int urgentOverdueDays)
        {
            await _db.InitializeAsync().ConfigureAwait(false);

            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            var urgentCutoffDate = DateTime.Today.AddDays(-urgentOverdueDays).ToString("yyyy-MM-dd");
            var normalCutoffDate = DateTime.Today.AddDays(-normalOverdueDays).ToString("yyyy-MM-dd");

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT 
    (SELECT COUNT(*) FROM PurchaseRequisition) AS TotalPrs,
    (SELECT COALESCE(SUM(Value), 0.0) FROM PurchaseOrder) AS TotalPoValue,
    (SELECT COUNT(*) FROM PurchaseOrder WHERE Status = 'Raised') AS PosRaised,
    (SELECT COUNT(*) FROM RequestForQuotation WHERE Status = 'Sent' AND (QuoteReceivedDate IS NULL OR QuoteReceivedDate = '')) AS RfqsAwaitingQuote,
    (SELECT COUNT(DISTINCT p.Id) FROM PriceComparisonRequest p LEFT JOIN Approval a ON a.PcrId = p.Id WHERE a.Id IS NULL OR ((a.ReceivedDate IS NULL OR a.ReceivedDate = '') AND (a.Signed = 0 OR a.SignedDate IS NULL OR a.SignedDate = ''))) AS PcrsAwaitingSignature,
    (SELECT COUNT(*) FROM PurchaseRequisition WHERE Priority = 'Urgent' AND Status NOT IN ('Delivered', 'Closed', 'Cancelled', 'Merged')) AS UrgentCount,
    (SELECT COUNT(*) FROM PurchaseRequisition 
     WHERE Status NOT IN ('Delivered', 'Closed', 'Cancelled', 'Merged') 
       AND (
           (Priority = 'Urgent' AND substr(CreatedAt, 1, 10) <= @UrgentCutoffDate) 
           OR (Priority != 'Urgent' AND substr(CreatedAt, 1, 10) <= @NormalCutoffDate)
       )
    ) AS OverdueCount;";

            cmd.Parameters.AddWithValue("@UrgentCutoffDate", urgentCutoffDate);
            cmd.Parameters.AddWithValue("@NormalCutoffDate", normalCutoffDate);

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (await reader.ReadAsync().ConfigureAwait(false))
            {
                int totalPrs = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                decimal totalPoValue = reader.IsDBNull(1) ? 0m : Convert.ToDecimal(reader.GetValue(1));
                int posRaised = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));
                int rfqsAwaiting = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3));
                int pcrsAwaiting = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4));
                int urgentCount = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5));
                int overdueCount = reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetValue(6));

                return (totalPrs, totalPoValue, posRaised, rfqsAwaiting, pcrsAwaiting, urgentCount, overdueCount);
            }

            return (0, 0m, 0, 0, 0, 0, 0);
        }

        public async Task<List<PurchaseRequisition>> GetNeedsAttentionPrsAsync(int normalOverdueDays, int urgentOverdueDays, int limit = 10)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            var prs = new List<PurchaseRequisition>();

            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            var urgentCutoffDate = DateTime.Today.AddDays(-urgentOverdueDays).ToString("yyyy-MM-dd");
            var normalCutoffDate = DateTime.Today.AddDays(-normalOverdueDays).ToString("yyyy-MM-dd");

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, PrNo, Description, Requestor, Plant, Priority, Status, Notes, CreatedAt, UpdatedAt, ParentPrId, ConsolidatedFrom, PrType
FROM PurchaseRequisition
WHERE Status NOT IN ('Delivered', 'Closed', 'Cancelled', 'Merged')
  AND (
      Priority = 'Urgent'
      OR (Priority = 'Urgent' AND substr(CreatedAt, 1, 10) <= @UrgentCutoffDate)
      OR (Priority != 'Urgent' AND substr(CreatedAt, 1, 10) <= @NormalCutoffDate)
      OR Id IN (SELECT PrId FROM PriceComparisonRequest pcr LEFT JOIN Approval a ON a.PcrId = pcr.Id WHERE a.Id IS NULL OR ((a.ReceivedDate IS NULL OR a.ReceivedDate = '') AND (a.Signed = 0 OR a.SignedDate IS NULL OR a.SignedDate = '')))
  )
ORDER BY (CASE WHEN Priority = 'Urgent' THEN 1 ELSE 0 END) DESC, CreatedAt ASC
LIMIT @Limit;";

                cmd.Parameters.AddWithValue("@UrgentCutoffDate", urgentCutoffDate);
                cmd.Parameters.AddWithValue("@NormalCutoffDate", normalCutoffDate);
                cmd.Parameters.AddWithValue("@Limit", limit);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    prs.Add(ReadPr(reader));
                }
            }

            // No child hydration: the only caller is the Dashboard's Needs Attention widget, whose
            // template binds scalar PR fields exclusively.
            return prs;
        }
    }
}
