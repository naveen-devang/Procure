using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Procure.Spike.Views;

namespace Procure.Spike;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        ContentFrame.Navigate(typeof(PrBoardPage));
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem { Tag: "board" })
            ContentFrame.Navigate(typeof(PrBoardPage));
    }
}
