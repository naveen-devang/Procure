using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    public partial class PoItemCallOff : ObservableObject
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid PoItemId { get; set; }

        [ObservableProperty]
        public partial DateTime CallOffDate { get; set; } = DateTime.Today;

        [ObservableProperty]
        public partial decimal Quantity { get; set; }

        [ObservableProperty]
        public partial string? Note { get; set; }
    }
}
