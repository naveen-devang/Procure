using System;
using System.Collections.Generic;
using System.Linq;

namespace Procure.Models
{
    /// <summary>The three tabs of the PR board. A requisition belongs to exactly one, decided by its
    /// status alone - there is no stored "tab" to keep in step.</summary>
    public enum BoardTab { Active, Completed, OnHold }

    public static class BoardTabs
    {
        /// <summary>Finished. Mark completed writes <see cref="ProcurementStatus.Closed"/>.</summary>
        public static readonly string[] CompletedStatuses = { ProcurementStatus.Delivered, ProcurementStatus.Closed };

        /// <summary>Parked or gone. Merged is a system state (the board hides merged sources), so it
        /// lives here rather than among the work still to do.</summary>
        public static readonly string[] OnHoldStatuses = { ProcurementStatus.OnHold, ProcurementStatus.Cancelled, ProcurementStatus.Merged };

        /// <summary>Active is "everything else", not a list. A status this app does not know (an old
        /// import, a hand-edited row) therefore still shows up somewhere instead of in no tab at all.</summary>
        public static BoardTab Of(string? status)
        {
            if (string.IsNullOrWhiteSpace(status)) return BoardTab.Active;
            if (CompletedStatuses.Contains(status, StringComparer.OrdinalIgnoreCase)) return BoardTab.Completed;
            if (OnHoldStatuses.Contains(status, StringComparer.OrdinalIgnoreCase)) return BoardTab.OnHold;
            return BoardTab.Active;
        }

        /// <summary>The statuses the board's status dropdown offers inside a tab.</summary>
        public static IReadOnlyList<string> StatusesOf(BoardTab tab) => tab switch
        {
            BoardTab.Completed => CompletedStatuses,
            BoardTab.OnHold => OnHoldStatuses,
            _ => ProcurementStatus.AllStatuses.Where(s => Of(s) == BoardTab.Active).ToArray(),
        };

        public static string Title(BoardTab tab) => tab switch
        {
            BoardTab.Completed => "Completed",
            BoardTab.OnHold => "On hold & cancelled",
            _ => "Active",
        };
    }
}
