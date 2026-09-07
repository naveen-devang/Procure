using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace Procure.Utilities
{
    /// <summary>
    /// Read-only item-name labels sit clamped to a couple of lines in their row. Hovering the label
    /// drops the clamp so the whole spec is visible, and restores it when the pointer leaves - a way
    /// to see the full text without leaving the row, for the places the name is not editable (board
    /// card, PO wizard, batch RFQ). Editable name fields use a fixed two-line Editor plus a tooltip
    /// instead - a growable field inside a bindable row could not be made to shrink back reliably.
    /// </summary>
    public sealed class ExpandOnHoverBehavior : Behavior<Label>
    {
        public static readonly BindableProperty CollapsedLinesProperty =
            BindableProperty.Create(nameof(CollapsedLines), typeof(int), typeof(ExpandOnHoverBehavior), 2);

        public int CollapsedLines
        {
            get => (int)GetValue(CollapsedLinesProperty);
            set => SetValue(CollapsedLinesProperty, value);
        }

        private Label? _label;
        private PointerGestureRecognizer? _pointer;

        protected override void OnAttachedTo(Label label)
        {
            base.OnAttachedTo(label);
            _label = label;
            Collapse();
            _pointer = new PointerGestureRecognizer();
            _pointer.PointerEntered += OnEntered;
            _pointer.PointerExited += OnExited;
            label.GestureRecognizers.Add(_pointer);
        }

        protected override void OnDetachingFrom(Label label)
        {
            base.OnDetachingFrom(label);
            if (_pointer != null)
            {
                _pointer.PointerEntered -= OnEntered;
                _pointer.PointerExited -= OnExited;
                label.GestureRecognizers.Remove(_pointer);
                _pointer = null;
            }
            _label = null;
        }

        private void OnEntered(object? sender, PointerEventArgs e)
        {
            if (_label == null) return;
            _label.MaxLines = -1;
            _label.LineBreakMode = LineBreakMode.WordWrap;
        }

        private void OnExited(object? sender, PointerEventArgs e) => Collapse();

        private void Collapse()
        {
            if (_label == null) return;
            _label.MaxLines = CollapsedLines;
            _label.LineBreakMode = LineBreakMode.TailTruncation;
        }
    }
}
