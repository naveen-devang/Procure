using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class CustomColumnDefinition : ObservableObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [ObservableProperty]
        public partial string Name { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string DataType { get; set; } = CustomFieldDataType.Text; // Text, Number, Date, Select

        [ObservableProperty]
        public partial string? SelectOptions { get; set; }

        [ObservableProperty]
        public partial int SortOrder { get; set; }
    }
}
