using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class PurchaseRequisition : ObservableModel
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
            // Only the PR-level rollup: re-raising Pcr here would force every Pcr.* binding path in
            // the expanded panel to re-resolve on each PCR change; those paths already listen to the
            // PCR instance's own PropertyChanged.
            OnPropertyChanged(nameof(PcrStatusDisplay));
        }

        [ObservableProperty]
        public partial ObservableCollection<PurchaseOrder> Pos { get; set; } = new();

        [ObservableProperty]
        public partial ObservableCollection<CustomFieldValue> CustomValues { get; set; } = new();

        // Not loaded with the PR - filled on demand by PrListPageModel.LoadLinkedTasksAsync when
        // the detail panel is expanded. Tasks the user linked to this PR from the Tasks page.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LinkedTaskCountLabel))]
        public partial ObservableCollection<TodoTask> LinkedTasks { get; set; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LinkedTaskCountLabel))]
        public partial bool LinkedTasksLoaded { get; set; }

        public string LinkedTaskCountLabel => $"Tasks ({LinkedTasks?.Count ?? 0})";

        public void RefreshLinkedTaskCount() => OnPropertyChanged(nameof(LinkedTaskCountLabel));

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

        /// <summary>Whether this requisition carries its quote lines, order lines and custom field
        /// values, or only what a board card needs. The board's page read leaves them out - about 20
        /// of the ~35 rows a requisition carries, for four numbers that come from elsewhere - and
        /// PrListPageModel.EnsureHydratedAsync fills them in when it is actually opened.
        ///
        /// Not a UI concern, so deliberately not an ObservableProperty: nothing binds to it.</summary>
        public bool LineItemsLoaded { get; set; } = true;

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
                if (Pos == null || Pos.Count == 0)
                {
                    return MoneyFormat.Format(Currency, 0m);
                }

                // Single-currency (the overwhelmingly common case) takes a plain loop: this getter
                // re-runs for every realized board card on each hierarchy refresh, so the GroupBy
                // machinery is reserved for genuinely mixed-currency PRs.
                var firstCur = string.IsNullOrWhiteSpace(Pos[0].Currency) ? "AED" : Pos[0].Currency;
                var singleCurrency = true;
                decimal total = 0m;
                foreach (var po in Pos)
                {
                    var cur = string.IsNullOrWhiteSpace(po.Currency) ? "AED" : po.Currency;
                    if (!string.Equals(cur, firstCur, StringComparison.OrdinalIgnoreCase))
                    {
                        singleCurrency = false;
                        break;
                    }
                    total += po.Value;
                }
                if (singleCurrency)
                {
                    return MoneyFormat.Format(firstCur, total);
                }

                // Per-currency totals (like the PO wizard footer): a raw sum across currencies
                // stamped with the first PO's currency was meaningless for multi-currency PRs.
                var groups = Pos
                    .GroupBy(p => string.IsNullOrWhiteSpace(p.Currency) ? "AED" : p.Currency)
                    .Select(g => MoneyFormat.Format(g.Key, g.Sum(p => p.Value)));
                return string.Join(" • ", groups);
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

        /// <summary>One pass over this PR's children that refreshes everything derived from them:
        /// how much of each line is on order, and how completely each quote covers the requisition.
        ///
        /// Deliberately one pass, called from one place. Coverage used to be computed per binding
        /// read on the RFQ itself, which meant walking the PR's lines every time a card repainted;
        /// fulfilment was a second walk of the same data. This runs on the board's reload path, so
        /// it reuses buffers rather than allocating per quote.</summary>
        public void CalculateItemFulfillments()
        {
            if (Items == null || Items.Count == 0)
            {
                if (Rfqs != null)
                {
                    // A PR with no lines can still hold quotes; clear the coverage rather than
                    // leaving whatever the last requisition state put there.
                    foreach (var rfq in Rfqs) rfq.PrLineCount = rfq.PrLinesCovered = rfq.PrLinesPriced = rfq.PrLinesQuantityDrifted = 0;
                }
                return;
            }

            // Each PO line counts once, against one line. Summing "same id OR same name" credited a
            // PO raised for one line to every same-named sibling too, so a merged PR with 12 and 33
            // of one item read "Complete" as soon as the 12 was ordered.
            var ordered = Procure.Utilities.PrLineMatcher.OrderedQuantities(Items, Pos);

            foreach (var item in Items)
            {
                item.OrderedQuantity = ordered.TryGetValue(item.Id, out var qty) ? qty : 0m;
            }

            RecalculateQuoteCoverage();

            OnPropertyChanged(nameof(PoFulfillmentBadgeText));
            OnPropertyChanged(nameof(HasOverOrderedItems));
            OnPropertyChanged(nameof(IsPoFullyOrdered));
            OnPropertyChanged(nameof(IsPoPartiallyOrdered));
            OnPropertyChanged(nameof(TotalOrderedItemQuantity));
            OnPropertyChanged(nameof(TotalPendingItemQuantity));
            OnPropertyChanged(nameof(PendingItemsSummaryText));
            OnPropertyChanged(nameof(HasPendingItemsSummary));
        }

        /// <summary>Fills in each quote's "how much of this requisition do you actually cover"
        /// counters. Buffers are shared across every quote - a PR with five vendors used to mean
        /// five throwaway dictionaries per refresh.</summary>
        private void RecalculateQuoteCoverage()
        {
            if (Rfqs == null || Rfqs.Count == 0 || Items == null || Items.Count == 0) return;

            var buffer = new Dictionary<RfqItem, PrItem>();
            var claimed = new bool[Items.Count];

            foreach (var rfq in Rfqs)
            {
                rfq.PrLineCount = Items.Count;

                if (rfq.Items == null || rfq.Items.Count == 0)
                {
                    rfq.PrLinesCovered = 0;
                    rfq.PrLinesPriced = 0;
                    rfq.PrLinesQuantityDrifted = 0;
                    continue;
                }

                Procure.Utilities.PrLineMatcher.MapInto(rfq.Items, Items, buffer, claimed);

                int covered = 0, priced = 0, drifted = 0;
                foreach (var pair in buffer)
                {
                    var line = pair.Key;
                    covered++;

                    var isPriced = line.IsQuoted && ((line.QuotedUnitPrice ?? 0m) > 0m || line.LineTotal > 0m);
                    if (isPriced)
                    {
                        priced++;
                        // Only a priced line can drift: an unpriced one is about to be re-synced
                        // to the requisition anyway (see PrListPageModel's PR save).
                        if (line.Quantity != pair.Value.Quantity) drifted++;
                    }
                }

                rfq.PrLinesCovered = covered;
                rfq.PrLinesPriced = priced;
                rfq.PrLinesQuantityDrifted = drifted;
            }
        }

        public decimal TotalOrderedItemQuantity => Items?.Sum(i => i.OrderedQuantity) ?? 0m;
        public decimal TotalPendingItemQuantity => Items?.Sum(i => i.PendingQuantity) ?? 0m;

        public bool HasOverOrderedItems => HasItems && Items.Any(i => i.IsOverOrdered);
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

        public bool HasPendingItemsSummary => HasItems && !IsPoFullyOrdered;

        public string PoFulfillmentBadgeText
        {
            get
            {
                if (!HasItems) return string.Empty;
                int fullyOrderedCount = Items.Count(i => i.IsFullyOrdered);
                int totalCount = Items.Count;

                // Checked before "Complete": an over-ordered line satisfies IsFullyOrdered, so a PR
                // cut back below what was already ordered used to read Complete and hide the surplus.
                if (HasOverOrderedItems)
                {
                    var overQty = Items.Sum(i => i.OverOrderedQuantity);
                    var overCount = Items.Count(i => i.IsOverOrdered);
                    return $"PO: Over-ordered ({overCount} of {totalCount} items • {overQty:G29} more than the PR asks for)";
                }

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

            // Down into the children. Their computed labels - a quote's coverage chip, an order's
            // item count - are derived from data this PR owns, and nothing else tells them it moved,
            // so a PR edit used to leave the quote and order cards below it showing pre-edit values
            // until the whole board was rebuilt. Only reached when something actually changed:
            // MergeFrom gates this call, and the save paths call it once per save.
            if (Rfqs != null)
            {
                foreach (var rfq in Rfqs) rfq.NotifyCalculationsChanged();
            }
            if (Pos != null)
            {
                foreach (var po in Pos) po.NotifyCalculationsChanged();
            }
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
            // A board reload reads requisitions without their quote and order lines. Merging that
            // into a requisition the user has open would see empty collections and take the lines
            // away - MergeList removes anything the fresh copy does not have. So a shallow source
            // leaves those collections alone; it has nothing to say about them.
            if (MergeInto(this, fresh, mergeLineItems: fresh.LineItemsLoaded)) NotifyHierarchyChanged();
            if (fresh.LineItemsLoaded) LineItemsLoaded = true;
        }

        // ponytail: reflection rather than a hand-written copy for each of the seven model types. The
        // column list already lives in the repository's readers; duplicating it here is what goes stale
        // and silently shows old data. ~500 objects a load against a cached PropertyInfo[] is sub-ms.
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> MergeableProps = new();
        private static readonly ConcurrentDictionary<Type, PropertyInfo> IdProps = new();

        private static Guid IdOf(object o) => (Guid)IdProps.GetOrAdd(o.GetType(), static t => t.GetProperty("Id")!).GetValue(o)!;

        /// <summary>Copies every persisted property of <paramref name="fresh"/> onto
        /// <paramref name="target"/> (same runtime type). Returns true if anything actually changed.</summary>
        private static bool MergeInto(object target, object fresh, bool mergeLineItems = true)
        {
            var changed = false;

            foreach (var p in MergeableProps.GetOrAdd(target.GetType(), static t => t
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
                            && p.Name != nameof(IsExpanded) && p.Name != nameof(IsSelected)
                            // Describes what this instance holds, not what the row says. Copying it
                            // from a shallow read would mark a requisition whose lines are still in
                            // memory as needing them fetched again.
                            && p.Name != nameof(LineItemsLoaded)
                            // UI-helper setters with side effects (they rewrite the persisted
                            // properties they wrap); merging them would depend on reflection
                            // returning the raw properties first, which is not guaranteed.
                            && p.Name != nameof(Approval.SentDatePickerValue)
                            && p.Name != nameof(Approval.ReceivedDatePickerValue)
                            && p.Name != nameof(CustomFieldValue.DateValue))
                .ToArray()))
            {
                var current = p.GetValue(target);
                var incoming = p.GetValue(fresh);

                // Child collection (Items, Rfqs, Pos, CustomValues, Approvals, PO/RFQ line items):
                // merge the contents so the bound child layout is not reset.
                if (current is IList currentList && incoming is IList incomingList)
                {
                    // The lines under a quote or an order, and a requisition's custom field values:
                    // absent from a shallow read rather than deleted.
                    if (!mergeLineItems && IsLineItemCollection(target, p.Name)) continue;
                    changed |= MergeList(currentList, incomingList, mergeLineItems);
                }
                // Single nav property (Pcr): same row merges in place, a different row is assigned so
                // the setter can re-hook its PropertyChanged.
                else if (current is ObservableObject liveChild && incoming is ObservableObject freshChild
                         && IdOf(liveChild) == IdOf(freshChild))
                {
                    changed |= MergeInto(liveChild, freshChild, mergeLineItems);
                }
                else if (!Equals(current, incoming))
                {
                    p.SetValue(target, incoming);
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>The collections a shallow read does not carry: a quote's lines, an order's lines,
        /// and the requisition's custom field values. A requisition's own Items are NOT one of them -
        /// the board card counts them.</summary>
        private static bool IsLineItemCollection(object target, string propertyName) =>
            (propertyName == nameof(Items) && target is RequestForQuotation or PurchaseOrder)
            || propertyName == nameof(CustomValues);

        /// <summary>Reconciles a bound child collection by Id: a row that is still there merges into the
        /// live element and raises no collection event at all, new rows are inserted, dropped rows removed.
        /// ponytail: O(n^2) scan and remove+insert instead of Move — child lists are a handful of rows and
        /// the DB returns them in SortOrder, so a reorder is rare. Index the ids if that stops holding.</summary>
        private static bool MergeList(IList target, IList fresh, bool mergeLineItems = true)
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
