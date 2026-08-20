using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Procure.Data.Repositories;
using Procure.Models;

namespace Procure.Data
{
    public class SeedDataService
    {
        private readonly IPurchaseRequisitionRepository _prRepo;
        private readonly ICustomColumnRepository _customColumnRepo;

        public SeedDataService(
            IPurchaseRequisitionRepository prRepo,
            ICustomColumnRepository customColumnRepo)
        {
            _prRepo = prRepo;
            _customColumnRepo = customColumnRepo;
        }

        public async Task EnsureDataSeededAsync()
        {
            // Use lightweight count check — avoids loading all PR data just to test for existence
            var count = await _prRepo.GetCountAsync();
            if (count > 0) return;

            // 1. Seed Custom Columns
            var colDept = new CustomColumnDefinition
            {
                Id = Guid.NewGuid(),
                Name = "Department",
                DataType = CustomFieldDataType.Select,
                SelectOptions = "IT,Engineering,Operations,Finance,HR,Facilities",
                SortOrder = 1
            };

            var colCostCenter = new CustomColumnDefinition
            {
                Id = Guid.NewGuid(),
                Name = "Cost Center",
                DataType = CustomFieldDataType.Text,
                SortOrder = 2
            };

            var colGlAccount = new CustomColumnDefinition
            {
                Id = Guid.NewGuid(),
                Name = "GL Account",
                DataType = CustomFieldDataType.Text,
                SortOrder = 3
            };

            var colTargetDelivery = new CustomColumnDefinition
            {
                Id = Guid.NewGuid(),
                Name = "Required By Date",
                DataType = CustomFieldDataType.Date,
                SortOrder = 4
            };

            await _customColumnRepo.SaveDefinitionAsync(colDept);
            await _customColumnRepo.SaveDefinitionAsync(colCostCenter);
            await _customColumnRepo.SaveDefinitionAsync(colGlAccount);
            await _customColumnRepo.SaveDefinitionAsync(colTargetDelivery);

            // 2. Seed PR 1: Urgent Overdue, PCR Pending
            var pr1 = new PurchaseRequisition
            {
                Id = Guid.NewGuid(),
                PrNo = "PR-2026-001",
                Description = "High-Performance Workstations (10x) for ML Team",
                Requestor = "Sarah Lin",
                Plant = ProcurementPlant.RW01,
                Priority = ProcurementPriority.Urgent,
                Status = ProcurementStatus.PcrSubmitted,
                Notes = "Required for AI model training cluster upgrade before Q3 deployment.",
                CreatedAt = DateTime.Now.AddDays(-6),
                UpdatedAt = DateTime.Now.AddDays(-1)
            };

            var rfq1A = new RequestForQuotation
            {
                Id = Guid.NewGuid(),
                PrId = pr1.Id,
                RfqNo = "RFQ-001-A",
                Vendor = "Dell Enterprise Systems",
                Status = RfqStatus.QuoteReceived,
                SentDate = DateTime.Now.AddDays(-5),
                QuoteReceivedDate = DateTime.Now.AddDays(-3),
                Currency = "AED",
                QuoteAmount = 24500m,
                PaymentTerms = "30 Days Net",
                VatType = "5%",
                Freight = 350m,
                OtherCharges = 0m,
                Incoterms = "DDP",
                DeliveryLeadTime = "2 Weeks"
            };

            var rfq1B = new RequestForQuotation
            {
                Id = Guid.NewGuid(),
                PrId = pr1.Id,
                RfqNo = "RFQ-001-B",
                Vendor = "Lenovo Commercial Direct",
                Status = RfqStatus.QuoteReceived,
                SentDate = DateTime.Now.AddDays(-5),
                QuoteReceivedDate = DateTime.Now.AddDays(-2),
                Currency = "AED",
                QuoteAmount = 23800m,
                PaymentTerms = "100% Advance",
                VatType = "5%",
                Freight = 500m,
                OtherCharges = 0m,
                Incoterms = "CIF",
                DeliveryLeadTime = "3-4 Weeks"
            };

            var rfq1C = new RequestForQuotation
            {
                Id = Guid.NewGuid(),
                PrId = pr1.Id,
                RfqNo = "RFQ-001-C",
                Vendor = "HP Enterprise Solutions",
                Status = RfqStatus.QuoteReceived,
                SentDate = DateTime.Now.AddDays(-5),
                QuoteReceivedDate = DateTime.Now.AddDays(-3),
                Currency = "AED",
                QuoteAmount = 25200m,
                PaymentTerms = "50% Adv / 50% Del",
                VatType = "RC",
                Freight = 0m,
                OtherCharges = 0m,
                Incoterms = "DAP",
                DeliveryLeadTime = "10 Days"
            };

            var pcr1 = new PriceComparisonRequest
            {
                Id = Guid.NewGuid(),
                PrId = pr1.Id,
                PcrNo = "PCR-2026-001",
                CreatedAt = DateTime.Now.AddDays(-2),
                Approvals = new ObservableCollection<Approval>
                {
                    new Approval { Id = Guid.NewGuid(), Role = ApprovalRoles.ProcurementManager, Signed = true, SignedByName = "David Miller", SentDate = DateTime.Now.AddDays(-2), ReceivedDate = DateTime.Now.AddDays(-1), SignedDate = DateTime.Now.AddDays(-1), SortOrder = 0, RequiresMultipleDates = true },
                    new Approval { Id = Guid.NewGuid(), Role = ApprovalRoles.FinanceController, Signed = false, SignedByName = null, SentDate = DateTime.Now.AddDays(-1), ReceivedDate = null, SignedDate = null, SortOrder = 1, RequiresMultipleDates = true },
                    new Approval { Id = Guid.NewGuid(), Role = ApprovalRoles.Cfo, Signed = false, SignedByName = null, SentDate = null, ReceivedDate = null, SignedDate = null, SortOrder = 2, RequiresMultipleDates = true },
                    new Approval { Id = Guid.NewGuid(), Role = ApprovalRoles.Ceo, Signed = false, SignedByName = null, SentDate = null, ReceivedDate = null, SignedDate = null, SortOrder = 3, RequiresMultipleDates = true }
                }
            };
            foreach (var a in pcr1.Approvals) a.PcrId = pcr1.Id;

            pr1.Rfqs = new ObservableCollection<RequestForQuotation> { rfq1A, rfq1B, rfq1C };
            pr1.Pcr = pcr1;
            pr1.Items = new ObservableCollection<PrItem>
            {
                new PrItem { Id = Guid.NewGuid(), PrId = pr1.Id, ItemName = "Precision Tower 7920 Workstation", Quantity = 6, Unit = "units", EstimatedUnitPrice = 3200m, SortOrder = 0 },
                new PrItem { Id = Guid.NewGuid(), PrId = pr1.Id, ItemName = "UltraSharp 32-inch 4K Monitor", Quantity = 12, Unit = "units", EstimatedUnitPrice = 450m, SortOrder = 1 }
            };
            pr1.CustomValues = new ObservableCollection<CustomFieldValue>
            {
                new CustomFieldValue { ColumnId = colDept.Id, Value = "Engineering" },
                new CustomFieldValue { ColumnId = colCostCenter.Id, Value = "CC-ENG-402" },
                new CustomFieldValue { ColumnId = colGlAccount.Id, Value = "GL-5100-CAPEX" },
                new CustomFieldValue { ColumnId = colTargetDelivery.Id, Value = DateTime.Today.AddDays(14).ToString("yyyy-MM-dd") }
            };

            await _prRepo.SaveAsync(pr1);
            await _prRepo.SaveRfqAsync(rfq1A);
            await _prRepo.SaveRfqAsync(rfq1B);
            await _prRepo.SaveRfqAsync(rfq1C);
            await _prRepo.SavePcrAsync(pcr1);

            // 3. Seed PR 2: PO Raised
            var pr2 = new PurchaseRequisition
            {
                Id = Guid.NewGuid(),
                PrNo = "PR-2026-002",
                Description = "Cloud Rack Servers & High-Speed Optical Switches",
                Requestor = "Alex Rivera",
                Plant = ProcurementPlant.NO01,
                Priority = ProcurementPriority.Normal,
                Status = ProcurementStatus.PoRaised,
                Notes = "Data center capacity expansion phase 2.",
                CreatedAt = DateTime.Now.AddDays(-8),
                UpdatedAt = DateTime.Now.AddDays(-1)
            };

            var rfq2A = new RequestForQuotation
            {
                Id = Guid.NewGuid(),
                PrId = pr2.Id,
                RfqNo = "RFQ-002-A",
                Vendor = "Cisco Systems Direct",
                Status = RfqStatus.QuoteReceived,
                SentDate = DateTime.Now.AddDays(-7),
                QuoteReceivedDate = DateTime.Now.AddDays(-5),
                QuoteAmount = 45000m
            };

            var pcr2 = new PriceComparisonRequest
            {
                Id = Guid.NewGuid(),
                PrId = pr2.Id,
                PcrNo = "PCR-2026-002",
                CreatedAt = DateTime.Now.AddDays(-4),
                Approvals = new ObservableCollection<Approval>
                {
                    new Approval { Id = Guid.NewGuid(), Role = ApprovalRoles.ProcurementManager, Signed = true, SignedByName = "David Miller", SentDate = DateTime.Now.AddDays(-4), ReceivedDate = DateTime.Now.AddDays(-3), SignedDate = DateTime.Now.AddDays(-3), SortOrder = 0, RequiresMultipleDates = true },
                    new Approval { Id = Guid.NewGuid(), Role = ApprovalRoles.FinanceController, Signed = true, SignedByName = "Alex Rivera", SentDate = DateTime.Now.AddDays(-3), ReceivedDate = DateTime.Now.AddDays(-2), SignedDate = DateTime.Now.AddDays(-2), SortOrder = 1, RequiresMultipleDates = true },
                    new Approval { Id = Guid.NewGuid(), Role = ApprovalRoles.Cfo, Signed = true, SignedByName = "Rachel Zane", SentDate = DateTime.Now.AddDays(-3), ReceivedDate = DateTime.Now.AddDays(-2), SignedDate = DateTime.Now.AddDays(-2), SortOrder = 2, RequiresMultipleDates = true },
                    new Approval { Id = Guid.NewGuid(), Role = ApprovalRoles.Ceo, Signed = true, SignedByName = "Jonathan Ross", SentDate = DateTime.Now.AddDays(-2), ReceivedDate = DateTime.Now.AddDays(-1), SignedDate = DateTime.Now.AddDays(-1), SortOrder = 3, RequiresMultipleDates = true }
                }
            };
            foreach (var a in pcr2.Approvals) a.PcrId = pcr2.Id;

            var po2 = new PurchaseOrder
            {
                Id = Guid.NewGuid(),
                PrId = pr2.Id,
                PoNo = "PO-2026-881",
                Vendor = "Cisco Systems Direct",
                LinkedRfqId = rfq2A.Id,
                Value = 45000m,
                Status = PoStatus.Raised,
                Date = DateTime.Now.AddDays(-1)
            };

            pr2.Rfqs = new ObservableCollection<RequestForQuotation> { rfq2A };
            pr2.Pcr = pcr2;
            pr2.Pos = new ObservableCollection<PurchaseOrder> { po2 };
            pr2.CustomValues = new ObservableCollection<CustomFieldValue>
            {
                new CustomFieldValue { ColumnId = colDept.Id, Value = "IT" },
                new CustomFieldValue { ColumnId = colCostCenter.Id, Value = "CC-IT-101" },
                new CustomFieldValue { ColumnId = colGlAccount.Id, Value = "GL-5200-INFRA" }
            };

            await _prRepo.SaveAsync(pr2);
            await _prRepo.SaveRfqAsync(rfq2A);
            await _prRepo.SavePcrAsync(pcr2);
            await _prRepo.SavePoAsync(po2);

            // 4. Seed PR 3: RFQ Sent, Quotes Pending
            var pr3 = new PurchaseRequisition
            {
                Id = Guid.NewGuid(),
                PrNo = "PR-2026-003",
                Description = "Ergonomic Desk Chairs (25 units) for New Office Wing",
                Requestor = "Emma Watson",
                Plant = ProcurementPlant.RW01,
                Priority = ProcurementPriority.Normal,
                Status = ProcurementStatus.RfqSent,
                Notes = "Standardized sit-stand compatible ergonomic chairs.",
                CreatedAt = DateTime.Now.AddDays(-3),
                UpdatedAt = DateTime.Now.AddDays(-2)
            };

            var rfq3A = new RequestForQuotation
            {
                Id = Guid.NewGuid(),
                PrId = pr3.Id,
                RfqNo = "RFQ-003-A",
                Vendor = "Herman Miller Corporate",
                Status = RfqStatus.Sent,
                SentDate = DateTime.Now.AddDays(-2)
            };

            var rfq3B = new RequestForQuotation
            {
                Id = Guid.NewGuid(),
                PrId = pr3.Id,
                RfqNo = "RFQ-003-B",
                Vendor = "Steelcase Solutions",
                Status = RfqStatus.Sent,
                SentDate = DateTime.Now.AddDays(-2)
            };

            pr3.Rfqs = new ObservableCollection<RequestForQuotation> { rfq3A, rfq3B };
            pr3.CustomValues = new ObservableCollection<CustomFieldValue>
            {
                new CustomFieldValue { ColumnId = colDept.Id, Value = "Facilities" },
                new CustomFieldValue { ColumnId = colCostCenter.Id, Value = "CC-FAC-300" }
            };

            await _prRepo.SaveAsync(pr3);
            await _prRepo.SaveRfqAsync(rfq3A);
            await _prRepo.SaveRfqAsync(rfq3B);

            // 5. Seed PR 4: Delivered
            var pr4 = new PurchaseRequisition
            {
                Id = Guid.NewGuid(),
                PrNo = "PR-2026-004",
                Description = "Factory Calibration Sensors & High-Precision Actuators",
                Requestor = "Marcus Vance",
                Plant = ProcurementPlant.MF01,
                Priority = ProcurementPriority.Urgent,
                Status = ProcurementStatus.Delivered,
                Notes = "Critical sensor calibration for Production Line A.",
                CreatedAt = DateTime.Now.AddDays(-14),
                UpdatedAt = DateTime.Now.AddDays(-1)
            };

            var rfq4A = new RequestForQuotation
            {
                Id = Guid.NewGuid(),
                PrId = pr4.Id,
                RfqNo = "RFQ-004-A",
                Vendor = "Siemens Automation & Drives",
                Status = RfqStatus.QuoteReceived,
                SentDate = DateTime.Now.AddDays(-12),
                QuoteReceivedDate = DateTime.Now.AddDays(-10),
                QuoteAmount = 18400m
            };

            var po4 = new PurchaseOrder
            {
                Id = Guid.NewGuid(),
                PrId = pr4.Id,
                PoNo = "PO-2026-724",
                Vendor = "Siemens Automation & Drives",
                LinkedRfqId = rfq4A.Id,
                Value = 18400m,
                Status = PoStatus.Delivered,
                Date = DateTime.Now.AddDays(-8)
            };

            pr4.Rfqs = new ObservableCollection<RequestForQuotation> { rfq4A };
            pr4.Pos = new ObservableCollection<PurchaseOrder> { po4 };
            pr4.CustomValues = new ObservableCollection<CustomFieldValue>
            {
                new CustomFieldValue { ColumnId = colDept.Id, Value = "Operations" },
                new CustomFieldValue { ColumnId = colCostCenter.Id, Value = "CC-OPS-202" }
            };

            await _prRepo.SaveAsync(pr4);
            await _prRepo.SaveRfqAsync(rfq4A);
            await _prRepo.SavePoAsync(po4);

            // 6. Seed PR 5: Quotes Received
            var pr5 = new PurchaseRequisition
            {
                Id = Guid.NewGuid(),
                PrNo = "PR-2026-005",
                Description = "Endpoint Security & Threat Intelligence Annual Subscription",
                Requestor = "David Chen",
                Plant = ProcurementPlant.NO01,
                Priority = ProcurementPriority.Normal,
                Status = ProcurementStatus.QuotesReceived,
                Notes = "Annual renewal with expanded EDR coverage for remote workers.",
                CreatedAt = DateTime.Now.AddDays(-4),
                UpdatedAt = DateTime.Now.AddDays(-1)
            };

            var rfq5A = new RequestForQuotation
            {
                Id = Guid.NewGuid(),
                PrId = pr5.Id,
                RfqNo = "RFQ-005-A",
                Vendor = "CrowdStrike Falcon Enterprise",
                Status = RfqStatus.QuoteReceived,
                SentDate = DateTime.Now.AddDays(-4),
                QuoteReceivedDate = DateTime.Now.AddDays(-1),
                QuoteAmount = 12500m
            };

            var rfq5B = new RequestForQuotation
            {
                Id = Guid.NewGuid(),
                PrId = pr5.Id,
                RfqNo = "RFQ-005-B",
                Vendor = "SentinelOne Singularity Platform",
                Status = RfqStatus.QuoteReceived,
                SentDate = DateTime.Now.AddDays(-4),
                QuoteReceivedDate = DateTime.Now.AddDays(-2),
                QuoteAmount = 11900m
            };

            pr5.Rfqs = new ObservableCollection<RequestForQuotation> { rfq5A, rfq5B };
            pr5.CustomValues = new ObservableCollection<CustomFieldValue>
            {
                new CustomFieldValue { ColumnId = colDept.Id, Value = "IT" },
                new CustomFieldValue { ColumnId = colCostCenter.Id, Value = "CC-IT-101" }
            };

            await _prRepo.SaveAsync(pr5);
            await _prRepo.SaveRfqAsync(rfq5A);
            await _prRepo.SaveRfqAsync(rfq5B);
        }
    }
}