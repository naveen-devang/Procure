using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class Approval : ObservableObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid PcrId { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RoleDisplayName))]
        public partial string Role { get; set; } = ApprovalRoles.ProcurementManager;

        [ObservableProperty]
        public partial string? SignedByName { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasReceivedDate))]
        [NotifyPropertyChangedFor(nameof(IsReceived))]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        public partial bool Signed { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        public partial DateTime? SignedDate { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSentDate))]
        [NotifyPropertyChangedFor(nameof(IsSent))]
        [NotifyPropertyChangedFor(nameof(SentDatePickerValue))]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        public partial DateTime? SentDate { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasReceivedDate))]
        [NotifyPropertyChangedFor(nameof(IsReceived))]
        [NotifyPropertyChangedFor(nameof(ReceivedDatePickerValue))]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        public partial DateTime? ReceivedDate { get; set; }

        [ObservableProperty]
        public partial int SortOrder { get; set; }

        [ObservableProperty]
        public partial bool RequiresMultipleDates { get; set; } = true;

        [ObservableProperty]
        public partial bool IsIncluded { get; set; } = true;

        public bool HasSentDate => SentDate.HasValue;
        public bool HasReceivedDate => ReceivedDate.HasValue || (Signed && SignedDate.HasValue);
        public bool IsReceived => ReceivedDate.HasValue || (Signed && SignedDate.HasValue);
        public bool IsSent => SentDate.HasValue;

        public DateTime SentDatePickerValue
        {
            get => SentDate ?? DateTime.Today;
            set
            {
                if (SentDate != value)
                {
                    SentDate = value;
                    NotifyPropertiesChanged();
                }
            }
        }

        public DateTime ReceivedDatePickerValue
        {
            get => ReceivedDate ?? (SignedDate ?? DateTime.Today);
            set
            {
                if (ReceivedDate != value)
                {
                    ReceivedDate = value;
                    Signed = true;
                    SignedDate = value;
                    NotifyPropertiesChanged();
                }
            }
        }

        public string RoleDisplayName => Role switch
        {
            "ProcurementManager" => "Procurement Manager",
            "FinanceController" => "Finance Controller",
            "Cfo" => "CFO",
            "Ceo" => "CEO",
            _ => Role
        };

        public string StatusText
        {
            get
            {
                if (IsReceived)
                {
                    var d = ReceivedDate ?? SignedDate;
                    var dateStr = d.HasValue ? d.Value.ToString("MMM dd, yyyy") : "Signed";
                    return $"Received {dateStr}";
                }
                if (SentDate.HasValue)
                {
                    return $"Sent {SentDate.Value:MMM dd, yyyy} (Pending)";
                }
                return "Not sent yet";
            }
        }

        public void NotifyPropertiesChanged()
        {
            OnPropertyChanged(nameof(RoleDisplayName));
            OnPropertyChanged(nameof(HasSentDate));
            OnPropertyChanged(nameof(HasReceivedDate));
            OnPropertyChanged(nameof(IsReceived));
            OnPropertyChanged(nameof(IsSent));
            OnPropertyChanged(nameof(SentDatePickerValue));
            OnPropertyChanged(nameof(ReceivedDatePickerValue));
            OnPropertyChanged(nameof(StatusText));
        }

        public Approval Clone()
        {
            return new Approval
            {
                Id = Id,
                PcrId = PcrId,
                Role = Role,
                SignedByName = SignedByName,
                Signed = Signed,
                SignedDate = SignedDate,
                SentDate = SentDate,
                ReceivedDate = ReceivedDate,
                SortOrder = SortOrder,
                RequiresMultipleDates = RequiresMultipleDates,
                IsIncluded = IsIncluded
            };
        }
    }
}
