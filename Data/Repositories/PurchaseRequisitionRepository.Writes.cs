using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Procure.Models;

namespace Procure.Data.Repositories
{
    // Single-entity writes: PR, RFQ, PCR/approval and PO, plus the shared UPSERT helpers.
    public partial class PurchaseRequisitionRepository
    {
        // UPSERTs the PurchaseRequisition row only. Shared by SaveAsync (inside its transaction)
        // and SavePrFieldsAsync (standalone), so the UPSERT SQL exists exactly once.
        private static async Task UpsertPrRowAsync(SqliteConnection connection, SqliteTransaction? tx, PurchaseRequisition pr)
        {
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

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // Scalar-field-only save: writes the PurchaseRequisition row and nothing else.
        // Use instead of SaveAsync when Items and CustomValues are untouched.
        // Microsoft.Data.Sqlite's *Async methods run synchronously, so every public write method here
        // and in the Restructure partial hops to the thread pool once via Task.Run; page models can
        // await them straight from the UI thread without blocking the dispatcher.
        public Task SavePrFieldsAsync(PurchaseRequisition pr) => Task.Run(() => SavePrFieldsCoreAsync(pr));

        private async Task SavePrFieldsCoreAsync(PurchaseRequisition pr)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            pr.UpdatedAt = DateTime.Now;

            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            await UpsertPrRowAsync(connection, null, pr).ConfigureAwait(false);
            await RefreshSearchBlobAsync(connection, null, pr.Id).ConfigureAwait(false);
        }

        /// <summary>
        /// Recomputes one PR's denormalised search text after a write that could have changed anything
        /// it covers. A single statement over three indexed child lookups - sub-millisecond. Every write
        /// path that touches a PR, its items, its RFQs or its POs has to end in one of these or search
        /// silently goes stale for that PR; SearchBlobStaysFresh in DatabaseSelfCheck is what catches it.
        /// PCR and approval writes deliberately do not, because the blob does not cover them.
        /// </summary>
        private static async Task RefreshSearchBlobAsync(SqliteConnection connection, SqliteTransaction? tx, Guid prId)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = DatabaseConstants.SqlRebuildSearchBlob + " WHERE Id = @BlobPrId;";
            cmd.Parameters.AddWithValue("@BlobPrId", prId.ToString());
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        /// <summary>Rebuilds the search text for every PR. The restructure operations move rows between
        /// several PRs at once, so this is one statement instead of each of them having to enumerate
        /// exactly which PRs it touched - the kind of list that goes stale silently.
        /// ponytail: whole-table rebuild, ~240ms at 20,000 PRs. These are deliberate operations behind a
        /// confirmation dialog, so the simpler form wins; narrow it to the affected ids if that bites.</summary>
        private static async Task RebuildAllSearchBlobsAsync(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = DatabaseConstants.SqlRebuildSearchBlob + ";";
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }


