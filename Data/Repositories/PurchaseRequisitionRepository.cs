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
        private readonly ICustomColumnRepository _customColumnRepo;

        public PurchaseRequisitionRepository(
            SqliteDatabase db,
            ICustomColumnRepository customColumnRepo)
        {
            _db = db;
            _customColumnRepo = customColumnRepo;
        }

        public async Task<List<PurchaseRequisition>> GetAllAsync()
        {
            // ponytail-temp
            var probe = TimingProbe.Start();
            await _db.InitializeAsync();
            probe.Mark("InitializeAsync"); // ponytail-temp
            var prs = new List<PurchaseRequisition>();

            using var connection = _db.CreateConnection();
            await connection.OpenAsync();
            probe.Mark("CreateConnection+Open"); // ponytail-temp

            // 1. Load PRs
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, PrNo, Description, Requestor, Plant, Priority, Status, Notes, CreatedAt, UpdatedAt, ParentPrId, ConsolidatedFrom, PrType
FROM PurchaseRequisition
ORDER BY CreatedAt DESC;";

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    prs.Add(new PurchaseRequisition
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
                    });
                }
            }
            probe.Mark("PRs"); // ponytail-temp

            // 2. Load RFQs
            var rfqDict = new Dictionary<Guid, List<RequestForQuotation>>();
            var rfqById = new Dictionary<Guid, RequestForQuotation>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, PrId, RfqNo, Vendor, Status, SentDate, QuoteReceivedDate, QuoteAmount, PaymentTerms, VatType, Freight, OtherCharges, Incoterms, DeliveryLeadTime, Currency, SharedPrs, Warranty, TechnicalApproval, Discount
FROM RequestForQuotation;";

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
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
            probe.Mark("RFQs"); // ponytail-temp

            // 2b. Load RfqItems
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, RfqId, PrItemId, ItemName, Quantity, Unit, IsQuoted, QuotedUnitPrice, Notes, SortOrder, Discount, LastPrice
FROM RfqItem
ORDER BY SortOrder ASC;";

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
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
            probe.Mark("RfqItems"); // ponytail-temp

            // 3. Load PCRs and Approvals
            var pcrDict = new Dictionary<Guid, PriceComparisonRequest>();
            var pcrById = new Dictionary<Guid, PriceComparisonRequest>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT Id, PrId, PcrNo, CreatedAt, Remarks FROM PriceComparisonRequest;";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
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
            probe.Mark("PCRs"); // ponytail-temp

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT Id, PcrId, Role, SignedByName, Signed, SignedDate, SentDate, ReceivedDate, SortOrder, RequiresMultipleDates FROM Approval ORDER BY SortOrder ASC;";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
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
            probe.Mark("Approvals"); // ponytail-temp

            // 4. Load POs
            var poDict = new Dictionary<Guid, List<PurchaseOrder>>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, PrId, PoNo, Vendor, LinkedRfqId, Value, Status, Date, CombinedPrs, Currency, BaseAmount, Freight, OtherCharges, Discount, VatType
FROM PurchaseOrder;";

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
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
            probe.Mark("POs"); // ponytail-temp

            // 4b. Load PO Items
            var poItemDict = new Dictionary<Guid, List<PurchaseOrderItem>>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, PoId, PrItemId, RfqItemId, ItemName, Quantity, Unit, UnitPrice, Discount, LineTotal, SortOrder
FROM PurchaseOrderItem
ORDER BY SortOrder ASC;";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
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
            probe.Mark("POItems"); // ponytail-temp

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
            probe.Mark("AttachItemsToPOs"); // ponytail-temp

            // 5. Load Custom Values in a single bulk query with Column Definitions joined
            var customDict = new Dictionary<Guid, List<CustomFieldValue>>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT v.Id, v.PrId, v.ColumnId, v.Value, d.Name, d.DataType, d.SelectOptions
FROM CustomFieldValue v
LEFT JOIN CustomColumnDefinition d ON v.ColumnId = d.Id
ORDER BY d.SortOrder ASC, d.Name ASC;";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
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
            probe.Mark("CustomFieldValues"); // ponytail-temp

            // 6. Load PrItems
            var itemDict = new Dictionary<Guid, List<PrItem>>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, PrId, ItemName, Quantity, Unit, EstimatedUnitPrice, Notes, SortOrder
