using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Procure.Models;

namespace Procure.Data.Repositories
{
    // Multi-PR restructuring: merge, bulk RFQ/PO creation, and the split/unmerge family.
    public partial class PurchaseRequisitionRepository
    {
        // Like the Writes partial: SQLite work is synchronous under the async surface, so every
        // public restructure op hops to the thread pool once via Task.Run.
        public Task MergePrsAsync(List<PurchaseRequisition> sourcePrs, PurchaseRequisition masterPr, bool copyRfqs) => Task.Run(() => MergePrsCoreAsync(sourcePrs, masterPr, copyRfqs));

        private async Task MergePrsCoreAsync(List<PurchaseRequisition> sourcePrs, PurchaseRequisition masterPr, bool copyRfqs)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            masterPr.UpdatedAt = DateTime.Now;

            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
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

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
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

                        await itemCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
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

                        await rfqCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

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

                                await rfqItemCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
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

                await srcCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await tx.CommitAsync().ConfigureAwait(false);

            // These move rows between several PRs at once, so rebuild every PR's search text rather
            // than each operation maintaining its own list of which ones it touched.
            await RebuildAllSearchBlobsAsync(connection).ConfigureAwait(false);
        }

        public Task CreateBatchPoAsync(List<PurchaseRequisition> targetPrs, PurchaseOrder poTemplate) => Task.Run(() => CreateBatchPoCoreAsync(targetPrs, poTemplate));

        private async Task CreateBatchPoCoreAsync(List<PurchaseRequisition> targetPrs, PurchaseOrder poTemplate)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
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

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await tx.CommitAsync().ConfigureAwait(false);

            // These move rows between several PRs at once, so rebuild every PR's search text rather
            // than each operation maintaining its own list of which ones it touched.
            await RebuildAllSearchBlobsAsync(connection).ConfigureAwait(false);

