using System;
using System.Globalization;

namespace Procure.Utilities
{
    /// <summary>One open task with a due date, as the reminder service sees it.</summary>
    public sealed record ReminderRow(Guid Id, string Title, DateTime DueDate, string? ReminderTime,
        DateTime? SnoozedUntil, DateTime? Acknowledged, string LinkLabel);

    /// <summary>
    /// When a task's reminder goes off. A task's ReminderTime is null (the default time from Settings,
    /// on the due day), <see cref="Off"/>, or "HH:mm". A snooze replaces the time until the task's
    /// due date or reminder time is changed. A reminder that was dismissed or acted on is
    /// "acknowledged" at the exact moment it went off, so it stays quiet until that moment moves.
    /// </summary>
    public static class TaskReminders
    {
        public const string Off = "off";
        public static readonly TimeSpan DefaultTime = new(9, 0, 0);

        public static TimeSpan? ParseTime(string? text) =>
            TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out var t) ? t : null;

        public static string FormatTime(TimeSpan time) => time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

        /// <summary>When the reminder goes off, or null for a task with reminders turned off.</summary>
        public static DateTime? RemindAt(ReminderRow row, TimeSpan defaultTime) =>
            row.ReminderTime == Off ? null
            : row.SnoozedUntil ?? row.DueDate.Date + (ParseTime(row.ReminderTime) ?? defaultTime);

        public static bool IsDue(ReminderRow row, DateTime now, TimeSpan defaultTime) =>
            RemindAt(row, defaultTime) is { } at && at <= now && row.Acknowledged != at;

        /// <summary>"Tomorrow at the default time" - the Snooze menu's last choice.</summary>
        public static DateTime TomorrowAt(DateTime now, TimeSpan defaultTime) => now.Date.AddDays(1) + defaultTime;

        public static void SelfCheck()
        {
            static void Check(bool ok, string what)
            {
                if (!ok) throw new InvalidOperationException("TaskReminders: " + what);
            }

            var due = new DateTime(2026, 9, 26);
            var nine = TimeSpan.FromHours(9);
            ReminderRow Row(string? time = null, DateTime? snoozed = null, DateTime? ack = null) =>
                new(Guid.NewGuid(), "t", due, time, snoozed, ack, "");

            Check(RemindAt(Row(), nine) == due.AddHours(9), "default time on the due day");
            Check(RemindAt(Row("14:30"), nine) == due.AddHours(14.5), "a set time");
            Check(RemindAt(Row(Off), nine) is null, "reminders off");
            Check(RemindAt(Row(snoozed: due.AddHours(10)), nine) == due.AddHours(10), "a snooze replaces the time");

            Check(!IsDue(Row(), due.AddHours(8.99), nine), "not before its time");
            Check(IsDue(Row(), due.AddHours(9), nine), "due at its time");
            Check(IsDue(Row(), due.AddDays(3), nine), "still due days later until acknowledged");
            Check(!IsDue(Row(ack: due.AddHours(9)), due.AddDays(3), nine), "quiet once acknowledged");
            Check(IsDue(Row("11:00", ack: due.AddHours(9)), due.AddHours(11), nine), "a new time fires again after an old dismissal");
            Check(!IsDue(Row(snoozed: due.AddHours(10), ack: due.AddHours(9)), due.AddHours(9.5), nine), "snoozed is quiet until the snooze ends");

            Check(ParseTime("09:05") == new TimeSpan(9, 5, 0) && ParseTime("9am") is null && ParseTime(null) is null, "time text");
            Check(FormatTime(new TimeSpan(7, 3, 0)) == "07:03", "time format");
            Check(TomorrowAt(due.AddHours(15), nine) == due.AddDays(1).AddHours(9), "tomorrow at the default time");
        }
    }
}
