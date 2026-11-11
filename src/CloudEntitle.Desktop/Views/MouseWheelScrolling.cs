using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Animation;
using System.Runtime.CompilerServices;

namespace CloudEntitle.Desktop.Views;

public static class MouseWheelScrolling
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(MouseWheelScrolling),
        new PropertyMetadata(false, OnIsEnabledChanged));
    public static readonly DependencyProperty IsSmoothEnabledProperty = DependencyProperty.RegisterAttached(
        "IsSmoothEnabled", typeof(bool), typeof(MouseWheelScrolling), new PropertyMetadata(false));
    private static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.RegisterAttached(
        "AnimatedOffset", typeof(double), typeof(MouseWheelScrolling),
        new PropertyMetadata(0.0, OnAnimatedOffsetChanged));
    private static readonly ConditionalWeakTable<ScrollViewer, ScrollState> ScrollStates = new();

    private sealed class ScrollState
    {
        public bool IsAnimating { get; set; }
        public double Target { get; set; }
        public int Generation { get; set; }
    }

    private static void OnAnimatedOffsetChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        var viewer = (ScrollViewer)element;
        if (ScrollStates.TryGetValue(viewer, out var state) && state.IsAnimating)
            viewer.ScrollToVerticalOffset((double)args.NewValue);
    }

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsSmoothEnabled(DependencyObject element) => (bool)element.GetValue(IsSmoothEnabledProperty);
    public static void SetIsSmoothEnabled(DependencyObject element, bool value) => element.SetValue(IsSmoothEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not UIElement root) return;
        if ((bool)args.OldValue) root.PreviewMouseWheel -= OnPreviewMouseWheel;
        if ((bool)args.NewValue) root.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (args.Handled || args.Delta == 0) return;
        var closedComboBox = false;
        for (var element = args.OriginalSource as DependencyObject; element is not null; element = GetParent(element))
        {
            if (element is ComboBox comboBox)
            {
                if (comboBox.IsDropDownOpen) return;
                closedComboBox = true;
            }

            if (element is ScrollViewer viewer && viewer.IsEnabled
                && viewer.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled
                && CanScroll(viewer, args.Delta))
            {
                // Keep native logical scrolling/virtualization inside tables; hand off only at their edges.
                if (viewer.CanContentScroll && !closedComboBox) return;
                Scroll(viewer, args.Delta, SystemParameters.WheelScrollLines,
                    sender is DependencyObject root && GetIsSmoothEnabled(root) && SystemParameters.ClientAreaAnimation);
                args.Handled = true;
                return;
            }
            if (ReferenceEquals(element, sender)) break;
        }

        // A closed selector must not change its value when the containing page reaches an edge.
        if (closedComboBox) args.Handled = true;
    }

    private static bool CanScroll(ScrollViewer viewer, int delta) =>
        delta > 0 ? viewer.VerticalOffset > 0 : viewer.VerticalOffset < viewer.ScrollableHeight;

    internal static double GetOffset(ScrollViewer viewer, int delta, int lines)
        => GetOffset(viewer, viewer.VerticalOffset, delta, lines);

    private static double GetOffset(ScrollViewer viewer, double current, int delta, int lines)
    {
        var distance = lines < 0
            ? viewer.ViewportHeight
            : lines * (viewer.CanContentScroll ? 1.0 : 16.0);
        return Math.Clamp(current - delta / 120.0 * distance, 0, viewer.ScrollableHeight);
    }

    internal static void Scroll(ScrollViewer viewer, int delta, int lines, bool smooth)
    {
        if (!smooth || viewer.CanContentScroll)
        {
            StopAnimation(viewer);
            viewer.ScrollToVerticalOffset(GetOffset(viewer, delta, lines));
            return;
        }
        var state = ScrollStates.GetValue(viewer, _ =>
        {
            viewer.PreviewMouseDown += (_, _) => StopAnimation(viewer);
            viewer.PreviewKeyDown += (_, _) => StopAnimation(viewer);
            viewer.Unloaded += (_, _) => StopAnimation(viewer);
            return new ScrollState();
        });
        state.Target = GetOffset(viewer, state.IsAnimating ? state.Target : viewer.VerticalOffset, delta, lines);
        var animation = new DoubleAnimation(viewer.VerticalOffset, state.Target, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        state.IsAnimating = true;
        var generation = ++state.Generation;
        animation.Completed += (_, _) =>
        {
            if (!state.IsAnimating || state.Generation != generation) return;
            state.IsAnimating = false;
            viewer.SetValue(AnimatedOffsetProperty, state.Target);
            viewer.BeginAnimation(AnimatedOffsetProperty, null);
            viewer.ScrollToVerticalOffset(state.Target);
        };
        viewer.BeginAnimation(AnimatedOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    internal static void StopAnimation(ScrollViewer viewer)
    {
        if (!ScrollStates.TryGetValue(viewer, out var state) || !state.IsAnimating) return;
        state.IsAnimating = false;
        state.Generation++;
        viewer.SetValue(AnimatedOffsetProperty, viewer.VerticalOffset);
        viewer.BeginAnimation(AnimatedOffsetProperty, null);
    }

    private static DependencyObject? GetParent(DependencyObject element) =>
        element is Visual or Visual3D
            ? VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element)
            : element is FrameworkContentElement content
                ? content.Parent
                : LogicalTreeHelper.GetParent(element);
}