            foreach (var pr in targetPrs)
            {
                pr.NotifyHierarchyChanged();
            }
        }

        public Task CreateBatchRfqAsync(List<PurchaseRequisition> targetPrs, RequestForQuotation rfqTemplate, IEnumerable<RfqItem>? batchItems = null) => Task.Run(() => CreateBatchRfqCoreAsync(targetPrs, rfqTemplate, batchItems));

        private async Task CreateBatchRfqCoreAsync(List<PurchaseRequisition> targetPrs, RequestForQuotation rfqTemplate, IEnumerable<RfqItem>? batchItems)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
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

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

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

                    await itemCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }

            await tx.CommitAsync().ConfigureAwait(false);

            // These move rows between several PRs at once, so rebuild every PR's search text rather
            // than each operation maintaining its own list of which ones it touched.
            await RebuildAllSearchBlobsAsync(connection).ConfigureAwait(false);

            foreach (var pr in targetPrs)
            {
                pr.NotifyHierarchyChanged();
            }
        }

        public Task SplitMergedPrAsync(Guid masterPrId) => Task.Run(() => SplitMergedPrCoreAsync(masterPrId));

        private async Task SplitMergedPrCoreAsync(Guid masterPrId)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            using var tx = connection.BeginTransaction();

            // 1. Get the master PR details
            string? consolidatedFrom = null;
            using (var getMasterCmd = connection.CreateCommand())
            {
                getMasterCmd.Transaction = tx;
                getMasterCmd.CommandText = "SELECT ConsolidatedFrom FROM PurchaseRequisition WHERE Id = @Id;";
                getMasterCmd.Parameters.AddWithValue("@Id", masterPrId.ToString());
                var obj = await getMasterCmd.ExecuteScalarAsync().ConfigureAwait(false);
                consolidatedFrom = obj?.ToString();
            }

            // 2. Find and restore child PRs linked to this master PR
            var childIds = new List<string>();
            using (var findCmd = connection.CreateCommand())
            {
                findCmd.Transaction = tx;
                findCmd.CommandText = "SELECT Id FROM PurchaseRequisition WHERE ParentPrId = @ParentId;";
                findCmd.Parameters.AddWithValue("@ParentId", masterPrId.ToString());
                using var reader = await findCmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
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
                    using var rdr = await findByNoCmd.ExecuteReaderAsync().ConfigureAwait(false);
                    while (await rdr.ReadAsync().ConfigureAwait(false))
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
                    var poCount = Convert.ToInt32(await checkPoCmd.ExecuteScalarAsync().ConfigureAwait(false));
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
                        var rfqCount = Convert.ToInt32(await checkRfqCmd.ExecuteScalarAsync().ConfigureAwait(false));
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
                await restoreCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // 4. Delete the master PR and its cascaded data
            using (var delRfqItemsCmd = connection.CreateCommand())
            {
                delRfqItemsCmd.Transaction = tx;
                delRfqItemsCmd.CommandText = "DELETE FROM RfqItem WHERE RfqId IN (SELECT Id FROM RequestForQuotation WHERE PrId = @PrId);";
                delRfqItemsCmd.Parameters.AddWithValue("@PrId", masterPrId.ToString());
                await delRfqItemsCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using (var delRfqsCmd = connection.CreateCommand())
            {
                delRfqsCmd.Transaction = tx;
                delRfqsCmd.CommandText = "DELETE FROM RequestForQuotation WHERE PrId = @PrId;";
                delRfqsCmd.Parameters.AddWithValue("@PrId", masterPrId.ToString());
                await delRfqsCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using (var delPosCmd = connection.CreateCommand())
            {
                delPosCmd.Transaction = tx;
                delPosCmd.CommandText = "DELETE FROM PurchaseOrder WHERE PrId = @PrId;";
                delPosCmd.Parameters.AddWithValue("@PrId", masterPrId.ToString());
                await delPosCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using (var delPcrCmd = connection.CreateCommand())
            {
                delPcrCmd.Transaction = tx;
                delPcrCmd.CommandText = @"
DELETE FROM Approval WHERE PcrId IN (SELECT Id FROM PriceComparisonRequest WHERE PrId = @PrId);
DELETE FROM PriceComparisonRequest WHERE PrId = @PrId;";
                delPcrCmd.Parameters.AddWithValue("@PrId", masterPrId.ToString());
                await delPcrCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using (var delItemsCmd = connection.CreateCommand())
            {
                delItemsCmd.Transaction = tx;
                delItemsCmd.CommandText = "DELETE FROM PrItem WHERE PrId = @PrId;";
                delItemsCmd.Parameters.AddWithValue("@PrId", masterPrId.ToString());
                await delItemsCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using (var delPrCmd = connection.CreateCommand())
            {
                delPrCmd.Transaction = tx;
                delPrCmd.CommandText = "DELETE FROM PurchaseRequisition WHERE Id = @Id;";
                delPrCmd.Parameters.AddWithValue("@Id", masterPrId.ToString());
                await delPrCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await tx.CommitAsync().ConfigureAwait(false);

            // These move rows between several PRs at once, so rebuild every PR's search text rather
            // than each operation maintaining its own list of which ones it touched.
            await RebuildAllSearchBlobsAsync(connection).ConfigureAwait(false);
        }

        public Task PartialSplitMergedPrAsync(Guid masterPrId, List<PurchaseRequisition> splitPrs, List<PurchaseRequisition> keptPrs) => Task.Run(() => PartialSplitMergedPrCoreAsync(masterPrId, splitPrs, keptPrs));

        private async Task PartialSplitMergedPrCoreAsync(Guid masterPrId, List<PurchaseRequisition> splitPrs, List<PurchaseRequisition> keptPrs)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
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
                    var poCount = Convert.ToInt32(await checkPoCmd.ExecuteScalarAsync().ConfigureAwait(false));
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
                        var rfqCount = Convert.ToInt32(await checkRfqCmd.ExecuteScalarAsync().ConfigureAwait(false));
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
                await restoreCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
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
                await updateMasterCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // 3. Rebuild line items on master PR: wipe current and re-insert from kept PRs
            using (var delItemsCmd = connection.CreateCommand())
            {
                delItemsCmd.Transaction = tx;
                delItemsCmd.CommandText = "DELETE FROM PrItem WHERE PrId = @MasterId;";
                delItemsCmd.Parameters.AddWithValue("@MasterId", masterPrId.ToString());
                await delItemsCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
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

                        await itemCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }
            }

            await tx.CommitAsync().ConfigureAwait(false);

            // These move rows between several PRs at once, so rebuild every PR's search text rather
            // than each operation maintaining its own list of which ones it touched.
            await RebuildAllSearchBlobsAsync(connection).ConfigureAwait(false);
        }

        public Task SplitSharedRfqAsync(Guid rfqId) => Task.Run(() => SplitSharedRfqCoreAsync(rfqId));

        private async Task SplitSharedRfqCoreAsync(Guid rfqId)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            using var tx = connection.BeginTransaction();

            string? rfqNo = null;
            using (var getCmd = connection.CreateCommand())
            {
                getCmd.Transaction = tx;
                getCmd.CommandText = "SELECT RfqNo FROM RequestForQuotation WHERE Id = @Id;";
                getCmd.Parameters.AddWithValue("@Id", rfqId.ToString());
                var obj = await getCmd.ExecuteScalarAsync().ConfigureAwait(false);
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
                await updateCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await tx.CommitAsync().ConfigureAwait(false);
            // No search-blob rebuild: SharedPrs is not part of the blob and nothing else changed.
        }

        public Task SplitCombinedPoAsync(Guid poId) => Task.Run(() => SplitCombinedPoCoreAsync(poId));

        private async Task SplitCombinedPoCoreAsync(Guid poId)
        {
            await _db.InitializeAsync().ConfigureAwait(false);
            using var connection = _db.CreateConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            using var tx = connection.BeginTransaction();

            string? poNo = null;
            using (var getCmd = connection.CreateCommand())
            {
                getCmd.Transaction = tx;
                getCmd.CommandText = "SELECT PoNo FROM PurchaseOrder WHERE Id = @Id;";
                getCmd.Parameters.AddWithValue("@Id", poId.ToString());
                var obj = await getCmd.ExecuteScalarAsync().ConfigureAwait(false);
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
                await updateCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await tx.CommitAsync().ConfigureAwait(false);
            // No search-blob rebuild: CombinedPrs is not part of the blob and nothing else changed.
        }
    }
}
