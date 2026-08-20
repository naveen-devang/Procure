using Microsoft.Maui.Controls;

namespace Procure.Utilities
{
    /// <summary>
    /// Builds its content the first time <see cref="IsExpanded"/> turns true, then only toggles
    /// visibility. IsVisible="False" keeps an element in the visual tree, so a plain IsVisible
    /// binding still creates the MAUI element, its handler and its native WinUI control; this does
    /// not. BindingContext flows to the created content through normal parenting, so every binding
    /// inside <see cref="ContentTemplate"/> works unchanged.
    /// ponytail: content is never torn down again once built - collapsing only hides it. Ceiling is
    /// peak memory for cards the user actually opened; add a release-on-collapse if that ever bites.
    /// </summary>
    public sealed class LazyExpander : ContentView
    {
        public static readonly BindableProperty IsExpandedProperty = BindableProperty.Create(
            nameof(IsExpanded), typeof(bool), typeof(LazyExpander), false, propertyChanged: OnIsExpandedChanged);

        public static readonly BindableProperty ContentTemplateProperty = BindableProperty.Create(
            nameof(ContentTemplate), typeof(DataTemplate), typeof(LazyExpander));

        public LazyExpander() => IsVisible = false;

        public bool IsExpanded
        {
            get => (bool)GetValue(IsExpandedProperty);
            set => SetValue(IsExpandedProperty, value);
        }

        public DataTemplate? ContentTemplate
        {
            get => (DataTemplate?)GetValue(ContentTemplateProperty);
            set => SetValue(ContentTemplateProperty, value);
        }

        private static void OnIsExpandedChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var expander = (LazyExpander)bindable;
            var expanded = (bool)newValue;

            if (expanded && expander.Content is null && expander.ContentTemplate is not null)
                expander.Content = (View)expander.ContentTemplate.CreateContent();

            expander.IsVisible = expanded;
        }
    }
}
