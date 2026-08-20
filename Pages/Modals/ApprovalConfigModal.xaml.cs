using Microsoft.Maui.Controls;
using Procure.Models;
using Procure.PageModels;

namespace Procure.Pages.Modals
{
    public partial class ApprovalConfigModal : ContentView
    {
        private Approval? _draggedApproval;
        private PrListPageModel? ViewModel => BindingContext as PrListPageModel;

        public ApprovalConfigModal()
        {
            InitializeComponent();
        }

        private void OnApprovalDragStarting(object? sender, DragStartingEventArgs e)
        {
            if (sender is Element element && element.BindingContext is Approval approval)
            {
                _draggedApproval = approval;
                e.Data.Properties["Approval"] = approval;
            }
        }

        private void OnApprovalDrop(object? sender, DropEventArgs e)
        {
            if (sender is Element element && element.BindingContext is Approval targetApproval && ViewModel != null)
            {
                var sourceApproval = _draggedApproval ?? (e.Data.Properties.TryGetValue("Approval", out var appObj) ? appObj as Approval : null);
                if (sourceApproval != null && sourceApproval != targetApproval)
                {
                    ViewModel.ReorderApprovalStages(sourceApproval, targetApproval);
                }
            }
            _draggedApproval = null;
        }
    }
}