FROM PrItem
ORDER BY SortOrder ASC;";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
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
            probe.Mark("PrItems"); // ponytail-temp

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
            probe.Mark("NavProps+Fulfillments"); // ponytail-temp
            probe.Flush(); // ponytail-temp

            return prs;
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
            await _db.InitializeAsync();

            using var connection = _db.CreateConnection();
            await connection.OpenAsync();

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

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
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
            await _db.InitializeAsync();
            var prs = new List<PurchaseRequisition>();

            using var connection = _db.CreateConnection();
            await connection.OpenAsync();

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

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    prs.Add(new PurchaseRequisition
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
                    });
                }
            }

            if (prs.Count == 0) return prs;

            var prIds = prs.Select(p => p.Id).ToList();
            var prIdStrings = string.Join(",", prIds.Select(id => $"'{id}'"));

            // Load RFQs for these few PRs
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $@"
SELECT Id, PrId, RfqNo, Vendor, Status, SentDate, QuoteReceivedDate, QuoteAmount, PaymentTerms, VatType, Freight, OtherCharges, Incoterms, DeliveryLeadTime, Currency, SharedPrs
FROM RequestForQuotation
WHERE PrId IN ({prIdStrings});";

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var prId = Guid.Parse(reader.GetString(1));
                    var pr = prs.FirstOrDefault(p => p.Id == prId);
                    if (pr != null)
                    {
                        pr.Rfqs.Add(new RequestForQuotation
                        {
                            Id = Guid.Parse(reader.GetString(0)),
                            PrId = prId,
                            RfqNo = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                            Vendor = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                            Status = reader.IsDBNull(4) ? RfqStatus.Sent : reader.GetString(4),
                            SentDate = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5)),
                            QuoteReceivedDate = reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6)),
                            QuoteAmount = reader.IsDBNull(7) ? null : Convert.ToDecimal(reader.GetValue(7)),
                            PaymentTerms = reader.IsDBNull(8) ? "30 Days Net" : reader.GetString(8),
                            VatType = reader.IsDBNull(9) ? "5%" : reader.GetString(9),
                            Freight = reader.IsDBNull(10) ? null : Convert.ToDecimal(reader.GetValue(10)),
                            OtherCharges = reader.IsDBNull(11) ? null : Convert.ToDecimal(reader.GetValue(11)),
                            Incoterms = reader.IsDBNull(12) ? "DDP" : reader.GetString(12),
                            DeliveryLeadTime = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
                            Currency = reader.IsDBNull(14) ? "AED" : reader.GetString(14),
                            SharedPrs = reader.IsDBNull(15) ? string.Empty : reader.GetString(15)
                        });
                    }
                }
            }

            // Load PCRs and Approvals for these PRs
            var pcrDict = new Dictionary<Guid, PriceComparisonRequest>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $@"
SELECT Id, PrId, PcrNo, CreatedAt
FROM PriceComparisonRequest
WHERE PrId IN ({prIdStrings});";

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var prId = Guid.Parse(reader.GetString(1));
                    var pcr = new PriceComparisonRequest
                    {
                        Id = Guid.Parse(reader.GetString(0)),
                        PrId = prId,
                        PcrNo = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        CreatedAt = DateTime.Parse(reader.GetString(3))
                    };
                    pcrDict[pcr.Id] = pcr;
                    var pr = prs.FirstOrDefault(p => p.Id == prId);
                    if (pr != null) pr.Pcr = pcr;
                }
            }

            if (pcrDict.Count > 0)
            {
                var pcrIdStrings = string.Join(",", pcrDict.Keys.Select(k => $"'{k}'"));
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = $@"
SELECT Id, PcrId, Role, SignedByName, Signed, SignedDate, SentDate, ReceivedDate, SortOrder, RequiresMultipleDates
FROM Approval
WHERE PcrId IN ({pcrIdStrings})
ORDER BY SortOrder ASC;";

                    using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var pcrId = Guid.Parse(reader.GetString(1));
                        if (pcrDict.TryGetValue(pcrId, out var pcr))
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
                                RequiresMultipleDates = reader.IsDBNull(9) || reader.GetInt32(9) != 0,
                                IsIncluded = true
                            });
                        }
                    }
                }
            }

            // Load POs for these PRs
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $@"
SELECT Id, PrId, PoNo, Vendor, LinkedRfqId, Value, Status, Date, CombinedPrs
FROM PurchaseOrder
WHERE PrId IN ({prIdStrings});";

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var prId = Guid.Parse(reader.GetString(1));
                    var pr = prs.FirstOrDefault(p => p.Id == prId);
                    if (pr != null)
                    {
                        pr.Pos.Add(new PurchaseOrder
                        {
                            Id = Guid.Parse(reader.GetString(0)),
                            PrId = prId,
                            PoNo = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                            Vendor = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                            LinkedRfqId = reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                            Value = reader.IsDBNull(5) ? 0 : Convert.ToDecimal(reader.GetValue(5)),
                            Status = reader.IsDBNull(6) ? PoStatus.Raised : reader.GetString(6),
                            Date = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7)),
                            CombinedPrs = reader.IsDBNull(8) ? string.Empty : reader.GetString(8)
                        });
                    }
                }
            }

            foreach (var pr in prs)
            {
                pr.NotifyHierarchyChanged();
            }

            return prs;
        }

        // ponytail: TEMPORARY diagnostic scaffolding. Measures where the ~1.1s cold cost of
        // GetAllAsync actually lands. DELETE this whole region plus every `probe.` line in
        // GetAllAsync (grep for "ponytail-temp") once the numbers are captured.
        #region ponytail-temp timing probe
        private static int _getAllCallCount;

        private sealed class TimingProbe
        {
            private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
            private readonly List<(string Label, long Ticks)> _marks = new(16);
            private readonly int _call = System.Threading.Interlocked.Increment(ref _getAllCallCount);

            public static TimingProbe Start() => new();

            // Record only — no formatting, no I/O — so the probe does not distort what it measures.
            public void Mark(string label) => _marks.Add((label, _sw.ElapsedTicks));

            public void Flush()
            {
                try
                {
                    var sb = new System.Text.StringBuilder();
                    var stamp = _call == 1 ? "COLD" : "warm";
                    sb.AppendLine($"=== GetAllAsync call #{_call} ({stamp}) {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===");
                    long prev = 0;
                    foreach (var (label, ticks) in _marks)
                    {
                        var step = (ticks - prev) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        var total = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        sb.AppendLine($"  {label,-24} step {step,9:F1} ms   total {total,9:F1} ms");
                        prev = ticks;
                    }

                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(DatabaseConstants.DatabaseDirectory, "getall-timing.log"),
                        sb.ToString());
                }
                catch
                {
                    // Diagnostics must never break the read path.
                }
            }
        }
        #endregion
    }
}
