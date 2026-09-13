using Microsoft.UI.Xaml.Controls;

namespace Procure.App.Views;

public sealed partial class StubPage : Page
{
    public StubPage() => InitializeComponent();

    public StubPage(string title) : this() => TitleText.Text = title;
}
