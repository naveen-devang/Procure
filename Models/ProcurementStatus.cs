namespace Procure.Models
{
    public static class ProcurementStatus
    {
        public const string PrRaised = "PR Raised";
        public const string RfqSent = "RFQ Sent";
        public const string QuotesReceived = "Quotes Received";
        public const string PcrSubmitted = "PCR Submitted";
        public const string PcrApproved = "PCR Approved";
        public const string PoRaised = "PO Raised";
        public const string PartiallyDelivered = "Partially Delivered";
        public const string Delivered = "Delivered";
        public const string Closed = "Closed";
        public const string Merged = "Merged";
        public const string OnHold = "On Hold";
        public const string Cancelled = "Cancelled";

        public static readonly string[] AllStatuses =
        [
            PrRaised,
            RfqSent,
            QuotesReceived,
            PcrSubmitted,
            PcrApproved,
            PoRaised,
            PartiallyDelivered,
            Delivered,
            Closed,
            Merged,
            OnHold,
            Cancelled
        ];
    }

    public static class ProcurementPriority
    {
        public const string Normal = "Normal";
        public const string Urgent = "Urgent";

        public static readonly string[] AllPriorities =
        [
            Normal,
            Urgent
        ];
    }

    public static class ProcurementPlant
    {
        public const string RW01 = "RW01";
        public const string NO01 = "NO01";
        public const string MF01 = "MF01";

        public static readonly string[] AllPlants =
        [
            RW01,
            NO01,
            MF01
        ];
    }

    public static class ProcurementPrType
    {
        public const string StoresAndSpares = "Stores&Spares";
        public const string RawMaterial = "Raw Material";
        public const string PackingMaterial = "Packing Material";
        public const string Service = "Service";
        public const string Capex = "Capex";

        public static readonly string[] AllPrTypes =
        [
            StoresAndSpares,
            RawMaterial,
            PackingMaterial,
            Service,
            Capex
        ];
    }

    public static class ApprovalRoles
    {
        public const string ProcurementManager = "Procurement Manager";
        public const string FinanceController = "Finance Controller";
        public const string Cfo = "CFO";
        public const string Ceo = "CEO";
    }

    public static class RfqStatus
    {
        public const string Sent = "Sent";
        public const string QuoteReceived = "Quote Received";

        public static readonly string[] AllStatuses = [Sent, QuoteReceived];
    }

    public static class PoStatus
    {
        public const string Raised = "Raised";
        public const string Delivered = "Delivered";
        public const string Closed = "Closed";

        public static readonly string[] AllStatuses = [Raised, Delivered, Closed];
    }

    public static class CustomFieldDataType
    {
        public const string Text = "Text";
        public const string Number = "Number";
        public const string Date = "Date";
        public const string Select = "Select";

        public static readonly string[] All = [Text, Number, Date, Select];
    }
}
