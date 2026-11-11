using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CloudEntitle.Desktop.Views;

public static class WorkspaceSections
{
    public static readonly DependencyProperty IsSectionProperty = DependencyProperty.RegisterAttached(
        "IsSection", typeof(bool), typeof(WorkspaceSections), new PropertyMetadata(false));
    public static readonly DependencyProperty IsHeadingProperty = DependencyProperty.RegisterAttached(
        "IsHeading", typeof(bool), typeof(WorkspaceSections), new PropertyMetadata(false));

    public static bool GetIsSection(DependencyObject element) => (bool)element.GetValue(IsSectionProperty);
    public static void SetIsSection(DependencyObject element, bool value) => element.SetValue(IsSectionProperty, value);
    public static bool GetIsHeading(DependencyObject element) => (bool)element.GetValue(IsHeadingProperty);
    public static void SetIsHeading(DependencyObject element, bool value) => element.SetValue(IsHeadingProperty, value);

    internal sealed record Section(string Title, FrameworkElement Target, IReadOnlyList<Expander> Expanders,
        IReadOnlyList<TabItem> Tabs);

    internal static IReadOnlyList<Section> Find(DependencyObject root)
    {
        var sections = new List<Section>();
        var visited = new HashSet<DependencyObject>();
        Visit(root, [], []);
        return sections;

        void Visit(DependencyObject element, IReadOnlyList<Expander> expanders, IReadOnlyList<TabItem> tabs)
        {
            if (!visited.Add(element) || element is FrameworkElement { Visibility: not Visibility.Visible }) return;
            if (element is TabControl tabControl)
            {
                foreach (var item in tabControl.Items.OfType<TabItem>()) Visit(item, expanders, tabs);
                return;
            }
            if (element is TabItem tab)
            {
                tabs = [.. tabs, tab];
                if (tab.Header is string header)
                    sections.Add(new Section(header, tab.Content as FrameworkElement ?? tab, expanders, tabs));
                if (tab.Content is DependencyObject content) Visit(content, expanders, tabs);
                return;
            }
            if (element is Expander expander)
            {
                if (expander.Header is string title && !string.IsNullOrWhiteSpace(title))
                    sections.Add(new Section(title, expander, expanders, tabs));
                expanders = [.. expanders, expander];
            }
            else if (element is FrameworkElement target && GetIsSection(element))
            {
                var heading = FindHeading(element, new HashSet<DependencyObject>());
                if (heading is not null) sections.Add(new Section(heading, target, expanders, tabs));
            }
            if (element is ItemsControl) return;
            foreach (var child in Children(element)) Visit(child, expanders, tabs);
        }
    }

    internal static void Navigate(Section section)
    {
        foreach (var tab in section.Tabs) tab.IsSelected = true;
        foreach (var expander in section.Expanders) expander.IsExpanded = true;
        if (section.Target is Expander targetExpander) targetExpander.IsExpanded = true;
        section.Target.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            Window.GetWindow(section.Target)?.UpdateLayout();
            if (section.Target.IsVisible)
                section.Target.BringIntoView(new Rect(0, 0, Math.Max(1, section.Target.ActualWidth), 1));
        }));
    }

    private static string? FindHeading(DependencyObject element, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(element)) return null;
        if (element is TextBlock heading && GetIsHeading(heading) && !string.IsNullOrWhiteSpace(heading.Text))
            return heading.Text;
        if (element is ItemsControl) return null;
        foreach (var child in Children(element))
        {
            var title = FindHeading(child, visited);
            if (title is not null) return title;
        }
        return null;
    }

    private static IEnumerable<DependencyObject> Children(DependencyObject element)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(element))
            if (child is DependencyObject dependency) yield return dependency;
        if (element is not Visual) yield break;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            yield return VisualTreeHelper.GetChild(element, index);
    }
}
