using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Procure.Abstractions;
using Procure.Services;
using Procure.Utilities;

namespace Procure.Models
{
    // One row in the Settings page's shortcut list. Subscribes directly to the shared
    // IKeyboardShortcutService singleton rather than routing every update through SettingsPageModel -
    // these rows are created once and live for the app's lifetime alongside that same singleton, so
    // there's nothing to leak by not unsubscribing.
    public partial class ShortcutRowViewModel : ObservableObject
    {
        private readonly IKeyboardShortcutService _service;
        private readonly IUiDispatcher _dispatcher;

        public string Id { get; }
        public string DisplayName { get; }
        public string Scope { get; }

        [ObservableProperty]
        public partial string Combo { get; set; }

        [ObservableProperty]
        public partial bool IsRecording { get; set; }

        [ObservableProperty]
        public partial bool IsCustomized { get; set; }

        public string DisplayText => IsRecording ? "Press keys…" : Combo;

        partial void OnComboChanged(string value) => OnPropertyChanged(nameof(DisplayText));
        partial void OnIsRecordingChanged(bool value) => OnPropertyChanged(nameof(DisplayText));

        public ShortcutRowViewModel(KeyboardShortcutDefinition definition, IKeyboardShortcutService service, IUiDispatcher dispatcher)
        {
            Id = definition.Id;
            DisplayName = definition.DisplayName;
            Scope = definition.Scope;
            _service = service;
            _dispatcher = dispatcher;

            Combo = service.GetCombo(Id);
            IsCustomized = service.IsCustomized(Id);

            _service.ShortcutsChanged += OnShortcutsChanged;
            _service.RecordingActionChanged += OnRecordingChanged;
        }

        private void OnShortcutsChanged(object? sender, System.EventArgs e)
        {
            _dispatcher.Post(() =>
            {
                Combo = _service.GetCombo(Id);
                IsCustomized = _service.IsCustomized(Id);
            });
        }

        private void OnRecordingChanged(object? sender, System.EventArgs e)
        {
            _dispatcher.Post(() => IsRecording = _service.RecordingActionId == Id);
        }

        [RelayCommand]
        private void StartRecording() => _service.RecordingActionId = Id;

        [RelayCommand]
        private void ResetToDefault() => _service.ResetToDefault(Id);
    }
}
