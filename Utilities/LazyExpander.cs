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
    /// Hosted inside a virtualizing CollectionView, so containers are recycled: one of these may show
    /// PR 5 and later PR 205. That is fine, and is why the ceiling below stopped mattering. Everything
    /// in <see cref="ContentTemplate"/> binds to BindingContext, so recycling rebinds it; IsExpanded
    /// re-evaluates against the new row, hiding or showing accordingly; and a placeholder still in
    /// flight when a container is recycled checks IsExpanded again before building, so it either builds
    /// for the new row or drops itself.
    ///
    /// By default content is never torn down again once built - collapsing only hides it. Under
    /// recycling that bounds peak memory at roughly (containers on screen x panel size) rather than at
    /// every card ever opened. Set <see cref="AutoReleaseDelay"/> to trade that back: a card collapsed
    /// and left alone that long releases its content and rebuilds from scratch (same two-phase
    /// placeholder-then-build flow) if reopened. Unset by default, so every other user of this
    /// component - the ten modals, the loading skeleton - is unaffected.
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

        /// <summary>Null (default) means never release, matching every prior use of this component.
        /// Set on the one usage that wraps a PR card's detail panel; the modals and the skeleton leave
        /// this unset.</summary>
        public static readonly BindableProperty AutoReleaseDelayProperty = BindableProperty.Create(
            nameof(AutoReleaseDelay), typeof(TimeSpan?), typeof(LazyExpander), (TimeSpan?)null);

        private bool _contentBuilt;

        // Bumped on every collapse-armed-for-release and every recycle (BindingContext change). A
        // release callback captures the generation it was scheduled under; if the number has moved by
        // the time it fires, it targets a different row (or the same row re-expanded) and does nothing.
        // This is the one piece of complexity a release-on-collapse needed that made it not worth doing
        // before - containers here are recycled by the CollectionView above, so "10 seconds after this
        // container collapsed" and "10 seconds after this PR's card collapsed" are different questions,
        // and only the second one is the intended behavior.
        private int _generation;

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

        public TimeSpan? AutoReleaseDelay
        {
            get => (TimeSpan?)GetValue(AutoReleaseDelayProperty);
            set => SetValue(AutoReleaseDelayProperty, value);
        }

        /// <summary>Test seam for BoardMemorySelfCheck.</summary>
        internal bool HasBuiltContentForTest => _contentBuilt;

        protected override void OnBindingContextChanged()
        {
            base.OnBindingContextChanged();
            // A different row now owns this recycled container - any release timer already in flight
            // was scheduled for whatever row was here before and must not act on this one.
            unchecked { _generation++; }
        }

        private static void OnIsExpandedChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var expander = (LazyExpander)bindable;

            if (!(bool)newValue)
            {
                expander.IsVisible = false;
                expander.ArmAutoRelease();
                return;
            }

            // Reopened - this generation's collapse is moot, and a same-generation release callback
            // (already in flight, already past its IsExpanded check) still must not fire.
            unchecked { expander._generation++; }

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

        /// <summary>Schedules a release for the current generation, no-op if <see cref="AutoReleaseDelay"/>
        /// is unset. The callback re-checks both the generation and IsExpanded before acting, so a
        /// recycle or a reopen in the meantime silently cancels it - there is nothing to unschedule.</summary>
        private void ArmAutoRelease()
        {
            var delay = AutoReleaseDelay;
            if (delay is null) return;

            // Dispatcher is only populated once the element is attached to a live page/handler; a
            // standalone instance (as in BoardMemorySelfCheck) falls back to the current-thread
            // dispatcher, same as ShowToast elsewhere in this codebase.
            var dispatcher = Dispatcher ?? Microsoft.Maui.Dispatching.Dispatcher.GetForCurrentThread();
            if (dispatcher is null) return;

            var generation = _generation;
            dispatcher.DispatchDelayed(delay.Value, () =>
            {
                if (_generation != generation || IsExpanded) return;
                Release();
            });
        }

        /// <summary>Drops built content back to the pre-first-expand state. The next IsExpanded=true
        /// runs BuildContent (and the placeholder flow, if configured) exactly as it would on a card
        /// that was never opened this session.</summary>
        private void Release()
        {
            if (!_contentBuilt) return;
            _contentBuilt = false;
            Content = null;
        }
    }
}
