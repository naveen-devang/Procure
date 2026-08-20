using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Procure.Models;

namespace Procure.Data.Repositories
{
    public class PurchaseRequisitionRepository : IPurchaseRequisitionRepository
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
            await _db.InitializeAsync();
            var prs = new List<PurchaseRequisition>();

            using var connection = _db.CreateConnection();
            await connection.OpenAsync();

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

            // 4b. Load PO Items
            var poItemDict = new Dictionary<Guid, List<PurchaseOrderItem>>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
SELECT Id, PoId, PrItemId, RfqItemId, ItemName, Quantity, Unit, UnitPrice, Discount, LineTotal
FROM PurchaseOrderItem;";
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
                        Discount = reader.IsDBNull(8) ? null : (decimal)reader.GetDouble(8)
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

        public async Task SaveAsync(PurchaseRequisition pr)
        {
            await _db.InitializeAsync();
            pr.UpdatedAt = DateTime.Now;

            using var connection = _db.CreateConnection();
            await connection.OpenAsync();
            using var tx = connection.BeginTransaction();

            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO PurchaseRequisition (Id, PrNo, Description, Requestor, Plant, Priority, Status, Notes, CreatedAt, UpdatedAt, ParentPrId, ConsolidatedFrom, PrType)
VALUES (@Id, @PrNo, @Description, @Requestor, @Plant, @Priority, @Status, @Notes, @CreatedAt, @UpdatedAt, @ParentPrId, @ConsolidatedFrom, @PrType)
ON CONFLICT(Id) DO UPDATE SET
    PrNo = excluded.PrNo,
    Description = excluded.Description,
    Requestor = excluded.Requestor,
    Plant = excluded.Plant,
    PrType = excluded.PrType,
    Priority = excluded.Priority,
    Status = excluded.Status,
    Notes = excluded.Notes,
    UpdatedAt = excluded.UpdatedAt,
    ParentPrId = excluded.ParentPrId,
    ConsolidatedFrom = excluded.ConsolidatedFrom;";

            cmd.Parameters.AddWithValue("@Id", pr.Id.ToString());
            cmd.Parameters.AddWithValue("@PrNo", pr.PrNo);
            cmd.Parameters.AddWithValue("@Description", pr.Description);
            cmd.Parameters.AddWithValue("@Requestor", pr.Requestor);
            cmd.Parameters.AddWithValue("@Plant", string.IsNullOrWhiteSpace(pr.Plant) ? ProcurementPlant.RW01 : pr.Plant);
            cmd.Parameters.AddWithValue("@PrType", string.IsNullOrWhiteSpace(pr.PrType) ? ProcurementPrType.StoresAndSpares : pr.PrType);
            cmd.Parameters.AddWithValue("@Priority", pr.Priority);
            cmd.Parameters.AddWithValue("@Status", pr.Status);
            cmd.Parameters.AddWithValue("@Notes", pr.Notes);
            cmd.Parameters.AddWithValue("@CreatedAt", pr.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("@UpdatedAt", pr.UpdatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("@ParentPrId", pr.ParentPrId.HasValue ? pr.ParentPrId.Value.ToString() : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@ConsolidatedFrom", pr.ConsolidatedFrom ?? string.Empty);

            await cmd.ExecuteNonQueryAsync();

            // Sync PrItems
            using (var deleteItemCmd = connection.CreateCommand())
            {
                deleteItemCmd.Transaction = tx;
                deleteItemCmd.CommandText = "DELETE FROM PrItem WHERE PrId = @PrId;";
                deleteItemCmd.Parameters.AddWithValue("@PrId", pr.Id.ToString());
                await deleteItemCmd.ExecuteNonQueryAsync();
            }

            if (pr.Items != null && pr.Items.Count > 0)
            {
                int sortOrder = 0;
                foreach (var item in pr.Items)
                {
                    if (string.IsNullOrWhiteSpace(item.ItemName)) continue;
                    item.PrId = pr.Id;
                    item.SortOrder = sortOrder++;

                    using var insItemCmd = connection.CreateCommand();
                    insItemCmd.Transaction = tx;
                    insItemCmd.CommandText = @"
INSERT INTO PrItem (Id, PrId, ItemName, Quantity, Unit, EstimatedUnitPrice, Notes, SortOrder)
VALUES (@Id, @PrId, @ItemName, @Quantity, @Unit, @EstimatedUnitPrice, @Notes, @SortOrder);";

                    insItemCmd.Parameters.AddWithValue("@Id", item.Id.ToString());
                    insItemCmd.Parameters.AddWithValue("@PrId", item.PrId.ToString());
                    insItemCmd.Parameters.AddWithValue("@ItemName", item.ItemName.Trim());
                    insItemCmd.Parameters.AddWithValue("@Quantity", (double)item.Quantity);
                    insItemCmd.Parameters.AddWithValue("@Unit", string.IsNullOrWhiteSpace(item.Unit) ? "pcs" : item.Unit.Trim());
                    insItemCmd.Parameters.AddWithValue("@EstimatedUnitPrice", item.EstimatedUnitPrice.HasValue ? (double)item.EstimatedUnitPrice.Value : (object)DBNull.Value);
                    insItemCmd.Parameters.AddWithValue("@Notes", item.Notes ?? string.Empty);
                    insItemCmd.Parameters.AddWithValue("@SortOrder", item.SortOrder);

                    await insItemCmd.ExecuteNonQueryAsync();
                }
            }

            await tx.CommitAsync();

            if (pr.CustomValues.Count > 0)
            {
                await _customColumnRepo.SaveValuesForPrAsync(pr.Id, pr.CustomValues);
            }
        }

        public async Task SaveBatchPrsAsync(List<PurchaseRequisition> prs)
        {
            if (prs == null || prs.Count == 0) return;

            await _db.InitializeAsync();
            using var connection = _db.CreateConnection();
            await connection.OpenAsync();
            using var tx = connection.BeginTransaction();

            foreach (var pr in prs)
            {
                pr.UpdatedAt = DateTime.Now;
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO PurchaseRequisition (Id, PrNo, Description, Requestor, Plant, Priority, Status, Notes, CreatedAt, UpdatedAt, ParentPrId, ConsolidatedFrom, PrType)
VALUES (@Id, @PrNo, @Description, @Requestor, @Plant, @Priority, @Status, @Notes, @CreatedAt, @UpdatedAt, @ParentPrId, @ConsolidatedFrom, @PrType)
ON CONFLICT(Id) DO UPDATE SET
    PrNo = excluded.PrNo,
    Description = excluded.Description,
    Requestor = excluded.Requestor,
    Plant = excluded.Plant,
    PrType = excluded.PrType,
    Priority = excluded.Priority,
    Status = excluded.Status,
    Notes = excluded.Notes,
    UpdatedAt = excluded.UpdatedAt,
    ParentPrId = excluded.ParentPrId,
    ConsolidatedFrom = excluded.ConsolidatedFrom;";

                cmd.Parameters.AddWithValue("@Id", pr.Id.ToString());
                cmd.Parameters.AddWithValue("@PrNo", pr.PrNo);
                cmd.Parameters.AddWithValue("@Description", pr.Description);
                cmd.Parameters.AddWithValue("@Requestor", pr.Requestor);
                cmd.Parameters.AddWithValue("@Plant", string.IsNullOrWhiteSpace(pr.Plant) ? ProcurementPlant.RW01 : pr.Plant);
                cmd.Parameters.AddWithValue("@PrType", string.IsNullOrWhiteSpace(pr.PrType) ? ProcurementPrType.StoresAndSpares : pr.PrType);
                cmd.Parameters.AddWithValue("@Priority", pr.Priority);
                cmd.Parameters.AddWithValue("@Status", pr.Status);
                cmd.Parameters.AddWithValue("@Notes", pr.Notes);
                cmd.Parameters.AddWithValue("@CreatedAt", pr.CreatedAt.ToString("o"));
                cmd.Parameters.AddWithValue("@UpdatedAt", pr.UpdatedAt.ToString("o"));
                cmd.Parameters.AddWithValue("@ParentPrId", pr.ParentPrId.HasValue ? pr.ParentPrId.Value.ToString() : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ConsolidatedFrom", pr.ConsolidatedFrom ?? string.Empty);

                await cmd.ExecuteNonQueryAsync();

                // Save PrItems
                using var delItemCmd = connection.CreateCommand();
                delItemCmd.Transaction = tx;
                delItemCmd.CommandText = "DELETE FROM PrItem WHERE PrId = @PrId;";
                delItemCmd.Parameters.AddWithValue("@PrId", pr.Id.ToString());
                await delItemCmd.ExecuteNonQueryAsync();

                if (pr.Items != null && pr.Items.Count > 0)
                {
                    int sortOrder = 0;
                    foreach (var item in pr.Items)
                    {
                        if (string.IsNullOrWhiteSpace(item.ItemName)) continue;
                        item.PrId = pr.Id;
                        item.SortOrder = sortOrder++;

                        using var insItemCmd = connection.CreateCommand();
                        insItemCmd.Transaction = tx;
                        insItemCmd.CommandText = @"
INSERT INTO PrItem (Id, PrId, ItemName, Quantity, Unit, EstimatedUnitPrice, Notes, SortOrder)
VALUES (@Id, @PrId, @ItemName, @Quantity, @Unit, @EstimatedUnitPrice, @Notes, @SortOrder);";

                        insItemCmd.Parameters.AddWithValue("@Id", item.Id.ToString());
                        insItemCmd.Parameters.AddWithValue("@PrId", item.PrId.ToString());
                        insItemCmd.Parameters.AddWithValue("@ItemName", item.ItemName.Trim());
                        insItemCmd.Parameters.AddWithValue("@Quantity", (double)item.Quantity);
                        insItemCmd.Parameters.AddWithValue("@Unit", string.IsNullOrWhiteSpace(item.Unit) ? "pcs" : item.Unit.Trim());
                        insItemCmd.Parameters.AddWithValue("@EstimatedUnitPrice", item.EstimatedUnitPrice.HasValue ? (double)item.EstimatedUnitPrice.Value : (object)DBNull.Value);
                        insItemCmd.Parameters.AddWithValue("@Notes", item.Notes ?? string.Empty);
                        insItemCmd.Parameters.AddWithValue("@SortOrder", item.SortOrder);

                        await insItemCmd.ExecuteNonQueryAsync();
                    }
                }
            }

            await tx.CommitAsync();

            // Save custom field values
            foreach (var pr in prs)
            {
                if (pr.CustomValues.Count > 0)
                {
                    await _customColumnRepo.SaveValuesForPrAsync(pr.Id, pr.CustomValues);
                }
            }
        }

        public async Task DeleteAsync(Guid id)
        {
            await _db.InitializeAsync();
            using var connection = _db.CreateConnection();
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM PurchaseRequisition WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Id", id.ToString());

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task SaveRfqAsync(RequestForQuotation rfq)
        {
            await _db.InitializeAsync();
            using var connection = _db.CreateConnection();
            await connection.OpenAsync();
            using var tx = connection.BeginTransaction();

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO RequestForQuotation (Id, PrId, RfqNo, Vendor, Status, SentDate, QuoteReceivedDate, QuoteAmount, PaymentTerms, VatType, Freight, OtherCharges, Discount, Incoterms, DeliveryLeadTime, Currency, SharedPrs, Warranty, TechnicalApproval)
VALUES (@Id, @PrId, @RfqNo, @Vendor, @Status, @SentDate, @QuoteReceivedDate, @QuoteAmount, @PaymentTerms, @VatType, @Freight, @OtherCharges, @Discount, @Incoterms, @DeliveryLeadTime, @Currency, @SharedPrs, @Warranty, @TechnicalApproval)
ON CONFLICT(Id) DO UPDATE SET
    RfqNo = excluded.RfqNo,
    Vendor = excluded.Vendor,
    Status = excluded.Status,
    SentDate = excluded.SentDate,
    QuoteReceivedDate = excluded.QuoteReceivedDate,
    QuoteAmount = excluded.QuoteAmount,
    PaymentTerms = excluded.PaymentTerms,
    VatType = excluded.VatType,
    Freight = excluded.Freight,
    OtherCharges = excluded.OtherCharges,
    Discount = excluded.Discount,
    Incoterms = excluded.Incoterms,
    DeliveryLeadTime = excluded.DeliveryLeadTime,
    Currency = excluded.Currency,
    SharedPrs = excluded.SharedPrs,
    Warranty = excluded.Warranty,
    TechnicalApproval = excluded.TechnicalApproval;";

                cmd.Parameters.AddWithValue("@Id", rfq.Id.ToString());
                cmd.Parameters.AddWithValue("@PrId", rfq.PrId.ToString());
                cmd.Parameters.AddWithValue("@RfqNo", rfq.RfqNo);
                cmd.Parameters.AddWithValue("@Vendor", rfq.Vendor);
                cmd.Parameters.AddWithValue("@Status", rfq.Status);
                cmd.Parameters.AddWithValue("@SentDate", rfq.SentDate.HasValue ? rfq.SentDate.Value.ToString("o") : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@QuoteReceivedDate", rfq.QuoteReceivedDate.HasValue ? rfq.QuoteReceivedDate.Value.ToString("o") : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@QuoteAmount", rfq.BaseAmount > 0 ? (object)rfq.BaseAmount : (rfq.QuoteAmount.HasValue ? rfq.QuoteAmount.Value : (object)DBNull.Value));
                cmd.Parameters.AddWithValue("@PaymentTerms", rfq.PaymentTerms ?? "30 Days Net");
                cmd.Parameters.AddWithValue("@VatType", rfq.VatType ?? "5%");
                cmd.Parameters.AddWithValue("@Freight", rfq.Freight.HasValue ? rfq.Freight.Value : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@OtherCharges", rfq.OtherCharges.HasValue ? rfq.OtherCharges.Value : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Discount", rfq.Discount.HasValue ? rfq.Discount.Value : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Incoterms", rfq.Incoterms ?? "DDP");
                cmd.Parameters.AddWithValue("@DeliveryLeadTime", rfq.DeliveryLeadTime ?? string.Empty);
                cmd.Parameters.AddWithValue("@Currency", rfq.Currency ?? "AED");
                cmd.Parameters.AddWithValue("@SharedPrs", rfq.SharedPrs ?? string.Empty);
                cmd.Parameters.AddWithValue("@Warranty", rfq.Warranty ?? string.Empty);
                cmd.Parameters.AddWithValue("@TechnicalApproval", rfq.TechnicalApproval ?? string.Empty);

                await cmd.ExecuteNonQueryAsync();
            }

            // Save RfqItems
            if (rfq.Items != null)
            {
                var activeItemIds = rfq.Items.Select(i => $"'{i.Id}'").ToList();
                if (activeItemIds.Count > 0)
                {
                    using var deleteCmd = connection.CreateCommand();
                    deleteCmd.Transaction = tx;
                    deleteCmd.CommandText = $"DELETE FROM RfqItem WHERE RfqId = '{rfq.Id}' AND Id NOT IN ({string.Join(",", activeItemIds)});";
                    await deleteCmd.ExecuteNonQueryAsync();
                }
                else
                {
                    using var deleteCmd = connection.CreateCommand();
                    deleteCmd.Transaction = tx;
                    deleteCmd.CommandText = $"DELETE FROM RfqItem WHERE RfqId = '{rfq.Id}';";
                    await deleteCmd.ExecuteNonQueryAsync();
                }

                int sortOrder = 0;
                foreach (var item in rfq.Items)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT INTO RfqItem (Id, RfqId, PrItemId, ItemName, Quantity, Unit, IsQuoted, QuotedUnitPrice, Discount, LastPrice, Notes, SortOrder)
VALUES (@Id, @RfqId, @PrItemId, @ItemName, @Quantity, @Unit, @IsQuoted, @QuotedUnitPrice, @Discount, @LastPrice, @Notes, @SortOrder)
ON CONFLICT(Id) DO UPDATE SET
    ItemName = excluded.ItemName,
    Quantity = excluded.Quantity,
    Unit = excluded.Unit,
    IsQuoted = excluded.IsQuoted,
    QuotedUnitPrice = excluded.QuotedUnitPrice,
    Discount = excluded.Discount,
    LastPrice = excluded.LastPrice,
    Notes = excluded.Notes,
    SortOrder = excluded.SortOrder;";

                    cmd.Parameters.AddWithValue("@Id", item.Id.ToString());
                    cmd.Parameters.AddWithValue("@RfqId", rfq.Id.ToString());
                    cmd.Parameters.AddWithValue("@PrItemId", item.PrItemId.HasValue ? item.PrItemId.Value.ToString() : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@ItemName", item.ItemName);
                    cmd.Parameters.AddWithValue("@Quantity", (double)item.Quantity);
                    cmd.Parameters.AddWithValue("@Unit", item.Unit ?? "pcs");
                    cmd.Parameters.AddWithValue("@IsQuoted", item.IsQuoted ? 1 : 0);
                    cmd.Parameters.AddWithValue("@QuotedUnitPrice", item.QuotedUnitPrice.HasValue ? (double)item.QuotedUnitPrice.Value : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@Discount", item.Discount.HasValue ? (double)item.Discount.Value : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@LastPrice", item.LastPrice.HasValue ? (double)item.LastPrice.Value : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@Notes", item.Notes ?? string.Empty);
                    cmd.Parameters.AddWithValue("@SortOrder", sortOrder++);

                    await cmd.ExecuteNonQueryAsync();
                }
            }

            await tx.CommitAsync();
        }

        public async Task DeleteRfqAsync(Guid rfqId)
        {
            await _db.InitializeAsync();
            using var connection = _db.CreateConnection();
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM RequestForQuotation WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Id", rfqId.ToString());

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task SavePcrAsync(PriceComparisonRequest pcr)
        {
            await _db.InitializeAsync();
            using var connection = _db.CreateConnection();
            await connection.OpenAsync();
            using var tx = connection.BeginTransaction();

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO PriceComparisonRequest (Id, PrId, PcrNo, CreatedAt, Remarks)
VALUES (@Id, @PrId, @PcrNo, @CreatedAt, @Remarks)
ON CONFLICT(Id) DO UPDATE SET
    PcrNo = excluded.PcrNo,
    Remarks = excluded.Remarks;";

                cmd.Parameters.AddWithValue("@Id", pcr.Id.ToString());
                cmd.Parameters.AddWithValue("@PrId", pcr.PrId.ToString());
                cmd.Parameters.AddWithValue("@PcrNo", pcr.PcrNo);
                cmd.Parameters.AddWithValue("@CreatedAt", pcr.CreatedAt.ToString("o"));
                cmd.Parameters.AddWithValue("@Remarks", pcr.Remarks ?? string.Empty);

                await cmd.ExecuteNonQueryAsync();
            }

            pcr.EnsureDefaultApprovals();

            // Delete any approvals removed from memory
            var activeIds = pcr.Approvals.Select(a => $"'{a.Id}'").ToList();
            if (activeIds.Count > 0)
            {
                using var deleteCmd = connection.CreateCommand();
                deleteCmd.Transaction = tx;
                deleteCmd.CommandText = $"DELETE FROM Approval WHERE PcrId = '{pcr.Id}' AND Id NOT IN ({string.Join(",", activeIds)});";
                await deleteCmd.ExecuteNonQueryAsync();
            }

            foreach (var approval in pcr.Approvals)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO Approval (Id, PcrId, Role, SignedByName, Signed, SignedDate, SentDate, ReceivedDate, SortOrder, RequiresMultipleDates)
VALUES (@Id, @PcrId, @Role, @SignedByName, @Signed, @SignedDate, @SentDate, @ReceivedDate, @SortOrder, @RequiresMultipleDates)
ON CONFLICT(Id) DO UPDATE SET
    Role = excluded.Role,
    SignedByName = excluded.SignedByName,
    Signed = excluded.Signed,
    SignedDate = excluded.SignedDate,
    SentDate = excluded.SentDate,
    ReceivedDate = excluded.ReceivedDate,
    SortOrder = excluded.SortOrder,
    RequiresMultipleDates = excluded.RequiresMultipleDates;";

                cmd.Parameters.AddWithValue("@Id", approval.Id.ToString());
                cmd.Parameters.AddWithValue("@PcrId", pcr.Id.ToString());
                cmd.Parameters.AddWithValue("@Role", approval.Role);
                cmd.Parameters.AddWithValue("@SignedByName", (object?)approval.SignedByName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Signed", (approval.Signed || approval.ReceivedDate.HasValue) ? 1 : 0);
                cmd.Parameters.AddWithValue("@SignedDate", approval.SignedDate.HasValue ? approval.SignedDate.Value.ToString("o") : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@SentDate", approval.SentDate.HasValue ? approval.SentDate.Value.ToString("o") : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ReceivedDate", approval.ReceivedDate.HasValue ? approval.ReceivedDate.Value.ToString("o") : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@SortOrder", approval.SortOrder);
                cmd.Parameters.AddWithValue("@RequiresMultipleDates", approval.RequiresMultipleDates ? 1 : 0);

                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }

        public async Task UpdateApprovalAsync(Approval approval)
        {
            await _db.InitializeAsync();
            using var connection = _db.CreateConnection();
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
INSERT INTO Approval (Id, PcrId, Role, SignedByName, Signed, SignedDate, SentDate, ReceivedDate, SortOrder, RequiresMultipleDates)
VALUES (@Id, @PcrId, @Role, @SignedByName, @Signed, @SignedDate, @SentDate, @ReceivedDate, @SortOrder, @RequiresMultipleDates)
ON CONFLICT(Id) DO UPDATE SET
    Role = excluded.Role,
    SignedByName = excluded.SignedByName,
    Signed = excluded.Signed,
    SignedDate = excluded.SignedDate,
    SentDate = excluded.SentDate,
    ReceivedDate = excluded.ReceivedDate,
    SortOrder = excluded.SortOrder,
    RequiresMultipleDates = excluded.RequiresMultipleDates;";

            cmd.Parameters.AddWithValue("@Id", approval.Id.ToString());
            cmd.Parameters.AddWithValue("@PcrId", approval.PcrId.ToString());
            cmd.Parameters.AddWithValue("@Role", approval.Role);
            cmd.Parameters.AddWithValue("@SignedByName", (object?)approval.SignedByName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Signed", (approval.Signed || approval.ReceivedDate.HasValue) ? 1 : 0);
            cmd.Parameters.AddWithValue("@SignedDate", approval.SignedDate.HasValue ? approval.SignedDate.Value.ToString("o") : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@SentDate", approval.SentDate.HasValue ? approval.SentDate.Value.ToString("o") : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@ReceivedDate", approval.ReceivedDate.HasValue ? approval.ReceivedDate.Value.ToString("o") : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@SortOrder", approval.SortOrder);
            cmd.Parameters.AddWithValue("@RequiresMultipleDates", approval.RequiresMultipleDates ? 1 : 0);

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task SavePoAsync(PurchaseOrder po)
        {
            await _db.InitializeAsync();
            using var connection = _db.CreateConnection();
            await connection.OpenAsync();
            using var tx = connection.BeginTransaction();

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO PurchaseOrder (Id, PrId, PoNo, Vendor, LinkedRfqId, Value, Status, Date, CombinedPrs, Currency, BaseAmount, Freight, OtherCharges, Discount, VatType)
VALUES (@Id, @PrId, @PoNo, @Vendor, @LinkedRfqId, @Value, @Status, @Date, @CombinedPrs, @Currency, @BaseAmount, @Freight, @OtherCharges, @Discount, @VatType)
ON CONFLICT(Id) DO UPDATE SET
    PoNo = excluded.PoNo,
    Vendor = excluded.Vendor,
    LinkedRfqId = excluded.LinkedRfqId,
    Value = excluded.Value,
    Status = excluded.Status,
    Date = excluded.Date,
    CombinedPrs = excluded.CombinedPrs,
    Currency = excluded.Currency,
    BaseAmount = excluded.BaseAmount,
    Freight = excluded.Freight,
    OtherCharges = excluded.OtherCharges,
    Discount = excluded.Discount,
    VatType = excluded.VatType;";

                cmd.Parameters.AddWithValue("@Id", po.Id.ToString());
                cmd.Parameters.AddWithValue("@PrId", po.PrId.ToString());
                cmd.Parameters.AddWithValue("@PoNo", po.PoNo ?? string.Empty);
                cmd.Parameters.AddWithValue("@Vendor", po.Vendor ?? string.Empty);
                cmd.Parameters.AddWithValue("@LinkedRfqId", po.LinkedRfqId.HasValue ? po.LinkedRfqId.Value.ToString() : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Value", po.Value);
                cmd.Parameters.AddWithValue("@Status", po.Status ?? PoStatus.Raised);
                cmd.Parameters.AddWithValue("@Date", po.Date.HasValue ? po.Date.Value.ToString("o") : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@CombinedPrs", po.CombinedPrs ?? string.Empty);
                cmd.Parameters.AddWithValue("@Currency", string.IsNullOrWhiteSpace(po.Currency) ? "AED" : po.Currency);
                cmd.Parameters.AddWithValue("@BaseAmount", po.BaseAmount.HasValue ? (object)po.BaseAmount.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@Freight", po.Freight.HasValue ? (object)po.Freight.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@OtherCharges", po.OtherCharges.HasValue ? (object)po.OtherCharges.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@Discount", po.Discount.HasValue ? (object)po.Discount.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@VatType", string.IsNullOrWhiteSpace(po.VatType) ? "5%" : po.VatType);

                await cmd.ExecuteNonQueryAsync();

                // Save PO Items
                using var delCmd = connection.CreateCommand();
                delCmd.Transaction = tx;
                delCmd.CommandText = "DELETE FROM PurchaseOrderItem WHERE PoId = @PoId;";
                delCmd.Parameters.AddWithValue("@PoId", po.Id.ToString());
                await delCmd.ExecuteNonQueryAsync();

                if (po.Items != null && po.Items.Count > 0)
                {
                    foreach (var item in po.Items)
                    {
                        using var itemCmd = connection.CreateCommand();
                        itemCmd.Transaction = tx;
                        itemCmd.CommandText = @"
INSERT INTO PurchaseOrderItem (Id, PoId, PrItemId, RfqItemId, ItemName, Quantity, Unit, UnitPrice, Discount, LineTotal)
VALUES (@Id, @PoId, @PrItemId, @RfqItemId, @ItemName, @Quantity, @Unit, @UnitPrice, @Discount, @LineTotal);";

                        itemCmd.Parameters.AddWithValue("@Id", item.Id.ToString());
                        itemCmd.Parameters.AddWithValue("@PoId", po.Id.ToString());
                        itemCmd.Parameters.AddWithValue("@PrItemId", item.PrItemId.HasValue ? item.PrItemId.Value.ToString() : (object)DBNull.Value);
                        itemCmd.Parameters.AddWithValue("@RfqItemId", item.RfqItemId.HasValue ? item.RfqItemId.Value.ToString() : (object)DBNull.Value);
                        itemCmd.Parameters.AddWithValue("@ItemName", item.ItemName ?? string.Empty);
                        itemCmd.Parameters.AddWithValue("@Quantity", item.Quantity);
                        itemCmd.Parameters.AddWithValue("@Unit", item.Unit ?? "pcs");
                        itemCmd.Parameters.AddWithValue("@UnitPrice", item.UnitPrice.HasValue ? (object)item.UnitPrice.Value : DBNull.Value);
                        itemCmd.Parameters.AddWithValue("@Discount", item.Discount.HasValue ? (object)item.Discount.Value : DBNull.Value);
                        itemCmd.Parameters.AddWithValue("@LineTotal", item.LineTotal);

                        await itemCmd.ExecuteNonQueryAsync();
                    }
                }

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        public async Task DeletePoAsync(Guid poId)
        {
            await _db.InitializeAsync();
            using var connection = _db.CreateConnection();
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM PurchaseOrder WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Id", poId.ToString());

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task MergePrsAsync(List<PurchaseRequisition> sourcePrs, PurchaseRequisition masterPr, bool copyRfqs)
        {
            await _db.InitializeAsync();
            masterPr.UpdatedAt = DateTime.Now;

            using var connection = _db.CreateConnection();
            await connection.OpenAsync();
            using var tx = connection.BeginTransaction();

            // 1. Save master PR
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO PurchaseRequisition (Id, PrNo, Description, Requestor, Priority, Status, Notes, CreatedAt, UpdatedAt, ParentPrId, ConsolidatedFrom)
VALUES (@Id, @PrNo, @Description, @Requestor, @Priority, @Status, @Notes, @CreatedAt, @UpdatedAt, @ParentPrId, @ConsolidatedFrom);";

                cmd.Parameters.AddWithValue("@Id", masterPr.Id.ToString());
                cmd.Parameters.AddWithValue("@PrNo", masterPr.PrNo);
                cmd.Parameters.AddWithValue("@Description", masterPr.Description);
                cmd.Parameters.AddWithValue("@Requestor", masterPr.Requestor);
                cmd.Parameters.AddWithValue("@Priority", masterPr.Priority);
                cmd.Parameters.AddWithValue("@Status", masterPr.Status);
                cmd.Parameters.AddWithValue("@Notes", masterPr.Notes);
                cmd.Parameters.AddWithValue("@CreatedAt", masterPr.CreatedAt.ToString("o"));
                cmd.Parameters.AddWithValue("@UpdatedAt", masterPr.UpdatedAt.ToString("o"));
                cmd.Parameters.AddWithValue("@ParentPrId", DBNull.Value);
                cmd.Parameters.AddWithValue("@ConsolidatedFrom", masterPr.ConsolidatedFrom ?? string.Empty);

                await cmd.ExecuteNonQueryAsync();
            }

            // 1b. Consolidate line items from source PRs into master PR
            int itemSort = 0;
            foreach (var sourcePr in sourcePrs)
            {
                if (sourcePr.Items != null)
                {
                    foreach (var srcItem in sourcePr.Items)
                    {
                        var masterItem = new PrItem
                        {
                            Id = Guid.NewGuid(),
                            PrId = masterPr.Id,
                            ItemName = srcItem.ItemName,
                            Quantity = srcItem.Quantity,
                            Unit = srcItem.Unit,
                            EstimatedUnitPrice = srcItem.EstimatedUnitPrice,
                            Notes = string.IsNullOrWhiteSpace(srcItem.Notes) ? $"From {sourcePr.PrNo}" : $"{srcItem.Notes} (From {sourcePr.PrNo})",
                            SortOrder = itemSort++
                        };
                        masterPr.Items.Add(masterItem);

                        using var itemCmd = connection.CreateCommand();
                        itemCmd.Transaction = tx;
                        itemCmd.CommandText = @"
INSERT INTO PrItem (Id, PrId, ItemName, Quantity, Unit, EstimatedUnitPrice, Notes, SortOrder)
VALUES (@Id, @PrId, @ItemName, @Quantity, @Unit, @EstimatedUnitPrice, @Notes, @SortOrder);";

                        itemCmd.Parameters.AddWithValue("@Id", masterItem.Id.ToString());
                        itemCmd.Parameters.AddWithValue("@PrId", masterItem.PrId.ToString());
                        itemCmd.Parameters.AddWithValue("@ItemName", masterItem.ItemName.Trim());
                        itemCmd.Parameters.AddWithValue("@Quantity", (double)masterItem.Quantity);
                        itemCmd.Parameters.AddWithValue("@Unit", string.IsNullOrWhiteSpace(masterItem.Unit) ? "pcs" : masterItem.Unit.Trim());
                        itemCmd.Parameters.AddWithValue("@EstimatedUnitPrice", masterItem.EstimatedUnitPrice.HasValue ? (double)masterItem.EstimatedUnitPrice.Value : (object)DBNull.Value);
                        itemCmd.Parameters.AddWithValue("@Notes", masterItem.Notes ?? string.Empty);
                        itemCmd.Parameters.AddWithValue("@SortOrder", masterItem.SortOrder);

                        await itemCmd.ExecuteNonQueryAsync();
                    }
                }
            }

            // 2. Optionally copy RFQs from source PRs to master PR
            if (copyRfqs)
            {
                foreach (var sourcePr in sourcePrs)
                {
                    foreach (var rfq in sourcePr.Rfqs)
                    {
                        var vendorClean = string.IsNullOrWhiteSpace(rfq.Vendor) ? "VEND" : rfq.Vendor.Replace(" ", "");
                        var vendorSuffix = vendorClean.Substring(0, Math.Min(4, vendorClean.Length)).ToUpper();
                        var newRfq = new RequestForQuotation
                        {
                            Id = Guid.NewGuid(),
                            PrId = masterPr.Id,
                            RfqNo = $"RFQ-{masterPr.PrNo.Replace("PR-", "")}-{vendorSuffix}",
                            Vendor = rfq.Vendor,
                            Status = rfq.Status,
                            SentDate = rfq.SentDate,
                            QuoteReceivedDate = rfq.QuoteReceivedDate,
                            QuoteAmount = rfq.QuoteAmount,
                            PaymentTerms = rfq.PaymentTerms,
                            VatType = rfq.VatType,
                            Freight = rfq.Freight,
                            OtherCharges = rfq.OtherCharges,
                            Discount = rfq.Discount,
                            Incoterms = rfq.Incoterms,
                            DeliveryLeadTime = rfq.DeliveryLeadTime,
                            Warranty = rfq.Warranty,
                            TechnicalApproval = rfq.TechnicalApproval,
                            Currency = rfq.Currency
                        };
                        masterPr.Rfqs.Add(newRfq);

                        using var rfqCmd = connection.CreateCommand();
                        rfqCmd.Transaction = tx;
                        rfqCmd.CommandText = @"
INSERT INTO RequestForQuotation (Id, PrId, RfqNo, Vendor, Status, SentDate, QuoteReceivedDate, QuoteAmount, PaymentTerms, VatType, Freight, OtherCharges, Discount, Incoterms, DeliveryLeadTime, Currency, Warranty, TechnicalApproval)
VALUES (@Id, @PrId, @RfqNo, @Vendor, @Status, @SentDate, @QuoteReceivedDate, @QuoteAmount, @PaymentTerms, @VatType, @Freight, @OtherCharges, @Discount, @Incoterms, @DeliveryLeadTime, @Currency, @Warranty, @TechnicalApproval);";

                        rfqCmd.Parameters.AddWithValue("@Id", newRfq.Id.ToString());
                        rfqCmd.Parameters.AddWithValue("@PrId", masterPr.Id.ToString());
                        rfqCmd.Parameters.AddWithValue("@RfqNo", newRfq.RfqNo);
                        rfqCmd.Parameters.AddWithValue("@Vendor", newRfq.Vendor);
                        rfqCmd.Parameters.AddWithValue("@Status", newRfq.Status);
                        rfqCmd.Parameters.AddWithValue("@SentDate", newRfq.SentDate.HasValue ? newRfq.SentDate.Value.ToString("o") : (object)DBNull.Value);
                        rfqCmd.Parameters.AddWithValue("@QuoteReceivedDate", newRfq.QuoteReceivedDate.HasValue ? newRfq.QuoteReceivedDate.Value.ToString("o") : (object)DBNull.Value);
                        rfqCmd.Parameters.AddWithValue("@QuoteAmount", newRfq.BaseAmount > 0 ? (object)newRfq.BaseAmount : (newRfq.QuoteAmount.HasValue ? newRfq.QuoteAmount.Value : (object)DBNull.Value));
                        rfqCmd.Parameters.AddWithValue("@PaymentTerms", newRfq.PaymentTerms ?? "30 Days Net");
                        rfqCmd.Parameters.AddWithValue("@VatType", newRfq.VatType ?? "5%");
                        rfqCmd.Parameters.AddWithValue("@Freight", newRfq.Freight.HasValue ? newRfq.Freight.Value : (object)DBNull.Value);
                        rfqCmd.Parameters.AddWithValue("@OtherCharges", newRfq.OtherCharges.HasValue ? newRfq.OtherCharges.Value : (object)DBNull.Value);
                        rfqCmd.Parameters.AddWithValue("@Discount", newRfq.Discount.HasValue ? newRfq.Discount.Value : (object)DBNull.Value);
                        rfqCmd.Parameters.AddWithValue("@Incoterms", newRfq.Incoterms ?? "DDP");
                        rfqCmd.Parameters.AddWithValue("@DeliveryLeadTime", newRfq.DeliveryLeadTime ?? string.Empty);
                        rfqCmd.Parameters.AddWithValue("@Warranty", newRfq.Warranty ?? string.Empty);
                        rfqCmd.Parameters.AddWithValue("@TechnicalApproval", newRfq.TechnicalApproval ?? string.Empty);
                        rfqCmd.Parameters.AddWithValue("@Currency", newRfq.Currency ?? "AED");

                        await rfqCmd.ExecuteNonQueryAsync();

                        if (rfq.Items != null)
                        {
                            int rfqItemSort = 0;
                            foreach (var srcRfqItem in rfq.Items)
                            {
                                var newRfqItem = new RfqItem
                                {
                                    Id = Guid.NewGuid(),
                                    RfqId = newRfq.Id,
                                    ItemName = srcRfqItem.ItemName,
                                    Quantity = srcRfqItem.Quantity,
                                    Unit = srcRfqItem.Unit,
                                    IsQuoted = srcRfqItem.IsQuoted,
                                    QuotedUnitPrice = srcRfqItem.QuotedUnitPrice,
                                    Discount = srcRfqItem.Discount,
                                    LastPrice = srcRfqItem.LastPrice,
                                    Notes = srcRfqItem.Notes,
                                    SortOrder = rfqItemSort++
                                };
                                newRfq.Items.Add(newRfqItem);

                                using var rfqItemCmd = connection.CreateCommand();
                                rfqItemCmd.Transaction = tx;
                                rfqItemCmd.CommandText = @"
INSERT INTO RfqItem (Id, RfqId, ItemName, Quantity, Unit, IsQuoted, QuotedUnitPrice, Discount, LastPrice, Notes, SortOrder)
VALUES (@Id, @RfqId, @ItemName, @Quantity, @Unit, @IsQuoted, @QuotedUnitPrice, @Discount, @LastPrice, @Notes, @SortOrder);";

                                rfqItemCmd.Parameters.AddWithValue("@Id", newRfqItem.Id.ToString());
                                rfqItemCmd.Parameters.AddWithValue("@RfqId", newRfq.Id.ToString());
                                rfqItemCmd.Parameters.AddWithValue("@ItemName", newRfqItem.ItemName);
                                rfqItemCmd.Parameters.AddWithValue("@Quantity", (double)newRfqItem.Quantity);
                                rfqItemCmd.Parameters.AddWithValue("@Unit", newRfqItem.Unit ?? "pcs");
                                rfqItemCmd.Parameters.AddWithValue("@IsQuoted", newRfqItem.IsQuoted ? 1 : 0);
                                rfqItemCmd.Parameters.AddWithValue("@QuotedUnitPrice", newRfqItem.QuotedUnitPrice.HasValue ? (double)newRfqItem.QuotedUnitPrice.Value : (object)DBNull.Value);
                                rfqItemCmd.Parameters.AddWithValue("@Discount", newRfqItem.Discount.HasValue ? (double)newRfqItem.Discount.Value : (object)DBNull.Value);
                                rfqItemCmd.Parameters.AddWithValue("@LastPrice", newRfqItem.LastPrice.HasValue ? (double)newRfqItem.LastPrice.Value : (object)DBNull.Value);
                                rfqItemCmd.Parameters.AddWithValue("@Notes", newRfqItem.Notes ?? string.Empty);
                                rfqItemCmd.Parameters.AddWithValue("@SortOrder", newRfqItem.SortOrder);

                                await rfqItemCmd.ExecuteNonQueryAsync();
                            }
                        }
                    }
                }
            }

            // 3. Update source PRs: status = Merged, ParentPrId = masterPr.Id, Notes updated
            foreach (var src in sourcePrs)
            {
                src.Status = ProcurementStatus.Merged;
                src.ParentPrId = masterPr.Id;
                src.UpdatedAt = DateTime.Now;
                if (!src.Notes.Contains($"Merged into {masterPr.PrNo}"))
                {
                    src.Notes = string.IsNullOrWhiteSpace(src.Notes)
                        ? $"Merged into {masterPr.PrNo}"
                        : $"{src.Notes}\nMerged into {masterPr.PrNo}";
                }

                using var srcCmd = connection.CreateCommand();
                srcCmd.Transaction = tx;
                srcCmd.CommandText = @"
UPDATE PurchaseRequisition
SET Status = @Status, ParentPrId = @ParentPrId, Notes = @Notes, UpdatedAt = @UpdatedAt
WHERE Id = @Id;";

                srcCmd.Parameters.AddWithValue("@Id", src.Id.ToString());
                srcCmd.Parameters.AddWithValue("@Status", src.Status);
                srcCmd.Parameters.AddWithValue("@ParentPrId", masterPr.Id.ToString());
                srcCmd.Parameters.AddWithValue("@Notes", src.Notes);
                srcCmd.Parameters.AddWithValue("@UpdatedAt", src.UpdatedAt.ToString("o"));

                await srcCmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }

        public async Task CreateBatchPoAsync(List<PurchaseRequisition> targetPrs, PurchaseOrder poTemplate)
        {
            await _db.InitializeAsync();
            using var connection = _db.CreateConnection();
            await connection.OpenAsync();
            using var tx = connection.BeginTransaction();

            var allocatedValue = targetPrs.Count > 0 && poTemplate.Value > 0 ? (poTemplate.Value / targetPrs.Count) : 0m;
            var combinedPrNumbers = string.Join(", ", targetPrs.Select(p => p.PrNo));

            foreach (var pr in targetPrs)
            {
                var po = new PurchaseOrder
                {
                    Id = Guid.NewGuid(),
                    PrId = pr.Id,
                    PoNo = poTemplate.PoNo,
                    Vendor = poTemplate.Vendor,
                    Value = allocatedValue,
                    Status = poTemplate.Status,
                    Date = poTemplate.Date ?? DateTime.Today,
                    CombinedPrs = combinedPrNumbers,
                    Currency = string.IsNullOrWhiteSpace(poTemplate.Currency) ? "AED" : poTemplate.Currency
                };
                pr.Pos.Add(po);

                if (pr.Status == ProcurementStatus.PcrApproved || pr.Status == ProcurementStatus.PcrSubmitted || pr.Status == ProcurementStatus.QuotesReceived || pr.Status == ProcurementStatus.PrRaised)
                {
                    pr.Status = ProcurementStatus.PoRaised;
                }

                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO PurchaseOrder (Id, PrId, PoNo, Vendor, LinkedRfqId, Value, Status, Date, CombinedPrs, Currency)
VALUES (@Id, @PrId, @PoNo, @Vendor, @LinkedRfqId, @Value, @Status, @Date, @CombinedPrs, @Currency);

UPDATE PurchaseRequisition SET Status = @PrStatus, UpdatedAt = @UpdatedAt WHERE Id = @PrId;";

                cmd.Parameters.AddWithValue("@Id", po.Id.ToString());
                cmd.Parameters.AddWithValue("@PrId", pr.Id.ToString());
                cmd.Parameters.AddWithValue("@PoNo", po.PoNo);
                cmd.Parameters.AddWithValue("@Vendor", po.Vendor);
                cmd.Parameters.AddWithValue("@LinkedRfqId", DBNull.Value);
                cmd.Parameters.AddWithValue("@Value", po.Value);
                cmd.Parameters.AddWithValue("@Status", po.Status);
                cmd.Parameters.AddWithValue("@Date", po.Date.HasValue ? po.Date.Value.ToString("o") : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@CombinedPrs", po.CombinedPrs);
                cmd.Parameters.AddWithValue("@Currency", po.Currency);
                cmd.Parameters.AddWithValue("@PrStatus", pr.Status);
                cmd.Parameters.AddWithValue("@UpdatedAt", DateTime.Now.ToString("o"));

                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();

            foreach (var pr in targetPrs)
            {
                pr.NotifyHierarchyChanged();
            }
        }

        public async Task CreateBatchRfqAsync(List<PurchaseRequisition> targetPrs, RequestForQuotation rfqTemplate, IEnumerable<RfqItem>? batchItems = null)
        {
            await _db.InitializeAsync();
            using var connection = _db.CreateConnection();
            await connection.OpenAsync();
            using var tx = connection.BeginTransaction();

            var sharedPrNumbers = string.Join(", ", targetPrs.Select(p => p.PrNo));
            var batchItemList = batchItems?.ToList() ?? new List<RfqItem>();

            foreach (var pr in targetPrs)
            {
                var prSpecificItems = batchItemList.Where(bi => bi.Notes == pr.PrNo || (pr.Items != null && pr.Items.Any(pi => pi.Id == bi.PrItemId))).ToList();

                var prQuotedSum = prSpecificItems.Where(i => i.IsQuoted).Sum(i => i.LineTotal);
                var prQuoteAmount = prQuotedSum > 0 ? prQuotedSum : rfqTemplate.QuoteAmount;

                var rfq = new RequestForQuotation
                {
                    Id = Guid.NewGuid(),
                    PrId = pr.Id,
                    RfqNo = rfqTemplate.RfqNo,
                    Vendor = rfqTemplate.Vendor,
                    Currency = rfqTemplate.Currency,
                    Status = (prQuoteAmount.HasValue && prQuoteAmount.Value > 0) ? RfqStatus.QuoteReceived : rfqTemplate.Status,
                    SentDate = rfqTemplate.SentDate ?? DateTime.Today,
                    QuoteReceivedDate = (prQuoteAmount.HasValue && prQuoteAmount.Value > 0) ? DateTime.Today : rfqTemplate.QuoteReceivedDate,
                    QuoteAmount = prQuoteAmount,
                    PaymentTerms = rfqTemplate.PaymentTerms,
                    VatType = rfqTemplate.VatType,
                    Freight = rfqTemplate.Freight,
                    OtherCharges = rfqTemplate.OtherCharges,
                    Discount = rfqTemplate.Discount,
                    Incoterms = rfqTemplate.Incoterms,
                    DeliveryLeadTime = rfqTemplate.DeliveryLeadTime,
                    Warranty = rfqTemplate.Warranty,
                    TechnicalApproval = rfqTemplate.TechnicalApproval,
                    SharedPrs = sharedPrNumbers,
                    Items = new ObservableCollection<RfqItem>()
                };
                pr.Rfqs.Add(rfq);

                if (pr.Status == ProcurementStatus.PrRaised)
                {
                    pr.Status = (rfq.Status == RfqStatus.QuoteReceived) ? ProcurementStatus.QuotesReceived : ProcurementStatus.RfqSent;
                }

                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO RequestForQuotation (Id, PrId, RfqNo, Vendor, Status, SentDate, QuoteReceivedDate, QuoteAmount, PaymentTerms, VatType, Freight, OtherCharges, Discount, Incoterms, DeliveryLeadTime, Currency, SharedPrs, Warranty, TechnicalApproval)
VALUES (@Id, @PrId, @RfqNo, @Vendor, @Status, @SentDate, @QuoteReceivedDate, @QuoteAmount, @PaymentTerms, @VatType, @Freight, @OtherCharges, @Discount, @Incoterms, @DeliveryLeadTime, @Currency, @SharedPrs, @Warranty, @TechnicalApproval);

UPDATE PurchaseRequisition SET Status = @PrStatus, UpdatedAt = @UpdatedAt WHERE Id = @PrId;";

                cmd.Parameters.AddWithValue("@Id", rfq.Id.ToString());
                cmd.Parameters.AddWithValue("@PrId", pr.Id.ToString());
                cmd.Parameters.AddWithValue("@RfqNo", rfq.RfqNo);
                cmd.Parameters.AddWithValue("@Vendor", rfq.Vendor);
                cmd.Parameters.AddWithValue("@Status", rfq.Status);
                cmd.Parameters.AddWithValue("@SentDate", rfq.SentDate.HasValue ? rfq.SentDate.Value.ToString("o") : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@QuoteReceivedDate", rfq.QuoteReceivedDate.HasValue ? rfq.QuoteReceivedDate.Value.ToString("o") : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@QuoteAmount", rfq.QuoteAmount.HasValue ? rfq.QuoteAmount.Value : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@PaymentTerms", rfq.PaymentTerms ?? "30 Days Net");
                cmd.Parameters.AddWithValue("@VatType", rfq.VatType ?? "5%");
                cmd.Parameters.AddWithValue("@Freight", rfq.Freight.HasValue ? rfq.Freight.Value : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@OtherCharges", rfq.OtherCharges.HasValue ? rfq.OtherCharges.Value : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Discount", rfq.Discount.HasValue ? rfq.Discount.Value : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Incoterms", rfq.Incoterms ?? "DDP");
                cmd.Parameters.AddWithValue("@DeliveryLeadTime", rfq.DeliveryLeadTime ?? string.Empty);
                cmd.Parameters.AddWithValue("@Currency", rfq.Currency ?? "AED");
                cmd.Parameters.AddWithValue("@SharedPrs", rfq.SharedPrs);
                cmd.Parameters.AddWithValue("@Warranty", rfq.Warranty ?? string.Empty);
                cmd.Parameters.AddWithValue("@TechnicalApproval", rfq.TechnicalApproval ?? string.Empty);
                cmd.Parameters.AddWithValue("@PrStatus", pr.Status);
                cmd.Parameters.AddWithValue("@UpdatedAt", DateTime.Now.ToString("o"));

                await cmd.ExecuteNonQueryAsync();

                // Save RfqItems for this specific PR
                int sort = 0;
                foreach (var bi in prSpecificItems)
                {
                    var rfqItem = new RfqItem
                    {
                        Id = Guid.NewGuid(),
                        RfqId = rfq.Id,
                        PrItemId = bi.PrItemId,
                        ItemName = bi.ItemName,
                        Quantity = bi.Quantity,
                        Unit = bi.Unit,
                        IsQuoted = bi.IsQuoted,
                        QuotedUnitPrice = bi.QuotedUnitPrice,
                        Discount = bi.Discount,
                        LastPrice = bi.LastPrice,
                        Notes = bi.Notes,
                        SortOrder = sort++
                    };
                    rfq.Items.Add(rfqItem);

                    using var itemCmd = connection.CreateCommand();
                    itemCmd.Transaction = tx;
                    itemCmd.CommandText = @"
INSERT INTO RfqItem (Id, RfqId, PrItemId, ItemName, Quantity, Unit, IsQuoted, QuotedUnitPrice, Discount, LastPrice, Notes, SortOrder)
VALUES (@Id, @RfqId, @PrItemId, @ItemName, @Quantity, @Unit, @IsQuoted, @QuotedUnitPrice, @Discount, @LastPrice, @Notes, @SortOrder);";

                    itemCmd.Parameters.AddWithValue("@Id", rfqItem.Id.ToString());
                    itemCmd.Parameters.AddWithValue("@RfqId", rfq.Id.ToString());
                    itemCmd.Parameters.AddWithValue("@PrItemId", rfqItem.PrItemId.HasValue ? rfqItem.PrItemId.Value.ToString() : (object)DBNull.Value);
                    itemCmd.Parameters.AddWithValue("@ItemName", rfqItem.ItemName);
                    itemCmd.Parameters.AddWithValue("@Quantity", (double)rfqItem.Quantity);
                    itemCmd.Parameters.AddWithValue("@Unit", rfqItem.Unit ?? "pcs");
                    itemCmd.Parameters.AddWithValue("@IsQuoted", rfqItem.IsQuoted ? 1 : 0);
                    itemCmd.Parameters.AddWithValue("@QuotedUnitPrice", rfqItem.QuotedUnitPrice.HasValue ? (double)rfqItem.QuotedUnitPrice.Value : (object)DBNull.Value);
                    itemCmd.Parameters.AddWithValue("@Discount", rfqItem.Discount.HasValue ? (double)rfqItem.Discount.Value : (object)DBNull.Value);
                    itemCmd.Parameters.AddWithValue("@LastPrice", rfqItem.LastPrice.HasValue ? (double)rfqItem.LastPrice.Value : (object)DBNull.Value);
                    itemCmd.Parameters.AddWithValue("@Notes", rfqItem.Notes ?? string.Empty);
                    itemCmd.Parameters.AddWithValue("@SortOrder", rfqItem.SortOrder);

                    await itemCmd.ExecuteNonQueryAsync();
                }
            }

            await tx.CommitAsync();

            foreach (var pr in targetPrs)
            {
                pr.NotifyHierarchyChanged();
            }
        }

        public async Task SplitMergedPrAsync(Guid masterPrId)
        {
            await _db.InitializeAsync();
            using var connection = _db.CreateConnection();
            await connection.OpenAsync();
            using var tx = connection.BeginTransaction();

            // 1. Get the master PR details
            string? consolidatedFrom = null;
            using (var getMasterCmd = connection.CreateCommand())
            {
                getMasterCmd.Transaction = tx;
                getMasterCmd.CommandText = "SELECT ConsolidatedFrom FROM PurchaseRequisition WHERE Id = @Id;";
                getMasterCmd.Parameters.AddWithValue("@Id", masterPrId.ToString());
                var obj = await getMasterCmd.ExecuteScalarAsync();
                consolidatedFrom = obj?.ToString();
            }

            // 2. Find and restore child PRs linked to this master PR
            var childIds = new List<string>();
            using (var findCmd = connection.CreateCommand())
            {
                findCmd.Transaction = tx;
                findCmd.CommandText = "SELECT Id FROM PurchaseRequisition WHERE ParentPrId = @ParentId;";
                findCmd.Parameters.AddWithValue("@ParentId", masterPrId.ToString());
                using var reader = await findCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    childIds.Add(reader.GetString(0));
                }
            }

            // If no children by ParentPrId, fallback to matching by ConsolidatedFrom PR numbers
            if (childIds.Count == 0 && !string.IsNullOrWhiteSpace(consolidatedFrom))
            {
                var prNos = consolidatedFrom.Split(new[] { ',', '/' }, StringSplitOptions.RemoveEmptyEntries)
                                            .Select(s => s.Trim())
                                            .Where(s => !string.IsNullOrWhiteSpace(s))
                                            .ToList();
                foreach (var prNo in prNos)
                {
                    using var findByNoCmd = connection.CreateCommand();
                    findByNoCmd.Transaction = tx;
                    findByNoCmd.CommandText = "SELECT Id FROM PurchaseRequisition WHERE PrNo = @PrNo OR PrNo = @PrNoWithPrefix;";
                    findByNoCmd.Parameters.AddWithValue("@PrNo", prNo);
                    findByNoCmd.Parameters.AddWithValue("@PrNoWithPrefix", prNo.StartsWith("PR-", StringComparison.OrdinalIgnoreCase) ? prNo : $"PR-{prNo}");
                    using var rdr = await findByNoCmd.ExecuteReaderAsync();
                    while (await rdr.ReadAsync())
                    {
                        var id = rdr.GetString(0);
                        if (!childIds.Contains(id)) childIds.Add(id);
                    }
                }
            }

            // 3. Restore each child PR
            foreach (var childId in childIds)
            {
                string restoredStatus = ProcurementStatus.PrRaised;
                using (var checkPoCmd = connection.CreateCommand())
                {
                    checkPoCmd.Transaction = tx;
                    checkPoCmd.CommandText = "SELECT COUNT(*) FROM PurchaseOrder WHERE PrId = @PrId;";
                    checkPoCmd.Parameters.AddWithValue("@PrId", childId);
                    var poCount = Convert.ToInt32(await checkPoCmd.ExecuteScalarAsync());
                    if (poCount > 0)
                    {
                        restoredStatus = ProcurementStatus.PoRaised;
                    }
                    else
                    {
                        using var checkRfqCmd = connection.CreateCommand();
                        checkRfqCmd.Transaction = tx;
                        checkRfqCmd.CommandText = "SELECT COUNT(*) FROM RequestForQuotation WHERE PrId = @PrId;";
                        checkRfqCmd.Parameters.AddWithValue("@PrId", childId);
                        var rfqCount = Convert.ToInt32(await checkRfqCmd.ExecuteScalarAsync());
                        if (rfqCount > 0)
                        {
                            restoredStatus = ProcurementStatus.RfqSent;
                        }
                    }
                }

                using var restoreCmd = connection.CreateCommand();
                restoreCmd.Transaction = tx;
                restoreCmd.CommandText = @"
UPDATE PurchaseRequisition
SET Status = @Status, ParentPrId = NULL, UpdatedAt = @UpdatedAt
WHERE Id = @Id;";
                restoreCmd.Parameters.AddWithValue("@Status", restoredStatus);
                restoreCmd.Parameters.AddWithValue("@UpdatedAt", DateTime.Now.ToString("o"));
                restoreCmd.Parameters.AddWithValue("@Id", childId);
                await restoreCmd.ExecuteNonQueryAsync();
            }

            // 4. Delete the master PR and its cascaded data
            using (var delRfqItemsCmd = connection.CreateCommand())
            {
                delRfqItemsCmd.Transaction = tx;
                delRfqItemsCmd.CommandText = "DELETE FROM RfqItem WHERE RfqId IN (SELECT Id FROM RequestForQuotation WHERE PrId = @PrId);";
                delRfqItemsCmd.Parameters.AddWithValue("@PrId", masterPrId.ToString());
                await delRfqItemsCmd.ExecuteNonQueryAsync();
            }

            using (var delRfqsCmd = connection.CreateCommand())
            {
                delRfqsCmd.Transaction = tx;
                delRfqsCmd.CommandText = "DELETE FROM RequestForQuotation WHERE PrId = @PrId;";
                delRfqsCmd.Parameters.AddWithValue("@PrId", masterPrId.ToString());
                await delRfqsCmd.ExecuteNonQueryAsync();
            }

            using (var delPosCmd = connection.CreateCommand())
            {
                delPosCmd.Transaction = tx;
                delPosCmd.CommandText = "DELETE FROM PurchaseOrder WHERE PrId = @PrId;";
                delPosCmd.Parameters.AddWithValue("@PrId", masterPrId.ToString());
                await delPosCmd.ExecuteNonQueryAsync();
            }

            using (var delPcrCmd = connection.CreateCommand())
            {
                delPcrCmd.Transaction = tx;
                delPcrCmd.CommandText = @"
DELETE FROM Approval WHERE PcrId IN (SELECT Id FROM PriceComparisonRequest WHERE PrId = @PrId);
DELETE FROM PriceComparisonRequest WHERE PrId = @PrId;";
                delPcrCmd.Parameters.AddWithValue("@PrId", masterPrId.ToString());
                await delPcrCmd.ExecuteNonQueryAsync();
            }

            using (var delItemsCmd = connection.CreateCommand())
            {
                delItemsCmd.Transaction = tx;
                delItemsCmd.CommandText = "DELETE FROM PrItem WHERE PrId = @PrId;";
                delItemsCmd.Parameters.AddWithValue("@PrId", masterPrId.ToString());
                await delItemsCmd.ExecuteNonQueryAsync();
            }

            using (var delPrCmd = connection.CreateCommand())
            {
                delPrCmd.Transaction = tx;
                delPrCmd.CommandText = "DELETE FROM PurchaseRequisition WHERE Id = @Id;";
                delPrCmd.Parameters.AddWithValue("@Id", masterPrId.ToString());
                await delPrCmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }

        public async Task PartialSplitMergedPrAsync(Guid masterPrId, List<PurchaseRequisition> splitPrs, List<PurchaseRequisition> keptPrs)
        {
            await _db.InitializeAsync();
            using var connection = _db.CreateConnection();
            await connection.OpenAsync();
            using var tx = connection.BeginTransaction();

            // 1. Restore each split PR
            foreach (var child in splitPrs)
            {
                string restoredStatus = ProcurementStatus.PrRaised;
                using (var checkPoCmd = connection.CreateCommand())
                {
                    checkPoCmd.Transaction = tx;
                    checkPoCmd.CommandText = "SELECT COUNT(*) FROM PurchaseOrder WHERE PrId = @PrId;";
                    checkPoCmd.Parameters.AddWithValue("@PrId", child.Id.ToString());
                    var poCount = Convert.ToInt32(await checkPoCmd.ExecuteScalarAsync());
                    if (poCount > 0)
                    {
                        restoredStatus = ProcurementStatus.PoRaised;
                    }
                    else
                    {
                        using var checkRfqCmd = connection.CreateCommand();
                        checkRfqCmd.Transaction = tx;
                        checkRfqCmd.CommandText = "SELECT COUNT(*) FROM RequestForQuotation WHERE PrId = @PrId;";
                        checkRfqCmd.Parameters.AddWithValue("@PrId", child.Id.ToString());
                        var rfqCount = Convert.ToInt32(await checkRfqCmd.ExecuteScalarAsync());
                        if (rfqCount > 0)
                        {
                            restoredStatus = ProcurementStatus.RfqSent;
                        }
                    }
                }

                // Clean Notes by removing "Merged into ..." if present
                var cleanedNotes = child.Notes ?? string.Empty;
                var lines = cleanedNotes.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
                                        .Where(l => !l.StartsWith("Merged into", StringComparison.OrdinalIgnoreCase))
                                        .ToList();
                cleanedNotes = string.Join("\n", lines).Trim();

                using var restoreCmd = connection.CreateCommand();
                restoreCmd.Transaction = tx;
                restoreCmd.CommandText = @"
UPDATE PurchaseRequisition
SET Status = @Status, ParentPrId = NULL, Notes = @Notes, UpdatedAt = @UpdatedAt
WHERE Id = @Id;";
                restoreCmd.Parameters.AddWithValue("@Status", restoredStatus);
                restoreCmd.Parameters.AddWithValue("@Notes", cleanedNotes);
                restoreCmd.Parameters.AddWithValue("@UpdatedAt", DateTime.Now.ToString("o"));
                restoreCmd.Parameters.AddWithValue("@Id", child.Id.ToString());
                await restoreCmd.ExecuteNonQueryAsync();
            }

            // 2. Update master PR metadata for remaining kept PRs
            var cleanNumbers = keptPrs.Select(p =>
            {
                var no = p.PrNo.Trim();
                if (no.StartsWith("PR-", StringComparison.OrdinalIgnoreCase))
                    return no.Substring(3).Trim();
                if (no.StartsWith("PR ", StringComparison.OrdinalIgnoreCase))
                    return no.Substring(3).Trim();
                return no;
            }).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

            var newPrNo = cleanNumbers.Count > 0 ? string.Join("/", cleanNumbers) : $"PR-{DateTime.Now.Year}-{masterPrId.ToString().Substring(0, 4)}";
            var newConsolidatedFrom = string.Join(", ", keptPrs.Select(p => p.PrNo));
            var newDescription = string.Join("\n", keptPrs.Select(p => $"• {p.PrNo}: {p.Description} ({p.Requestor})"));
            var newNotes = $"Consolidated from: {newConsolidatedFrom}";

            using (var updateMasterCmd = connection.CreateCommand())
            {
                updateMasterCmd.Transaction = tx;
                updateMasterCmd.CommandText = @"
UPDATE PurchaseRequisition
SET PrNo = @PrNo, Description = @Description, ConsolidatedFrom = @ConsolidatedFrom, Notes = @Notes, UpdatedAt = @UpdatedAt
WHERE Id = @Id;";
                updateMasterCmd.Parameters.AddWithValue("@PrNo", newPrNo);
                updateMasterCmd.Parameters.AddWithValue("@Description", newDescription);
                updateMasterCmd.Parameters.AddWithValue("@ConsolidatedFrom", newConsolidatedFrom);
                updateMasterCmd.Parameters.AddWithValue("@Notes", newNotes);
                updateMasterCmd.Parameters.AddWithValue("@UpdatedAt", DateTime.Now.ToString("o"));
                updateMasterCmd.Parameters.AddWithValue("@Id", masterPrId.ToString());
                await updateMasterCmd.ExecuteNonQueryAsync();
            }

            // 3. Rebuild line items on master PR: wipe current and re-insert from kept PRs
            using (var delItemsCmd = connection.CreateCommand())
            {
                delItemsCmd.Transaction = tx;
                delItemsCmd.CommandText = "DELETE FROM PrItem WHERE PrId = @MasterId;";
                delItemsCmd.Parameters.AddWithValue("@MasterId", masterPrId.ToString());
                await delItemsCmd.ExecuteNonQueryAsync();
            }

            int itemSort = 0;
            foreach (var keptPr in keptPrs)
            {
                if (keptPr.Items != null)
                {
                    foreach (var srcItem in keptPr.Items)
                    {
                        using var itemCmd = connection.CreateCommand();
                        itemCmd.Transaction = tx;
                        itemCmd.CommandText = @"
INSERT INTO PrItem (Id, PrId, ItemName, Quantity, Unit, EstimatedUnitPrice, Notes, SortOrder)
VALUES (@Id, @PrId, @ItemName, @Quantity, @Unit, @EstimatedUnitPrice, @Notes, @SortOrder);";

                        itemCmd.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString());
                        itemCmd.Parameters.AddWithValue("@PrId", masterPrId.ToString());
                        itemCmd.Parameters.AddWithValue("@ItemName", srcItem.ItemName.Trim());
                        itemCmd.Parameters.AddWithValue("@Quantity", (double)srcItem.Quantity);
                        itemCmd.Parameters.AddWithValue("@Unit", string.IsNullOrWhiteSpace(srcItem.Unit) ? "pcs" : srcItem.Unit.Trim());
                        itemCmd.Parameters.AddWithValue("@EstimatedUnitPrice", srcItem.EstimatedUnitPrice.HasValue ? (double)srcItem.EstimatedUnitPrice.Value : (object)DBNull.Value);
                        itemCmd.Parameters.AddWithValue("@Notes", string.IsNullOrWhiteSpace(srcItem.Notes) ? $"From {keptPr.PrNo}" : $"{srcItem.Notes} (From {keptPr.PrNo})");
                        itemCmd.Parameters.AddWithValue("@SortOrder", itemSort++);

                        await itemCmd.ExecuteNonQueryAsync();
                    }
                }
            }

            await tx.CommitAsync();
        }

        public async Task SplitSharedRfqAsync(Guid rfqId)
        {
            await _db.InitializeAsync();
            using var connection = _db.CreateConnection();
            await connection.OpenAsync();
            using var tx = connection.BeginTransaction();

            string? rfqNo = null;
            using (var getCmd = connection.CreateCommand())
            {
                getCmd.Transaction = tx;
                getCmd.CommandText = "SELECT RfqNo FROM RequestForQuotation WHERE Id = @Id;";
                getCmd.Parameters.AddWithValue("@Id", rfqId.ToString());
                var obj = await getCmd.ExecuteScalarAsync();
                rfqNo = obj?.ToString();
            }

            using (var updateCmd = connection.CreateCommand())
            {
                updateCmd.Transaction = tx;
                updateCmd.CommandText = @"
UPDATE RequestForQuotation 
SET SharedPrs = '' 
WHERE Id = @Id OR (@RfqNo IS NOT NULL AND RfqNo = @RfqNo);";
                updateCmd.Parameters.AddWithValue("@Id", rfqId.ToString());
                updateCmd.Parameters.AddWithValue("@RfqNo", (object?)rfqNo ?? DBNull.Value);
                await updateCmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }

        public async Task SplitCombinedPoAsync(Guid poId)
        {
            await _db.InitializeAsync();
            using var connection = _db.CreateConnection();
            await connection.OpenAsync();
            using var tx = connection.BeginTransaction();

            string? poNo = null;
            using (var getCmd = connection.CreateCommand())
            {
                getCmd.Transaction = tx;
                getCmd.CommandText = "SELECT PoNo FROM PurchaseOrder WHERE Id = @Id;";
                getCmd.Parameters.AddWithValue("@Id", poId.ToString());
                var obj = await getCmd.ExecuteScalarAsync();
                poNo = obj?.ToString();
            }

            using (var updateCmd = connection.CreateCommand())
            {
                updateCmd.Transaction = tx;
                updateCmd.CommandText = @"
UPDATE PurchaseOrder 
SET CombinedPrs = '' 
WHERE Id = @Id OR (@PoNo IS NOT NULL AND PoNo = @PoNo);";
                updateCmd.Parameters.AddWithValue("@Id", poId.ToString());
                updateCmd.Parameters.AddWithValue("@PoNo", (object?)poNo ?? DBNull.Value);
                await updateCmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
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
    }
}
