using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class PurchaseRequisition : ObservableObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [ObservableProperty]
        public partial string PrNo { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Description { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Requestor { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Plant { get; set; } = ProcurementPlant.RW01;

        [ObservableProperty]
        public partial string PrType { get; set; } = ProcurementPrType.StoresAndSpares;

        [ObservableProperty]
        public partial string Priority { get; set; } = ProcurementPriority.Normal;

        [ObservableProperty]
        public partial string Status { get; set; } = ProcurementStatus.PrRaised;

        [ObservableProperty]
        public partial string Notes { get; set; } = string.Empty;

        [ObservableProperty]
        public partial DateTime CreatedAt { get; set; } = DateTime.Now;

        [ObservableProperty]
        public partial DateTime UpdatedAt { get; set; } = DateTime.Now;

        [ObservableProperty]
        public partial ObservableCollection<PrItem> Items { get; set; } = new();

        [ObservableProperty]
        public partial ObservableCollection<RequestForQuotation> Rfqs { get; set; } = new();

        private PriceComparisonRequest? _pcr;
        public PriceComparisonRequest? Pcr
        {
            get => _pcr;
            set
            {
                if (_pcr != null)
                {
                    _pcr.PropertyChanged -= OnPcrPropertyChanged;
                }
                if (SetProperty(ref _pcr, value))
                {
                    if (_pcr != null)
                    {
                        _pcr.PropertyChanged += OnPcrPropertyChanged;
                    }
                    NotifyHierarchyChanged();
                }
            }
        }

        private void OnPcrPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(Pcr));
            OnPropertyChanged(nameof(PcrStatusDisplay));
        }

        [ObservableProperty]
        public partial ObservableCollection<PurchaseOrder> Pos { get; set; } = new();

        [ObservableProperty]
        public partial ObservableCollection<CustomFieldValue> CustomValues { get; set; } = new();

        [ObservableProperty]
        public partial Guid? ParentPrId { get; set; }

        [ObservableProperty]
        public partial string ConsolidatedFrom { get; set; } = string.Empty;

        // UI state helpers
        [ObservableProperty]
        public partial bool IsExpanded { get; set; }

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        public bool IsConsolidatedMaster => !string.IsNullOrWhiteSpace(ConsolidatedFrom);

        public bool IsMergedChild => ParentPrId.HasValue || Status == ProcurementStatus.Merged;

        public bool IsUrgent => Priority.Equals(ProcurementPriority.Urgent, StringComparison.OrdinalIgnoreCase);

        public int AgeDays => Math.Max(0, (DateTime.Today - CreatedAt.Date).Days);

        public string AgeDisplay
        {
            get
            {
                var days = AgeDays;
                if (days == 0) return "Today";
                if (days == 1) return "1d ago";
                return $"{days}d ago";
            }
        }

        public int ItemsCount => Items?.Count ?? 0;
        public bool HasItems => Items != null && Items.Count > 0;
        public decimal TotalItemQuantity => Items?.Sum(i => i.Quantity) ?? 0;

        public string ItemsSummary
        {
            get
            {
                if (HasItems)
                {
                    return string.Join(", ", Items.Select(i => $"{i.ItemName} ({i.FormattedQuantity})"));
                }
                return string.IsNullOrWhiteSpace(Description) ? "No items" : Description;
            }
        }

        public string PrimaryItemDisplay
        {
            get
            {
                if (HasItems)
                {
                    if (Items.Count == 1)
                    {
                        return $"{Items[0].ItemName} ({Items[0].FormattedQuantity})";
                    }
                    return $"{Items[0].ItemName} ({Items[0].FormattedQuantity}) +{Items.Count - 1} more";
                }
                return Description;
            }
        }

        public int RfqCount => Rfqs?.Count ?? 0;
        public int PoCount => Pos?.Count ?? 0;

        public string Currency => Pos?.FirstOrDefault()?.Currency ?? Rfqs?.FirstOrDefault()?.Currency ?? "AED";

        public decimal TotalPoValue => Pos?.Sum(p => p.Value) ?? 0m;
        public string FormattedTotalPoValue
        {
            get
            {
                var cur = Currency;
                return $"{cur} {TotalPoValue:N0}";
            }
        }

        public bool HasPcr => Pcr != null;

        public string PcrStatusDisplay
        {
            get
            {
                if (Pcr == null) return "No PCR";
                if (Pcr.IsFullyApproved) return "PCR Approved";
                return $"PCR Pending ({Pcr.ApprovalSummary})";
            }
        }

        public bool IsOverdue(int normalDaysThreshold = 10, int urgentDaysThreshold = 5)
        {
            if (Status == ProcurementStatus.Delivered || Status == ProcurementStatus.Closed || Status == ProcurementStatus.Cancelled || Status == ProcurementStatus.Merged)
                return false;

            var threshold = IsUrgent ? urgentDaysThreshold : normalDaysThreshold;
            return AgeDays >= threshold;
        }

        public bool HasSharedRfqs => Rfqs != null && Rfqs.Any(r => r.IsSharedRfq);
        public string SharedRfqsBadgeText => string.Join(", ", Rfqs.Where(r => r.IsSharedRfq).Select(r => r.SharedPrs).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());

        public bool HasCombinedPos => Pos != null && Pos.Any(p => p.IsCombinedPo);

        /// <summary>Gate for the whole badge row. All four badges are rare, so the card defers building
        /// the row - and the FlexLayout holding it - until a PR actually has one.</summary>
        public bool HasAnyBadge => IsConsolidatedMaster || IsMergedChild || HasSharedRfqs || HasCombinedPos;
        public string CombinedPosBadgeText => string.Join(", ", Pos.Where(p => p.IsCombinedPo).Select(p => p.CombinedPrs).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());

        public void CalculateItemFulfillments()
        {
            if (Items == null || Items.Count == 0) return;

            var allPoItems = Pos?.SelectMany(p => p.Items ?? Enumerable.Empty<PurchaseOrderItem>()).ToList() ?? new List<PurchaseOrderItem>();

            foreach (var item in Items)
            {
                var orderedQty = allPoItems
                    .Where(pi => (pi.PrItemId.HasValue && pi.PrItemId.Value == item.Id) || string.Equals(pi.ItemName, item.ItemName, StringComparison.OrdinalIgnoreCase))
                    .Sum(pi => pi.Quantity);

                item.OrderedQuantity = orderedQty;
            }

            OnPropertyChanged(nameof(PoFulfillmentBadgeText));
            OnPropertyChanged(nameof(IsPoFullyOrdered));
            OnPropertyChanged(nameof(IsPoPartiallyOrdered));
            OnPropertyChanged(nameof(TotalOrderedItemQuantity));
            OnPropertyChanged(nameof(TotalPendingItemQuantity));
        }

        public decimal TotalOrderedItemQuantity => Items?.Sum(i => i.OrderedQuantity) ?? 0m;
        public decimal TotalPendingItemQuantity => Items?.Sum(i => i.PendingQuantity) ?? 0m;

        public bool IsPoFullyOrdered => HasItems && Items.All(i => i.IsFullyOrdered);
        public bool IsPoPartiallyOrdered => HasItems && Pos.Count > 0 && !IsPoFullyOrdered;

        public string PendingItemsSummaryText
        {
            get
            {
                if (!HasItems || IsPoFullyOrdered) return string.Empty;
                var pendingGroups = Items
                    .Where(i => !i.IsFullyOrdered)
                    .GroupBy(i => string.IsNullOrWhiteSpace(i.Unit) ? "pcs" : i.Unit)
                    .Select(g => $"{g.Sum(i => i.PendingQuantity):G29} {g.Key}");
                return $"Pending: {string.Join(", ", pendingGroups)}";
            }
        }

        public bool HasPendingItemsSummary => !string.IsNullOrWhiteSpace(PendingItemsSummaryText);

        public string PoFulfillmentBadgeText
        {
            get
            {
                if (!HasItems) return string.Empty;
                int fullyOrderedCount = Items.Count(i => i.IsFullyOrdered);
                int totalCount = Items.Count;

                if (IsPoFullyOrdered)
                {
                    return $"PO: Complete ({fullyOrderedCount}/{totalCount} items)";
                }

                var pendingQty = TotalPendingItemQuantity;
                if (Pos == null || Pos.Count == 0)
                {
                    return $"PO: Unordered (0/{totalCount} items • {pendingQty:G29} pending)";
                }

                return $"PO: Partial ({fullyOrderedCount}/{totalCount} items • {pendingQty:G29} pending)";
            }
        }

        /// <summary>Raises only what a Status or Priority edit can change. Status and Priority notify
        /// themselves via [ObservableProperty]; IsMergedChild is derived from Status and does not.
        /// Use this instead of NotifyHierarchyChanged when no child collection was touched.</summary>
        public void NotifyStatusChanged()
        {
            OnPropertyChanged(nameof(IsMergedChild));
            OnPropertyChanged(nameof(HasAnyBadge));
        }

        public void NotifyHierarchyChanged()
        {
            CalculateItemFulfillments();
            if (Items != null)
            {
                foreach (var item in Items)
                {
                    item.NotifyThemeChanged();
                }
            }
            Pcr?.NotifyThemeChanged();
            Pcr?.NotifyApprovalsChanged();
            OnPropertyChanged(nameof(Pcr));
            OnPropertyChanged(nameof(PrType));
            OnPropertyChanged(nameof(ItemsCount));
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(TotalItemQuantity));
            OnPropertyChanged(nameof(ItemsSummary));
            OnPropertyChanged(nameof(PrimaryItemDisplay));
            OnPropertyChanged(nameof(RfqCount));
            OnPropertyChanged(nameof(PoCount));
            OnPropertyChanged(nameof(TotalPoValue));
            OnPropertyChanged(nameof(FormattedTotalPoValue));
            OnPropertyChanged(nameof(HasPcr));
            OnPropertyChanged(nameof(PcrStatusDisplay));
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(Priority));
            OnPropertyChanged(nameof(IsConsolidatedMaster));
            OnPropertyChanged(nameof(IsMergedChild));
            OnPropertyChanged(nameof(HasSharedRfqs));
            OnPropertyChanged(nameof(SharedRfqsBadgeText));
            OnPropertyChanged(nameof(HasCombinedPos));
            OnPropertyChanged(nameof(CombinedPosBadgeText));
            OnPropertyChanged(nameof(HasAnyBadge));
            // PoFulfillmentBadgeText, IsPoFullyOrdered, IsPoPartiallyOrdered, TotalOrderedItemQuantity
            // and TotalPendingItemQuantity are already raised by CalculateItemFulfillments() above.
        }

        /// <summary>Copies a freshly loaded row and its children onto this live instance, so a reload can
        /// reuse the object every card is already bound to instead of handing the board new instances it
        /// has to tear down and rebuild. Unchanged values are never re-assigned, so a reload that found no
        /// DB change raises no PropertyChanged at all. UI-only state (IsExpanded, IsSelected) is left alone.</summary>
        public void MergeFrom(PurchaseRequisition fresh)
        {
            if (fresh == null || ReferenceEquals(fresh, this)) return;

            // Only when something actually moved: the computed card labels (ItemsCount, TotalPoValue,
            // PcrStatusDisplay...) read the child collections and nothing notifies them on their own.
            // This is the pass the repository deliberately leaves to the UI thread.
            if (MergeInto(this, fresh)) NotifyHierarchyChanged();
        }

        // ponytail: reflection rather than a hand-written copy for each of the seven model types. The
        // column list already lives in the repository's readers; duplicating it here is what goes stale
        // and silently shows old data. ~500 objects a load against a cached PropertyInfo[] is sub-ms.
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> MergeableProps = new();
        private static readonly ConcurrentDictionary<Type, PropertyInfo> IdProps = new();

        private static Guid IdOf(object o) => (Guid)IdProps.GetOrAdd(o.GetType(), static t => t.GetProperty("Id")!).GetValue(o)!;

        /// <summary>Copies every persisted property of <paramref name="fresh"/> onto
        /// <paramref name="target"/> (same runtime type). Returns true if anything actually changed.</summary>
        private static bool MergeInto(object target, object fresh)
        {
            var changed = false;

            foreach (var p in MergeableProps.GetOrAdd(target.GetType(), static t => t
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
                            && p.Name != nameof(IsExpanded) && p.Name != nameof(IsSelected))
                .ToArray()))
            {
                var current = p.GetValue(target);
                var incoming = p.GetValue(fresh);

                // Child collection (Items, Rfqs, Pos, CustomValues, Approvals, PO/RFQ line items):
                // merge the contents so the bound child layout is not reset.
                if (current is IList currentList && incoming is IList incomingList)
                {
                    changed |= MergeList(currentList, incomingList);
                }
                // Single nav property (Pcr): same row merges in place, a different row is assigned so
                // the setter can re-hook its PropertyChanged.
                else if (current is ObservableObject liveChild && incoming is ObservableObject freshChild
                         && IdOf(liveChild) == IdOf(freshChild))
                {
                    changed |= MergeInto(liveChild, freshChild);
                }
                else if (!Equals(current, incoming))
                {
                    p.SetValue(target, incoming);
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>Reconciles a bound child collection by Id: a row that is still there merges into the
        /// live element and raises no collection event at all, new rows are inserted, dropped rows removed.
        /// ponytail: O(n^2) scan and remove+insert instead of Move — child lists are a handful of rows and
        /// the DB returns them in SortOrder, so a reorder is rare. Index the ids if that stops holding.</summary>
        private static bool MergeList(IList target, IList fresh)
        {
            var changed = false;

            for (var i = target.Count - 1; i >= 0; i--)
            {
                var id = IdOf(target[i]!);
                var stillLoaded = false;
                foreach (var f in fresh)
                {
                    if (IdOf(f!) == id) { stillLoaded = true; break; }
                }
                if (!stillLoaded)
                {
                    target.RemoveAt(i);
                    changed = true;
                }
            }

            for (var i = 0; i < fresh.Count; i++)
            {
                var id = IdOf(fresh[i]!);
                var existing = -1;
                for (var j = i; j < target.Count; j++)
                {
                    if (IdOf(target[j]!) == id) { existing = j; break; }
                }

                if (existing < 0)
                {
                    target.Insert(i, fresh[i]);
                    changed = true;
                    continue;
                }

                changed |= MergeInto(target[existing]!, fresh[i]!);
                if (existing != i)
                {
                    var moved = target[existing];
                    target.RemoveAt(existing);
                    target.Insert(i, moved);
                    changed = true;
                }
            }

            return changed;
        }
    }
}
