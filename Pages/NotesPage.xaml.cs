using System;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Procure.Controls;
using Procure.PageModels;

namespace Procure.Pages
{
    public partial class NotesPage : ContentPage
    {
        private readonly NotePageModel _viewModel;
        private readonly Procure.Services.IKeyboardShortcutService _shortcuts;

        public NotesPage(NotePageModel viewModel, Procure.Services.IKeyboardShortcutService shortcuts)
        {
            InitializeComponent();
            BindingContext = _viewModel = viewModel;
            _shortcuts = shortcuts;

            _viewModel.EditorLoadRequested += rtf =>
                MainThread.BeginInvokeOnMainThread(() => Editor.Load(rtf));
            Editor.ContentChanged += (_, e) => _viewModel.OnBodyEdited(e.Rtf, e.PlainText);
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
#if WINDOWS
            Procure.Utilities.NativeTheme.ForceRepaintOnAppear(this);
#endif
            await _viewModel.LoadListAsync();
        }

        protected override async void OnDisappearing()
        {
            base.OnDisappearing();
            await _viewModel.FlushPendingAsync();
        }

        private void OnBold(object? s, EventArgs e) => Editor.Apply(NoteFormat.Bold);
        private void OnItalic(object? s, EventArgs e) => Editor.Apply(NoteFormat.Italic);
        private void OnUnderline(object? s, EventArgs e) => Editor.Apply(NoteFormat.Underline);
        private void OnStrike(object? s, EventArgs e) => Editor.Apply(NoteFormat.Strike);
        private void OnBullet(object? s, EventArgs e) => Editor.Apply(NoteFormat.BulletList);
        private void OnNumber(object? s, EventArgs e) => Editor.Apply(NoteFormat.NumberList);
        private void OnChecklist(object? s, EventArgs e) => Editor.Apply(NoteFormat.Checklist);
        private void OnH1(object? s, EventArgs e) => Editor.Apply(NoteFormat.Heading1);
        private void OnH2(object? s, EventArgs e) => Editor.Apply(NoteFormat.Heading2);
        private void OnBody(object? s, EventArgs e) => Editor.Apply(NoteFormat.Body);
        private void OnUndo(object? s, EventArgs e) => Editor.Apply(NoteFormat.Undo);
        private void OnRedo(object? s, EventArgs e) => Editor.Apply(NoteFormat.Redo);

#if WINDOWS
        protected override void OnHandlerChanged()
        {
            base.OnHandlerChanged();
            if (Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement root)
            {
                root.PreviewKeyDown -= OnPagePreviewKeyDown;
                root.PreviewKeyDown += OnPagePreviewKeyDown;
            }
        }

        protected override void OnHandlerChanging(HandlerChangingEventArgs args)
        {
            base.OnHandlerChanging(args);
            if (args.NewHandler is null && args.OldHandler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement root)
                root.PreviewKeyDown -= OnPagePreviewKeyDown;
        }

        private void OnPagePreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (Procure.Utilities.ShortcutInput.Matches(_shortcuts.GetCombo(Procure.Utilities.KeyboardShortcutIds.NotesNew), e.Key))
            {
                _viewModel.NewNoteCommand.Execute(null);
                e.Handled = true;
            }
        }
#endif
    }
}