        /// <summary>Syncs a PR's item rows to match <paramref name="pr"/>.Items: departed rows are
        /// deleted, survivors UPSERTed under their existing Ids. Deleting and reinserting instead would
        /// fire the ON DELETE SET NULL on RfqItem.PrItemId / PurchaseOrderItem.PrItemId and sever those
        /// links permanently — FK actions run at delete time, so reinserting the same Guid does not
        /// restore them. Callers that only change scalar PR fields must use SavePrFieldsAsync.</summary>
        private static async Task SyncPrItemsAsync(SqliteConnection connection, SqliteTransaction tx, PurchaseRequisition pr)
        {
            var kept = new List<PrItem>();
            if (pr.Items != null)
            {
                foreach (var item in pr.Items)
                {
                    if (string.IsNullOrWhiteSpace(item.ItemName)) continue;
                    item.PrId = pr.Id;
                    item.SortOrder = kept.Count;
                    kept.Add(item);
                }
            }

            await DeleteDepartedChildrenAsync(connection, tx, "PrItem", "PrId", pr.Id, kept.Select(i => i.Id).ToList()).ConfigureAwait(false);

            foreach (var item in kept)
            {
                using var insItemCmd = connection.CreateCommand();
                insItemCmd.Transaction = tx;
                insItemCmd.CommandText = @"
INSERT INTO PrItem (Id, PrId, ItemName, Quantity, Unit, EstimatedUnitPrice, Notes, SortOrder)
VALUES (@Id, @PrId, @ItemName, @Quantity, @Unit, @EstimatedUnitPrice, @Notes, @SortOrder)
ON CONFLICT(Id) DO UPDATE SET
    PrId = excluded.PrId,
    ItemName = excluded.ItemName,
    Quantity = excluded.Quantity,
    Unit = excluded.Unit,
    EstimatedUnitPrice = excluded.EstimatedUnitPrice,
    Notes = excluded.Notes,
    SortOrder = excluded.SortOrder;";

                insItemCmd.Parameters.AddWithValue("@Id", item.Id.ToString());
                insItemCmd.Parameters.AddWithValue("@PrId", item.PrId.ToString());
                insItemCmd.Parameters.AddWithValue("@ItemName", item.ItemName.Trim());
                insItemCmd.Parameters.AddWithValue("@Quantity", (double)item.Quantity);
                insItemCmd.Parameters.AddWithValue("@Unit", string.IsNullOrWhiteSpace(item.Unit) ? "pcs" : item.Unit.Trim());
                insItemCmd.Parameters.AddWithValue("@EstimatedUnitPrice", item.EstimatedUnitPrice.HasValue ? (double)item.EstimatedUnitPrice.Value : (object)DBNull.Value);
                insItemCmd.Parameters.AddWithValue("@Notes", item.Notes ?? string.Empty);
                insItemCmd.Parameters.AddWithValue("@SortOrder", item.SortOrder);

                await insItemCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        public Task SaveAsync(PurchaseRequisition pr) => Task.Run(() => SaveCoreAsync(pr));

        private async Task SaveCoreAsync(PurchaseRequisition pr)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            pr.UpdatedAt = DateTime.Now;

            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            using var tx = connection.BeginTransaction();

            await UpsertPrRowAsync(connection, tx, pr).ConfigureAwait(false);

            await SyncPrItemsAsync(connection, tx, pr).ConfigureAwait(false);
            if (pr.CustomValues.Count > 0)
            {
                await CustomColumnRepository.WriteValuesForPrAsync(connection, tx, pr.Id, pr.CustomValues).ConfigureAwait(false);
            }
            await RefreshSearchBlobAsync(connection, tx, pr.Id).ConfigureAwait(false);

            await tx.CommitAsync().ConfigureAwait(false);
        }

        public Task SaveBatchPrsAsync(List<PurchaseRequisition> prs) => Task.Run(() => SaveBatchPrsCoreAsync(prs));

        private async Task SaveBatchPrsCoreAsync(List<PurchaseRequisition> prs)
        {
            if (prs == null || prs.Count == 0) return;

            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            using var tx = connection.BeginTransaction();

            foreach (var pr in prs)
            {
                pr.UpdatedAt = DateTime.Now;
                await UpsertPrRowAsync(connection, tx, pr).ConfigureAwait(false);
                await SyncPrItemsAsync(connection, tx, pr).ConfigureAwait(false);
                if (pr.CustomValues.Count > 0)
                {
                    await CustomColumnRepository.WriteValuesForPrAsync(connection, tx, pr.Id, pr.CustomValues).ConfigureAwait(false);
                }
                await RefreshSearchBlobAsync(connection, tx, pr.Id).ConfigureAwait(false);
            }

            await tx.CommitAsync().ConfigureAwait(false);
        }

        public Task DeleteAsync(Guid id) => Task.Run(() => DeleteCoreAsync(id));

        private async Task DeleteCoreAsync(Guid id)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM PurchaseRequisition WHERE Id = @Id;";
            cmd.Parameters.AddWithValue("@Id", id.ToString());

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        // Deletes only the child rows that are gone from memory, so an unchanged child set writes nothing.
        // Table and column names are compile-time constants; every value is parameterised via BindIdList.
        private static async Task DeleteDepartedChildrenAsync(SqliteConnection connection, SqliteTransaction tx, string table, string parentColumn, Guid parentId, IReadOnlyList<Guid> keepIds)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("@ParentId", parentId.ToString());

            // NOT IN () is not valid SQL, so an empty keep set is its own statement rather than an
            // empty placeholder list.
            cmd.CommandText = keepIds.Count == 0
                ? $"DELETE FROM {table} WHERE {parentColumn} = @ParentId;"
                : $"DELETE FROM {table} WHERE {parentColumn} = @ParentId AND Id NOT IN ({BindIdList(cmd, "@Keep", keepIds)});";

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public Task SaveRfqAsync(RequestForQuotation rfq) => Task.Run(() => SaveRfqCoreAsync(rfq));

        private async Task SaveRfqCoreAsync(RequestForQuotation rfq)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
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

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // Save RfqItems
            if (rfq.Items != null)
            {
                await DeleteDepartedChildrenAsync(connection, tx, "RfqItem", "RfqId", rfq.Id, rfq.Items.Select(i => i.Id).ToList()).ConfigureAwait(false);

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

                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }

            await RefreshSearchBlobAsync(connection, tx, rfq.PrId).ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
        }

        public Task DeleteRfqAsync(Guid rfqId) => Task.Run(() => DeleteRfqCoreAsync(rfqId));

        private async Task DeleteRfqCoreAsync(Guid rfqId)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            // Read the parent before the row goes, so its search text can be rebuilt afterwards.
            Guid prId;
            using (var lookup = connection.CreateCommand())
            {
                lookup.CommandText = "SELECT PrId FROM RequestForQuotation WHERE Id = @Id;";
                lookup.Parameters.AddWithValue("@Id", rfqId.ToString());
                var found = await lookup.ExecuteScalarAsync().ConfigureAwait(false);
                if (found is null or DBNull) return;
                prId = Guid.Parse((string)found);
            }

            using var tx = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM RequestForQuotation WHERE Id = @Id;";
                cmd.Parameters.AddWithValue("@Id", rfqId.ToString());
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await RefreshSearchBlobAsync(connection, tx, prId).ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
        }

