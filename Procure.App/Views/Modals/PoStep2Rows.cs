using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Procure.Models;

namespace Procure.App.Views.Modals;

// The PO wizard's second step as one flat list: each selected supplier's card cut into a top, a row per
// line, and a bottom. See the templates in AddPoModal.xaml for why.

public sealed partial class PoCardTopRow
{
    public PoCardTopRow(PoRfqSelection card) => Card = card;
    public PoRfqSelection Card { get; }
}

public sealed partial class PoLineRow
{
    public PoLineRow(PoRfqSelection card, PoRfqItemSelection line) { Card = card; Line = line; }
    public PoRfqSelection Card { get; }
    public PoRfqItemSelection Line { get; }
}

public sealed partial class PoCardBottomRow
{
    public PoCardBottomRow(PoRfqSelection card) => Card = card;
    public PoRfqSelection Card { get; }
}

public sealed partial class PoStep2TemplateSelector : DataTemplateSelector
{
    public DataTemplate? CardTop { get; set; }
    public DataTemplate? Line { get; set; }
    public DataTemplate? CardBottom { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        PoCardTopRow => CardTop,
        PoLineRow => Line,
        PoCardBottomRow => CardBottom,
        _ => null,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>
/// Keeps the flat row list in step with the cards it was cut from.
///
/// Lines are added and removed while the step is open ("+ Add line", the remove button), and each card's
/// Items is an ObservableCollection. Those changes are applied to the flat list in place rather than by
/// rebuilding it: a rebuild replaces the list's items, which throws the user back to the top of a
/// 750-row step the moment they remove a line near the bottom.
/// </summary>
internal sealed class PoStep2RowList
{
    public ObservableCollection<object> Rows { get; } = new();

    private readonly List<(PoRfqSelection Card, NotifyCollectionChangedEventHandler Handler)> _hooks = new();

    public void Build(IEnumerable<PoRfqSelection>? cards)
    {
        Detach();
        Rows.Clear();
        if (cards is null) return;

        foreach (var card in cards)
        {
            if (!card.IsSelected) continue;

            Rows.Add(new PoCardTopRow(card));
            foreach (var line in card.Items) Rows.Add(new PoLineRow(card, line));
            Rows.Add(new PoCardBottomRow(card));

            void OnItemsChanged(object? _, NotifyCollectionChangedEventArgs e) => ApplyLineChange(card, e);
            card.Items.CollectionChanged += OnItemsChanged;
            _hooks.Add((card, OnItemsChanged));
        }
    }

    /// <summary>Unhooks every card. The cards outlive the modal (they belong to the page model), so a
    /// handler left attached would keep this list - and the rows in it - alive after it closes.</summary>
    public void Detach()
    {
        foreach (var (card, handler) in _hooks) card.Items.CollectionChanged -= handler;
        _hooks.Clear();
    }

    private void ApplyLineChange(PoRfqSelection card, NotifyCollectionChangedEventArgs e)
    {
        var top = IndexOfTop(card);
        if (top < 0) return;

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null && e.NewStartingIndex >= 0:
                for (var i = 0; i < e.NewItems.Count; i++)
                    Rows.Insert(top + 1 + e.NewStartingIndex + i, new PoLineRow(card, (PoRfqItemSelection)e.NewItems[i]!));
                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems is not null && e.OldStartingIndex >= 0:
                for (var i = 0; i < e.OldItems.Count; i++)
                    Rows.RemoveAt(top + 1 + e.OldStartingIndex);
                break;

            default:
                // A reset or a move: rare, and rebuilding just this card's slice is simplest.
                RebuildCard(card, top);
                break;
        }
    }

    private void RebuildCard(PoRfqSelection card, int top)
    {
        var i = top + 1;
        while (i < Rows.Count && Rows[i] is PoLineRow line && ReferenceEquals(line.Card, card)) Rows.RemoveAt(i);
        foreach (var line in card.Items) Rows.Insert(i++, new PoLineRow(card, line));
    }

    private int IndexOfTop(PoRfqSelection card)
    {
        for (var i = 0; i < Rows.Count; i++)
            if (Rows[i] is PoCardTopRow t && ReferenceEquals(t.Card, card)) return i;
        return -1;
    }
}
