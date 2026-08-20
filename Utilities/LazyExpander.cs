using System;
using Microsoft.Maui.Controls;

namespace Procure.Utilities
{
    /// <summary>
    /// Builds its content the first time <see cref="IsExpanded"/> turns true, then only toggles
    /// visibility. IsVisible="False" keeps an element in the visual tree, so a plain IsVisible
    /// binding still creates the MAUI element, its handler and its native WinUI control; this does
    /// not. BindingContext flows to the created content through normal parenting, so every binding
    /// inside <see cref="ContentTemplate"/> works unchanged.
    ///
    /// When <see cref="PlaceholderTemplate"/> is set, the placeholder is shown first and the real
    /// content is built on a later tick. Building a data-heavy panel is one synchronous block on the
    /// UI thread, so nothing can paint while it runs - giving the placeholder its own frame first is
    /// the only way the click reads as "working" instead of "hung".
    ///
    /// ponytail: content is never torn down again once built - collapsing only hides it. Ceiling is
    /// peak memory for cards the user actually opened; add a release-on-collapse if that ever bites.
    /// </summary>
    public sealed class LazyExpander : ContentView
    {
        // Long enough for the placeholder to be committed and composited (a frame is ~16ms at 60Hz,
        // ~8ms at 120Hz) before the UI thread is taken for the content build. Too short and the
        // placeholder never actually reaches the screen, which defeats the point.
        private static readonly TimeSpan PlaceholderPaintDelay = TimeSpan.FromMilliseconds(80);

        public static readonly BindableProperty IsExpandedProperty = BindableProperty.Create(
            nameof(IsExpanded), typeof(bool), typeof(LazyExpander), false, propertyChanged: OnIsExpandedChanged);

        public static readonly BindableProperty ContentTemplateProperty = BindableProperty.Create(
            nameof(ContentTemplate), typeof(DataTemplate), typeof(LazyExpander));

        public static readonly BindableProperty PlaceholderTemplateProperty = BindableProperty.Create(
            nameof(PlaceholderTemplate), typeof(DataTemplate), typeof(LazyExpander));

        private bool _contentBuilt;

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

        /// <summary>Optional lightweight stand-in shown while the real content is being built.</summary>
        public DataTemplate? PlaceholderTemplate
        {
            get => (DataTemplate?)GetValue(PlaceholderTemplateProperty);
            set => SetValue(PlaceholderTemplateProperty, value);
        }

        private static void OnIsExpandedChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var expander = (LazyExpander)bindable;

            if (!(bool)newValue)
            {
                expander.IsVisible = false;
                return;
            }

            if (expander._contentBuilt || expander.ContentTemplate is null)
            {
                expander.IsVisible = true;
                return;
            }

            if (expander.PlaceholderTemplate is null)
            {
                expander.BuildContent();
                expander.IsVisible = true;
                return;
            }

            // Paint the placeholder this frame, build the real panel in a later one.
            expander.Content = (View)expander.PlaceholderTemplate.CreateContent();
            expander.IsVisible = true;
            expander.Dispatcher.DispatchDelayed(PlaceholderPaintDelay, () =>
            {
                if (expander.IsExpanded)
                {
                    expander.BuildContent();
                }
                else
                {
                    // Collapsed again before we got here - drop the placeholder.
                    expander.Content = null;
                }
            });
        }

        private void BuildContent()
        {
            if (_contentBuilt || ContentTemplate is null) return;
            _contentBuilt = true;
            Content = (View)ContentTemplate.CreateContent();
        }
    }
}
