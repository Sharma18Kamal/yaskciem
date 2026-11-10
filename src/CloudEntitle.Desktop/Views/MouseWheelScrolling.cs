using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace CloudEntitle.Desktop.Views;

public static class MouseWheelScrolling
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(MouseWheelScrolling),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

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
                viewer.ScrollToVerticalOffset(GetOffset(viewer, args.Delta, SystemParameters.WheelScrollLines));
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
    {
        var distance = lines < 0
            ? viewer.ViewportHeight
            : lines * (viewer.CanContentScroll ? 1.0 : 16.0);
        return Math.Clamp(viewer.VerticalOffset - delta / 120.0 * distance, 0, viewer.ScrollableHeight);
    }

    private static DependencyObject? GetParent(DependencyObject element) =>
        element is Visual or Visual3D
            ? VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element)
            : element is FrameworkContentElement content
                ? content.Parent
                : LogicalTreeHelper.GetParent(element);
}
