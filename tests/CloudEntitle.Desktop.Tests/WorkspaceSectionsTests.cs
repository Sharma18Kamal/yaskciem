using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CloudEntitle.Desktop.Views;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

[Collection("WPF rendering")]
public sealed class WorkspaceSectionsTests
{
    [Fact]
    public void SectionNavigation_ShouldFindCollapsedContentAndOpenAncestorsWithoutExposingHiddenSections()
    {
        RunSta(() =>
        {
            var title = new TextBlock { Text = "Ticketing connection" };
            WorkspaceSections.SetIsHeading(title, true);
            var section = new Border { Child = title };
            WorkspaceSections.SetIsSection(section, true);
            var details = new Expander { Header = "Advanced settings", Content = section, IsExpanded = false };
            var hidden = new Border { Child = new TextBlock { Text = "Not authorized" }, Visibility = Visibility.Collapsed };
            WorkspaceSections.SetIsSection(hidden, true);
            var content = new StackPanel();
            content.Children.Add(new Border { Height = 1000 });
            content.Children.Add(details);
            content.Children.Add(hidden);
            var sections = WorkspaceSections.Find(content);
            Assert.Equal(new[] { "Advanced settings", "Ticketing connection" }, sections.Select(item => item.Title));
            Assert.False(details.IsExpanded);
            WorkspaceSections.Navigate(sections[1]);
            Pump();
            Assert.True(details.IsExpanded);
            Assert.Equal(Visibility.Visible, title.Visibility);
            Assert.Equal(Visibility.Collapsed, hidden.Visibility);
        });
    }

    [Fact]
    public void SectionNavigation_ShouldNotTurnDataRowsIntoNavigationSections()
    {
        RunSta(() =>
        {
            var rows = new ListBox { ItemsSource = Enumerable.Range(1, 1000) };
            Assert.Empty(WorkspaceSections.Find(rows));
        });
    }

    [Fact]
    public void SectionNavigation_ShouldSelectInactiveTabsAndPreserveInputAndVisibility()
    {
        RunSta(() =>
        {
            var input = new TextBox { Text = "Unsaved draft" };
            var heading = new TextBlock { Text = "Members" };
            WorkspaceSections.SetIsHeading(heading, true);
            var content = new StackPanel();
            content.Children.Add(heading);
            content.Children.Add(input);
            WorkspaceSections.SetIsSection(content, true);
            var first = new TabItem { Header = "Overview", Content = new TextBlock() };
            var members = new TabItem { Header = "Members & roles", Content = content };
            var hidden = new TabItem { Header = "Not authorized", Visibility = Visibility.Collapsed };
            var tabs = new TabControl { Items = { first, members, hidden }, SelectedIndex = 0 };
            var group = new TabItem { Header = "People", Content = tabs };
            var groups = new TabControl { Items = { new TabItem { Header = "Home" }, group }, SelectedIndex = 0 };
            var sections = WorkspaceSections.Find(groups);
            Assert.DoesNotContain(sections, section => section.Title == "Not authorized");
            var destination = Assert.Single(sections, section => section.Title == "Members");
            WorkspaceSections.Navigate(destination);
            Pump();
            Assert.True(group.IsSelected);
            Assert.True(members.IsSelected);
            Assert.Equal(new[] { "People", "Members & roles" }, destination.Tabs.Select(tab => tab.Header));
            Assert.Equal("Unsaved draft", input.Text);
            Assert.Equal(Visibility.Collapsed, hidden.Visibility);
        });
    }

    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void RunSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Section navigation test timed out.");
        Assert.Null(failure);
    }
}
