using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Procure.Models;
using Procure.PageModels;

namespace Procure.App.Views;

/// <summary>Technical Approval field for the Add RFQ and Batch RFQ modals. Looks like a dropdown:
/// Not set / Approved / Not Approved / Custom…. "Custom…" opens a small panel with
/// "What was approved" and "Reason"; both are saved as one string (see
/// RequestForQuotation.JoinApproval), so the PCR prints it with no schema change.
/// Value uses the page model's shown form: "Not set" for nothing recorded.</summary>
public sealed class TechnicalApprovalPicker : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(TechnicalApprovalPicker),
        new PropertyMetadata(PrListPageModel.TechnicalApprovalNotSet, (d, _) => ((TechnicalApprovalPicker)d).ShowValue()));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private readonly TextBlock _summary = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, IsTextSelectionEnabled = false };
    private readonly Button _button = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly MenuFlyout _menu = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
    private readonly Flyout _panel = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
    private readonly TextBox _what = new() { Header = "What was approved", PlaceholderText = "e.g. Only item 1 approved", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _reason = new() { Header = "Reason (optional)", PlaceholderText = "e.g. Item 2 failed the pressure test", TextWrapping = TextWrapping.Wrap, MinHeight = 56 };
    private bool _discardPanel;

    public TechnicalApprovalPicker()
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(_summary);
        var chevron = new FontIcon { Glyph = "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);
        _button.Content = grid;
        _button.Click += (_, _) =>
        {
            // A custom entry reopens its panel, like clicking a filled-in field; the panel's
            // "Back to the list" gets to the standard choices.
            if (IsCustom(Value)) OpenPanel(); else _menu.ShowAt(_button);
        };

        foreach (var choice in PrListPageModel.StandardTechnicalApprovals)
        {
            var item = new MenuFlyoutItem { Text = choice };
            item.Click += (_, _) => Value = choice;
            _menu.Items.Add(item);
        }
        _menu.Items.Add(new MenuFlyoutSeparator());
        var custom = new MenuFlyoutItem { Text = "Custom…" };
        custom.Click += (_, _) => OpenPanel();
        _menu.Items.Add(custom);

        var back = new Button { Content = "Back to the list" };
        back.Click += (_, _) => { _discardPanel = true; _panel.Hide(); _menu.ShowAt(_button); };
        var done = new Button { Content = "Done", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        done.Click += (_, _) => _panel.Hide();
        var buttons = new Grid();
        buttons.Children.Add(back);
        done.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Children.Add(done);

        _panel.Content = new StackPanel
        {
            Width = 340,
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "CUSTOM APPROVAL", IsTextSelectionEnabled = false, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = 0.75 },
                _what,
                _reason,
                buttons,
            },
        };
        _panel.Opened += (_, _) => _what.Focus(FocusState.Programmatic);
        // Light dismiss (click outside, Esc) keeps what was typed, the same as Done; only
        // "Back to the list" drops it.
        _panel.Closed += (_, _) =>
        {
            if (_discardPanel) { _discardPanel = false; return; }
            var text = RequestForQuotation.JoinApproval(_what.Text, _reason.Text);
            // Typing "approved" lands on the standard choice, so the badges and PO picker agree.
            var standard = Array.Find(PrListPageModel.StandardTechnicalApprovals, s => string.Equals(s, text, StringComparison.OrdinalIgnoreCase));
            Value = text.Length == 0 ? PrListPageModel.TechnicalApprovalNotSet : standard ?? text;
        };

        Content = _button;
        ShowValue();
    }

    private void OpenPanel()
    {
        (_what.Text, _reason.Text) = IsCustom(Value) ? RequestForQuotation.SplitApproval(Value) : (string.Empty, string.Empty);
        _panel.ShowAt(_button);
    }

    private static bool IsCustom(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Array.IndexOf(PrListPageModel.StandardTechnicalApprovals, value) < 0;

    private void ShowValue()
    {
        var value = string.IsNullOrWhiteSpace(Value) ? PrListPageModel.TechnicalApprovalNotSet : Value;
        _summary.Text = IsCustom(value) ? "Custom: " + value : value;
        ToolTipService.SetToolTip(_button, IsCustom(value) ? value : null);
    }
}
