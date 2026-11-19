using System.Collections.ObjectModel;
using Procure.Models;
using Procure.Utilities;

namespace Procure.PageModels
{
    // The Sl No. beside each line in the editing dialogs, kept equal to what the line becomes on the
    // PCR sheet / in Copy for email. Each list is followed through every add, remove, reorder, paste
    // and replacement (a combined Batch RFQ, a merged PR reopened), so no caller has to renumber.
    public partial class PrListPageModel
    {
        // Quote lines: Copy for email numbers the ticked lines that have a name.
        private static bool CountsAsQuoteLine(RfqItem i) => i.IsQuoted && !string.IsNullOrWhiteSpace(i.ItemName);

        private readonly LineNumbers<RfqItem> _rfqLineNumbers =
            new(CountsAsQuoteLine, (i, no) => i.LineNo = no, nameof(RfqItem.IsQuoted), nameof(RfqItem.ItemName));
        private readonly LineNumbers<RfqItem> _batchRfqLineNumbers =
            new(CountsAsQuoteLine, (i, no) => i.LineNo = no, nameof(RfqItem.IsQuoted), nameof(RfqItem.ItemName));

        // PR lines: Save keeps the lines that have a name.
        private readonly LineNumbers<PrItem> _prLineNumbers =
            new(i => !string.IsNullOrWhiteSpace(i.ItemName), (i, no) => i.LineNo = no, nameof(PrItem.ItemName));

        // PCR items: the sheet numbers the ticked items in this order.
        private readonly LineNumbers<ExportItemSelection> _pcrLineNumbers =
            new(s => s.IsSelected, (s, no) => s.LineNo = no, nameof(ExportItemSelection.IsSelected));

        private void AttachLineNumbers()
        {
            _rfqLineNumbers.Attach(EditingRfqItems);
            _batchRfqLineNumbers.Attach(BatchEditingRfqItems);
            _prLineNumbers.Attach(EditingPrItems);
            _pcrLineNumbers.Attach(ExportItemSelections);
        }

        partial void OnEditingRfqItemsChanged(ObservableCollection<RfqItem> value) => _rfqLineNumbers.Attach(value);
        partial void OnBatchEditingRfqItemsChanged(ObservableCollection<RfqItem> value) => _batchRfqLineNumbers.Attach(value);
        partial void OnEditingPrItemsChanged(ObservableCollection<PrItem> value) => _prLineNumbers.Attach(value);
        partial void OnExportItemSelectionsChanged(ObservableCollection<ExportItemSelection> value) => _pcrLineNumbers.Attach(value);
    }
}
