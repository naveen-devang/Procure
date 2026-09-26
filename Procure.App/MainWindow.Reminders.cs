using System;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Procure.Abstractions;
using Procure.App.Converters;
using Procure.Services;
using Procure.Utilities;

namespace Procure.App;

// Task reminder cards (TaskReminderService): bottom-right of the window, above every page, so a
// reminder reaches you whichever tab you are on. Up to three cards, then "+N more".
public sealed partial class MainWindow
{
    private const int MaxReminderCards = 3;
    private TaskReminderService? _reminders;

    private void InitReminders()
    {
        _reminders = App.Services.GetRequiredService<TaskReminderService>();
        _reminders.Changed += BuildReminderCards;
    }

    /// <summary>After the first frame, so a launch is never held up by it.</summary>
    private void StartReminders() => _reminders?.Start();

    private void BuildReminderCards()
    {
        if (_reminders is not { } service) return;
        ReminderStack.Children.Clear();

        if (service.MissedCount > 0)
        {
            var count = service.MissedCount;
            ReminderStack.Children.Add(Card(
                "While Procure was closed",
                count == 1 ? "1 task came due" : $"{count} tasks came due",
                string.Empty,
                Action("Open Tasks", accent: true, () => service.OpenMissedAsync()),
                Action("Dismiss", accent: false, () => service.DismissMissedAsync())));
        }

        foreach (var row in service.Due.Take(MaxReminderCards))
        {
            var snooze = new DropDownButton { Content = "Snooze" };
            var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft };
            foreach (var (label, delay) in new (string, TimeSpan?)[] { ("10 minutes", TimeSpan.FromMinutes(10)), ("1 hour", TimeSpan.FromHours(1)), ("Tomorrow", null) })
            {
                var item = new MenuFlyoutItem { Text = label };
                item.Click += async (_, _) => await service.SnoozeAsync(row, delay);
                menu.Items.Add(item);
            }
            snooze.Flyout = menu;

            ReminderStack.Children.Add(Card(
                When(TaskReminders.RemindAt(row, service.DefaultTime)),
                row.Title,
                row.LinkLabel,
                Action("Done", accent: true, () => service.DoneAsync(row)),
                Action("Open", accent: false, () => service.OpenAsync(row)),
                snooze,
                Action("Dismiss", accent: false, () => service.DismissAsync(row))));
        }

        var more = service.Due.Count - MaxReminderCards;
        if (more > 0)
        {
            var link = new HyperlinkButton { Content = $"+{more} more due", HorizontalAlignment = HorizontalAlignment.Right };
            link.Click += (_, _) => NavigateTo(AppRoute.Tasks, null);
            ReminderStack.Children.Add(link);
        }
    }

    /// <summary>"Due today at 09:00", "Due Thu 24 Sep at 09:00".</summary>
    private static string When(DateTime? at) =>
        at is not { } a ? "Reminder"
        : a.Date == DateTime.Today ? $"Due today at {a:HH:mm}"
        : $"Due {a.ToString("ddd d MMM", CultureInfo.CurrentCulture)} at {a:HH:mm}";

    private static Button Action(string text, bool accent, Func<System.Threading.Tasks.Task> run)
    {
        var button = new Button { Content = text };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;   // one press, one action
            await run();
        };
        return button;
    }

    private Border Card(string caption, string title, string detail, params FrameworkElement[] actions)
    {
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(new TextBlock { Text = caption, FontSize = 11.5, Foreground = BoardTheme.Themed("AppTextTertiary") });
        body.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = BoardTheme.Themed("AppTextPrimary"),
        });
        if (detail.Length > 0)
            body.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = BoardTheme.Themed("AppTextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var a in actions) buttons.Children.Add(a);
        body.Children.Add(buttons);

        var card = new Border
        {
            Child = body,
            Padding = new Thickness(16, 12, 16, 14),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = BoardTheme.Themed("AppSecondaryBackground"),
            BorderBrush = BoardTheme.Themed("AppControlBorder"),
            Translation = new System.Numerics.Vector3(0, 0, 32),
        };
        // Lifted off the page like the Undo toast: the shadow falls on the NavigationView beside it.
        var shadow = new ThemeShadow();
        shadow.Receivers.Add(Nav);
        card.Shadow = shadow;
        return card;
    }
}
