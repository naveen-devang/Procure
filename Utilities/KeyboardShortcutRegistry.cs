using System.Collections.Generic;
using System.Linq;

namespace Procure.Utilities
{
    // Pure data - no WinUI types here, so both the settings service and the Settings page's list
    // can use it without pulling in platform-specific dependencies.
    public record KeyboardShortcutDefinition(string Id, string DisplayName, string Scope, string DefaultCombo);

    public static class KeyboardShortcutIds
    {
        public const string GoDashboard = "Global.GoDashboard";
        public const string GoPrBoard = "Global.GoPrBoard";
        public const string GoColumns = "Global.GoColumns";
        public const string GoMaterials = "Global.GoMaterials";
        public const string GoSettings = "Global.GoSettings";
        public const string ToggleSidebar = "Global.ToggleSidebar";

        public const string FocusSearch = "PrBoard.FocusSearch";
        public const string NewPr = "PrBoard.NewPr";
        public const string RefreshBoard = "PrBoard.Refresh";
        public const string ExportCsv = "PrBoard.ExportCsv";

        public const string ModalSave = "Modal.Save";
        public const string ModalSelectAll = "Modal.SelectAll";
        public const string ModalPaste = "Modal.Paste";

        public const string PcrPrint = "PcrPreview.Print";
        public const string PcrPrevPage = "PcrPreview.PrevPage";
        public const string PcrNextPage = "PcrPreview.NextPage";
        public const string PcrZoomIn = "PcrPreview.ZoomIn";
        public const string PcrZoomOut = "PcrPreview.ZoomOut";
    }

    // Single source of truth for every shortcut's default binding, display name, and grouping -
    // both KeyboardShortcutService (defaults) and the Settings page (the editable list) read this.
    public static class KeyboardShortcutRegistry
    {
        public static readonly IReadOnlyList<KeyboardShortcutDefinition> All = new List<KeyboardShortcutDefinition>
        {
            new(KeyboardShortcutIds.GoDashboard, "Go to Dashboard", "Global", "Ctrl+1"),
            new(KeyboardShortcutIds.GoPrBoard, "Go to PR Board", "Global", "Ctrl+2"),
            new(KeyboardShortcutIds.GoColumns, "Go to Custom Columns", "Global", "Ctrl+3"),
            new(KeyboardShortcutIds.GoMaterials, "Go to Raw & Packing", "Global", "Ctrl+Number4"),
            new(KeyboardShortcutIds.GoSettings, "Go to Settings", "Global", "Ctrl+Comma"),
            new(KeyboardShortcutIds.ToggleSidebar, "Toggle Sidebar", "Global", "Ctrl+B"),

            new(KeyboardShortcutIds.FocusSearch, "Focus Search", "PR Board", "Ctrl+F"),
            new(KeyboardShortcutIds.NewPr, "Add PR", "PR Board", "Ctrl+N"),
            new(KeyboardShortcutIds.RefreshBoard, "Refresh Board", "PR Board", "F5"),
            new(KeyboardShortcutIds.ExportCsv, "Export CSV", "PR Board", "Ctrl+E"),

            new(KeyboardShortcutIds.ModalSave, "Save (active dialog)", "Dialogs", "Ctrl+S"),
            new(KeyboardShortcutIds.ModalSelectAll, "Select All (active dialog)", "Dialogs", "Ctrl+A"),
            new(KeyboardShortcutIds.ModalPaste, "Paste Rows (Batch Create)", "Dialogs", "Ctrl+V"),

            new(KeyboardShortcutIds.PcrPrint, "Print", "PDF Preview", "Ctrl+P"),
            new(KeyboardShortcutIds.PcrPrevPage, "Previous Page", "PDF Preview", "Left"),
            new(KeyboardShortcutIds.PcrNextPage, "Next Page", "PDF Preview", "Right"),
            new(KeyboardShortcutIds.PcrZoomIn, "Zoom In", "PDF Preview", "Ctrl+Plus"),
            new(KeyboardShortcutIds.PcrZoomOut, "Zoom Out", "PDF Preview", "Ctrl+Minus"),
        };

        public static KeyboardShortcutDefinition Get(string id) => All.First(d => d.Id == id);
    }
}
