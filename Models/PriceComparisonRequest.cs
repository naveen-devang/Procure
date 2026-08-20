using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class PriceComparisonRequest : ObservableObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid PrId { get; set; }

        [ObservableProperty]
        public partial string PcrNo { get; set; } = string.Empty;

        [ObservableProperty]
        public partial DateTime CreatedAt { get; set; } = DateTime.Now;

        [ObservableProperty]
        public partial string Remarks { get; set; } = string.Empty;

        private ObservableCollection<Approval> _approvals = new();
        public ObservableCollection<Approval> Approvals
        {
            get => _approvals;
            set
            {
                if (_approvals != null)
                {
                    _approvals.CollectionChanged -= OnApprovalsCollectionChanged;
                    foreach (var a in _approvals) a.PropertyChanged -= OnApprovalItemPropertyChanged;
                }
                if (SetProperty(ref _approvals, value ?? new ObservableCollection<Approval>()))
                {
                    if (_approvals != null)
                    {
                        _approvals.CollectionChanged += OnApprovalsCollectionChanged;
                        foreach (var a in _approvals) a.PropertyChanged += OnApprovalItemPropertyChanged;
                    }
                    NotifyApprovalsChanged();
                }
            }
        }

        public PriceComparisonRequest()
        {
            _approvals.CollectionChanged += OnApprovalsCollectionChanged;
        }

        private void OnApprovalsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (Approval item in e.OldItems) item.PropertyChanged -= OnApprovalItemPropertyChanged;
            }
            if (e.NewItems != null)
            {
                foreach (Approval item in e.NewItems) item.PropertyChanged += OnApprovalItemPropertyChanged;
            }
            NotifyApprovalsChanged();
        }

        private void OnApprovalItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Approval.IsReceived) ||
                e.PropertyName == nameof(Approval.ReceivedDate) ||
                e.PropertyName == nameof(Approval.Signed) ||
                e.PropertyName == nameof(Approval.SignedDate) ||
                e.PropertyName == nameof(Approval.SentDate) ||
                e.PropertyName == nameof(Approval.StatusText))
            {
                NotifyApprovalsChanged();
            }
        }

        public bool IsFullyApproved => Approvals.Count > 0 && Approvals.All(a => a.IsReceived);

        public int SignedCount => Approvals.Count(a => a.IsReceived);
        public int TotalApprovalsCount => Approvals.Count;

        public string ApprovalSummary => $"{SignedCount}/{TotalApprovalsCount} Signed";

        public void EnsureDefaultApprovals(IEnumerable<string>? customDefaultRoles = null)
        {
            if (Approvals.Count == 0)
            {
                var roles = customDefaultRoles?.ToList() ?? new List<string>
                {
                    ApprovalRoles.ProcurementManager,
                    ApprovalRoles.FinanceController,
                    ApprovalRoles.Cfo,
                    ApprovalRoles.Ceo
                };

                int order = 0;
                foreach (var role in roles)
                {
                    Approvals.Add(new Approval
                    {
                        Id = Guid.NewGuid(),
                        PcrId = Id,
                        Role = role,
                        SortOrder = order++,
                        RequiresMultipleDates = true
                    });
                }
            }
        }

        public void NotifyThemeChanged()
        {
            if (Approvals != null)
            {
                foreach (var approval in Approvals)
                {
                    approval.NotifyPropertiesChanged();
                }
            }
        }

        public void NotifyApprovalsChanged()
        {
            OnPropertyChanged(nameof(Approvals));
            OnPropertyChanged(nameof(IsFullyApproved));
            OnPropertyChanged(nameof(SignedCount));
            OnPropertyChanged(nameof(TotalApprovalsCount));
            OnPropertyChanged(nameof(ApprovalSummary));
        }
    }
}

