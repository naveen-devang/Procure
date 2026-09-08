using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class ExportRfqSelection : ObservableObject
    {
        public RequestForQuotation Rfq { get; }

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        public string VendorName => string.IsNullOrWhiteSpace(Rfq.Vendor) ? "Unnamed Supplier" : Rfq.Vendor;

        public string DisplaySummary
        {
            get
            {
                var cost = Rfq.FormattedDisplayAmount;
                var rfqNum = Rfq.RfqNo;
                if (!string.IsNullOrWhiteSpace(rfqNum))
                {
                    return $"{VendorName} • {cost} ({rfqNum})";
                }
                return $"{VendorName} • {cost}";
            }
        }

        public ExportRfqSelection(RequestForQuotation rfq, bool isSelected = true)
        {
            Rfq = rfq;
            IsSelected = isSelected;
        }
    }
}
