using System;
using System.Globalization;

namespace Procure.Models
{
    /// <summary>One service invoice on the Service Entry register: a row of the paper book. Stands
    /// alone - PoNo and Vendor are typed, never linked to the PR board.</summary>
    public partial class ServiceEntry : ObservableModel
    {
        public const int InvoiceIn = 0, WithTechnical = 1, SeDone = 2, WithAccounts = 3;

        public static readonly string[] StageNames = { "Invoice in", "With technical", "SE done", "With accounts" };

        public Guid Id { get; set; } = Guid.NewGuid();
        public int SrNo { get; set; }
        public string PoNo { get; set; } = string.Empty;
        public decimal? PoAmount { get; set; }
        public string Vendor { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTime InvoiceDate { get; set; } = DateTime.Today;
        public string InvoiceNo { get; set; } = string.Empty;
        public decimal InvoiceAmount { get; set; }
        public DateTime? TechHandoverDate { get; set; }
        public DateTime? SapSeDate { get; set; }
        public string? ServiceEntryNo { get; set; }
        public DateTime? AccountHandoverDate { get; set; }

        /// <summary>Set by whoever loads the row, from Settings: days in one step before it is flagged.</summary>
        public int FlagDays { get; set; } = 10;

        // Same rule as the table's generated Stage column.
        public int Stage => AccountHandoverDate.HasValue ? WithAccounts
            : SapSeDate.HasValue ? SeDone
            : TechHandoverDate.HasValue ? WithTechnical
            : InvoiceIn;

        public DateTime StageDate => AccountHandoverDate ?? SapSeDate ?? TechHandoverDate ?? InvoiceDate;
        public int DaysInStage => Math.Max(0, (DateTime.Today - StageDate.Date).Days);
        public bool IsDone => Stage == WithAccounts;
        public bool IsLate => !IsDone && DaysInStage > FlagDays;

        public string StageName => StageNames[Stage];
        public string AgeText => IsDone ? string.Empty : DaysInStage == 1 ? "1 day in this step" : $"{DaysInStage} days in this step";

        public string PoAmountText => PoAmount is { } a ? Money(a) : "—";
        public string InvoiceAmountText => Money(InvoiceAmount);
        public string InvoiceDateText => Short(InvoiceDate);
        public string TechText => Short(TechHandoverDate);
        public string SapSeText => SapSeDate.HasValue ? Short(SapSeDate) : string.Empty;   // under the SE No, which already shows the dash
        public string SeNoText => string.IsNullOrWhiteSpace(ServiceEntryNo) ? "—" : ServiceEntryNo!;
        public string AccountText => Short(AccountHandoverDate);

        public static string Money(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);
        public static string Short(DateTime? d) => d is { } x ? x.ToString("dd MMM yy", CultureInfo.InvariantCulture) : "—";

        /// <summary>A detached copy for the editor - not MemberwiseClone, which would share PropertyChanged.</summary>
        public ServiceEntry Clone() => new()
        {
            Id = Id, SrNo = SrNo, PoNo = PoNo, PoAmount = PoAmount, Vendor = Vendor, Description = Description,
            InvoiceDate = InvoiceDate, InvoiceNo = InvoiceNo, InvoiceAmount = InvoiceAmount,
            TechHandoverDate = TechHandoverDate, SapSeDate = SapSeDate, ServiceEntryNo = ServiceEntryNo,
            AccountHandoverDate = AccountHandoverDate, FlagDays = FlagDays,
        };
    }

    /// <summary>The figures above the table, for the current month and the whole register.</summary>
    public sealed class ServiceEntrySummary
    {
        public int[] StageCounts { get; } = new int[4];
        public decimal OpenAmount { get; set; }
        public int LateCount { get; set; }
        public int SentThisMonth { get; set; }
        public decimal SentThisMonthAmount { get; set; }
        public int OpenCount => StageCounts[0] + StageCounts[1] + StageCounts[2];
        public int AllCount => OpenCount + StageCounts[3];
    }
}