        public Task SavePcrAsync(PriceComparisonRequest pcr) => Task.Run(() => SavePcrCoreAsync(pcr));

        private async Task SavePcrCoreAsync(PriceComparisonRequest pcr)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
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

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            pcr.EnsureDefaultApprovals();

            // Delete any approvals removed from memory
            if (pcr.Approvals.Count > 0)
            {
                await DeleteDepartedChildrenAsync(connection, tx, "Approval", "PcrId", pcr.Id, pcr.Approvals.Select(a => a.Id).ToList()).ConfigureAwait(false);
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

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await tx.CommitAsync().ConfigureAwait(false);
        }

        public Task UpdateApprovalAsync(Approval approval) => Task.Run(() => UpdateApprovalCoreAsync(approval));

        private async Task UpdateApprovalCoreAsync(Approval approval)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

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

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public Task SavePoAsync(PurchaseOrder po) => Task.Run(() => SavePoCoreAsync(po));

        private async Task SavePoCoreAsync(PurchaseOrder po)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            using var tx = connection.BeginTransaction();

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO PurchaseOrder (Id, PrId, PoNo, Vendor, LinkedRfqId, Value, Status, Date, CombinedPrs, Currency, BaseAmount, Freight, OtherCharges, Discount, VatType, TransportContractNumber, TransporterName, TransportRatePerUnit, TransportTotal)
VALUES (@Id, @PrId, @PoNo, @Vendor, @LinkedRfqId, @Value, @Status, @Date, @CombinedPrs, @Currency, @BaseAmount, @Freight, @OtherCharges, @Discount, @VatType, @TransportContractNumber, @TransporterName, @TransportRatePerUnit, @TransportTotal)
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
    VatType = excluded.VatType,
    TransportContractNumber = excluded.TransportContractNumber,
    TransporterName = excluded.TransporterName,
    TransportRatePerUnit = excluded.TransportRatePerUnit,
    TransportTotal = excluded.TransportTotal;";

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
                cmd.Parameters.AddWithValue("@TransportContractNumber", (object?)po.TransportContractNumber ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@TransporterName", (object?)po.TransporterName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@TransportRatePerUnit", po.TransportRatePerUnit.HasValue ? (object)po.TransportRatePerUnit.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@TransportTotal", po.TransportTotal.HasValue ? (object)po.TransportTotal.Value : DBNull.Value);

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                // Save PO Items: drop only the departed rows, then UPSERT the survivors, so a PO
                // whose items did not change writes no item rows at all.
                // Line order is persisted in SortOrder, so it survives the switch from
                // delete-and-reinsert to UPSERT.
                await DeleteDepartedChildrenAsync(connection, tx, "PurchaseOrderItem", "PoId", po.Id, po.Items?.Select(i => i.Id).ToList() ?? new List<Guid>()).ConfigureAwait(false);

                if (po.Items != null && po.Items.Count > 0)
                {
                    int poSortOrder = 0;
                    foreach (var item in po.Items)
                    {
                        item.SortOrder = poSortOrder++;
                        using var itemCmd = connection.CreateCommand();
                        itemCmd.Transaction = tx;
                        itemCmd.CommandText = @"
INSERT INTO PurchaseOrderItem (Id, PoId, PrItemId, RfqItemId, ItemName, Quantity, Unit, UnitPrice, Discount, LineTotal, SortOrder)
VALUES (@Id, @PoId, @PrItemId, @RfqItemId, @ItemName, @Quantity, @Unit, @UnitPrice, @Discount, @LineTotal, @SortOrder)
ON CONFLICT(Id) DO UPDATE SET
    PoId = excluded.PoId,
    PrItemId = excluded.PrItemId,
    RfqItemId = excluded.RfqItemId,
    ItemName = excluded.ItemName,
    Quantity = excluded.Quantity,
    Unit = excluded.Unit,
    UnitPrice = excluded.UnitPrice,
    Discount = excluded.Discount,
    LineTotal = excluded.LineTotal,
    SortOrder = excluded.SortOrder;";

                        itemCmd.Parameters.AddWithValue("@SortOrder", item.SortOrder);
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

                        await itemCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }

                await RefreshSearchBlobAsync(connection, tx, po.PrId).ConfigureAwait(false);
                await tx.CommitAsync().ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync().ConfigureAwait(false);
                throw;
            }
        }

        public Task DeletePoAsync(Guid poId) => Task.Run(() => DeletePoCoreAsync(poId));

        private async Task DeletePoCoreAsync(Guid poId)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);

            Guid prId;
            using (var lookup = connection.CreateCommand())
            {
                lookup.CommandText = "SELECT PrId FROM PurchaseOrder WHERE Id = @Id;";
                lookup.Parameters.AddWithValue("@Id", poId.ToString());
                var found = await lookup.ExecuteScalarAsync().ConfigureAwait(false);
                if (found is null or DBNull) return;
                prId = Guid.Parse((string)found);
            }

            using var tx = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM PurchaseOrder WHERE Id = @Id;";
                cmd.Parameters.AddWithValue("@Id", poId.ToString());
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await RefreshSearchBlobAsync(connection, tx, prId).ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
        }
    }
}
