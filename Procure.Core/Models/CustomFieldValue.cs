using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class CustomFieldValue : ObservableObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid PrId { get; set; }
        public Guid ColumnId { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasValue))]
        [NotifyPropertyChangedFor(nameof(DateValue))]
        public partial string? Value { get; set; }

        // UI helper properties
        [ObservableProperty]
        public partial string ColumnName { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSelect))]
        [NotifyPropertyChangedFor(nameof(IsDate))]
        [NotifyPropertyChangedFor(nameof(IsNumber))]
        [NotifyPropertyChangedFor(nameof(IsText))]
        public partial string ColumnDataType { get; set; } = CustomFieldDataType.Text;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(OptionsList))]
        public partial string? SelectOptions { get; set; }

        public bool HasValue => !string.IsNullOrWhiteSpace(Value);

        public bool IsSelect => string.Equals(ColumnDataType, CustomFieldDataType.Select, StringComparison.OrdinalIgnoreCase);
        public bool IsDate => string.Equals(ColumnDataType, CustomFieldDataType.Date, StringComparison.OrdinalIgnoreCase);
        public bool IsNumber => string.Equals(ColumnDataType, CustomFieldDataType.Number, StringComparison.OrdinalIgnoreCase);
        public bool IsText => !IsSelect && !IsDate && !IsNumber;

        private string[]? _optionsCache;

        partial void OnSelectOptionsChanged(string? value) => _optionsCache = null;

        // Cached: a fresh array per read gave the Select picker a new ItemsSource identity on every
        // re-raise, which reset its SelectedIndex to -1 and pushed null back into Value — silently
        // clearing the custom field.
        public string[] OptionsList => _optionsCache ??= !string.IsNullOrWhiteSpace(SelectOptions)
            ? SelectOptions.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

        public DateTime DateValue
        {
            get => DateTime.TryParse(Value, out var dt) ? dt : DateTime.Today;
            set
            {
                var formatted = value.ToString("yyyy-MM-dd");
                if (Value != formatted)
                {
                    Value = formatted;
                }
            }
        }
    }
}
