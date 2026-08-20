using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Procure.Pages.Controls
{
    public partial class MetricCard : Border
    {
        public static readonly BindableProperty TitleProperty =
            BindableProperty.Create(nameof(Title), typeof(string), typeof(MetricCard), string.Empty);

        public static readonly BindableProperty ValueProperty =
            BindableProperty.Create(nameof(Value), typeof(object), typeof(MetricCard), 0);

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public object Value
        {
            get => GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public MetricCard()
        {
            InitializeComponent();
        }
    }
}
