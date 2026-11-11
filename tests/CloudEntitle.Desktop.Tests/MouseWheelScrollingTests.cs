using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CloudEntitle.Desktop.Views;
using Xunit;

namespace CloudEntitle.Desktop.Tests;

[Collection("WPF rendering")]
public sealed class MouseWheelScrollingTests
{
    [Fact]
    public void Tables_ShouldKeepNativeScrollingAndHandOffAtTheEnd()
    {
        RunSta(() =>
        {
            var table = new DataGrid
            {
                Height = 140, CanUserAddRows = false,
                ItemsSource = Enumerable.Range(1, 100).Select(number => new { Number = number }).ToArray()
            };
            var panel = new StackPanel();
            panel.Children.Add(table);
            panel.Children.Add(new Border { Height = 1500 });
            var outer = new ScrollViewer { Content = panel };
            WithWindow(outer, () =>
            {
                var viewer = FindViewer(table);
                Assert.True(viewer.CanContentScroll);
                var wheel = Wheel(viewer, -120);
                Assert.False(wheel.Handled);
                Assert.True(viewer.VerticalOffset > 0);
                Assert.Equal(0, outer.VerticalOffset);
                viewer.ScrollToBottom();
                Pump();
                Assert.True(Wheel(viewer, -120).Handled);
                Assert.True(outer.VerticalOffset > 0);
                viewer.ScrollToTop();
                outer.ScrollToVerticalOffset(100);
                Pump();
                var previous = outer.VerticalOffset;
                Wheel(viewer, 120);
                Assert.True(outer.VerticalOffset < previous);
            });
        });
    }

    [Fact]
    public void EmptyTable_ShouldScrollThePage()
    {
        RunSta(() =>
        {
            var table = new DataGrid { Height = 140, CanUserAddRows = false };
            var panel = new StackPanel();
            panel.Children.Add(table);
            panel.Children.Add(new Border { Height = 1500 });
            var outer = new ScrollViewer { Content = panel };
            WithWindow(outer, () =>
            {
                Wheel(FindViewer(table), -120);
                Assert.True(outer.VerticalOffset > 0);
            });
        });
    }

    [Fact]
    public void Sidebar_ShouldScrollOnlyTheViewerUnderThePointer()
    {
        RunSta(() =>
        {
            var sidebarContent = new Border { Height = 1500 };
            var sidebar = new ScrollViewer { Width = 100, Content = sidebarContent };
            var page = new ScrollViewer { Width = 200, Content = new Border { Height = 1500 } };
            var columns = new StackPanel { Orientation = Orientation.Horizontal };
            columns.Children.Add(sidebar);
            columns.Children.Add(page);
            var outer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = columns
            };
            WithWindow(outer, () =>
            {
                Wheel(sidebarContent, -120);
                Assert.True(sidebar.VerticalOffset > 0);
                Assert.Equal(0, page.VerticalOffset);
            });
        });
    }

    [Fact]
    public void RoutedScrolling_ShouldStillNotifyHandledEventActivityListeners()
    {
        RunSta(() =>
        {
            var content = new Border { Height = 1500 };
            var viewer = new ScrollViewer { Content = content };
            WithWindow(viewer, () =>
            {
                var window = Window.GetWindow(viewer);
                var activity = 0;
                window.AddHandler(Mouse.PreviewMouseWheelEvent,
                    new MouseWheelEventHandler((_, _) => activity++), handledEventsToo: true);
                Assert.True(Wheel(content, -120).Handled);
                Assert.Equal(1, activity);
            });
        });
    }

    [Fact]
    public void Wheel_ShouldScrollNestedContentThenHandOffAtBothEdges()
    {
        RunSta(() =>
        {
            var innerContent = new Border { Height = 800 };
            var inner = new ScrollViewer { Height = 120, Content = innerContent };
            var panel = new StackPanel();
            panel.Children.Add(inner);
            panel.Children.Add(new Border { Height = 1500 });
            var outer = new ScrollViewer { Content = panel };
            WithWindow(outer, () =>
            {
                Wheel(innerContent, -120);
                Assert.True(inner.VerticalOffset > 0);
                Assert.Equal(0, outer.VerticalOffset);

                inner.ScrollToBottom();
                Pump();
                Wheel(innerContent, -120);
                Assert.True(outer.VerticalOffset > 0);

                inner.ScrollToTop();
                Pump();
                var previous = outer.VerticalOffset;
                Wheel(innerContent, 120);
                Assert.True(outer.VerticalOffset < previous);
                Assert.Equal(0, inner.VerticalOffset);
            });
        });
    }

    [Fact]
    public void ClosedSelectorAndHorizontalOnlyViewer_ShouldNotTrapPageScrolling()
    {
        RunSta(() =>
        {
            var combo = new ComboBox { ItemsSource = new[] { "First", "Second", "Third" }, SelectedIndex = 1 };
            var horizontal = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Border { Width = 1500, Height = 30 }
            };
            var panel = new StackPanel();
            panel.Children.Add(combo);
            panel.Children.Add(horizontal);
            panel.Children.Add(new Border { Height = 1500 });
            var outer = new ScrollViewer { Content = panel };
            WithWindow(outer, () =>
            {
                Wheel(combo, -120);
                Assert.True(outer.VerticalOffset > 0);
                Assert.Equal(1, combo.SelectedIndex);
                var previous = outer.VerticalOffset;
                Wheel(horizontal, -120);
                Assert.True(outer.VerticalOffset > previous);
                outer.ScrollToBottom();
                Pump();
                Assert.True(Wheel(combo, -120).Handled);
                Assert.Equal(1, combo.SelectedIndex);
            });
        });
    }

    [Fact]
    public void MultilineEditor_ShouldScrollItselfThenThePage()
    {
        RunSta(() =>
        {
            var editor = new TextBox
            {
                Height = 100, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Text = string.Join(Environment.NewLine, Enumerable.Range(1, 100))
            };
            var panel = new StackPanel();
            panel.Children.Add(editor);
            panel.Children.Add(new Border { Height = 1500 });
            var outer = new ScrollViewer { Content = panel };
            WithWindow(outer, () =>
            {
                var editorViewer = FindViewer(editor);
                Wheel(editorViewer, -120);
                Assert.True(editorViewer.VerticalOffset > 0);
                Assert.Equal(0, outer.VerticalOffset);
                editor.ScrollToEnd();
                Pump();
                Wheel(editorViewer, -120);
                Assert.True(outer.VerticalOffset > 0);
            });
        });
    }

    [Fact]
    public void Offset_ShouldRespectWheelSettingsAndPartialDeltas()
    {
        RunSta(() =>
        {
            var viewer = new ScrollViewer { Content = new Border { Height = 2000 } };
            WithWindow(viewer, () =>
            {
                Assert.Equal(48, MouseWheelScrolling.GetOffset(viewer, -120, 3));
                Assert.Equal(12, MouseWheelScrolling.GetOffset(viewer, -30, 3));
                Assert.Equal(0, MouseWheelScrolling.GetOffset(viewer, -120, 0));
                Assert.Equal(viewer.ViewportHeight, MouseWheelScrolling.GetOffset(viewer, -120, -1));
                Assert.Equal(0, MouseWheelScrolling.GetOffset(viewer, 120, 3));
                viewer.ScrollToBottom();
                Pump();
                Assert.Equal(viewer.ScrollableHeight, MouseWheelScrolling.GetOffset(viewer, -120, 3));
            });
        });
    }

    [Fact]
    public void OpenSelector_ShouldKeepNativeWheelBehavior()
    {
        RunSta(() =>
        {
            var combo = new ComboBox { ItemsSource = Enumerable.Range(1, 50).ToArray() };
            var panel = new StackPanel();
            panel.Children.Add(combo);
            panel.Children.Add(new Border { Height = 1500 });
            var outer = new ScrollViewer { Content = panel };
            WithWindow(outer, () =>
            {
                combo.IsDropDownOpen = true;
                Pump();
                Assert.False(Wheel(combo, -120).Handled);
                Assert.Equal(0, outer.VerticalOffset);
                combo.IsDropDownOpen = false;
            });
        });
    }

    [Fact]
    public void SmoothScrolling_ShouldAccumulateRapidInputAndKeepTheFinalOffset()
    {
        RunSta(() =>
        {
            var viewer = new ScrollViewer { Content = new Border { Height = 2000 } };
            WithWindow(viewer, () =>
            {
                MouseWheelScrolling.Scroll(viewer, -120, 3, smooth: true);
                MouseWheelScrolling.Scroll(viewer, -120, 3, smooth: true);
                WaitForAnimation();
                Assert.Equal(96, viewer.VerticalOffset, 1);
                WaitForAnimation();
                Assert.Equal(96, viewer.VerticalOffset, 1);
                MouseWheelScrolling.Scroll(viewer, 120, 3, smooth: true);
                WaitForAnimation();
                Assert.Equal(48, viewer.VerticalOffset, 1);
                viewer.ScrollToBottom();
                Pump();
                MouseWheelScrolling.Scroll(viewer, -120, 3, smooth: true);
                WaitForAnimation();
                Assert.Equal(viewer.ScrollableHeight, viewer.VerticalOffset, 1);
            });
        });
    }

    [Fact]
    public void SmoothScrolling_ShouldAllowDirectNavigationToInterruptAnimation()
    {
        RunSta(() =>
        {
            var viewer = new ScrollViewer { Content = new Border { Height = 2000 } };
            WithWindow(viewer, () =>
            {
                MouseWheelScrolling.Scroll(viewer, -120, 3, smooth: true);
                MouseWheelScrolling.StopAnimation(viewer);
                viewer.ScrollToVerticalOffset(300);
                Pump();
                WaitForAnimation();
                Assert.Equal(300, viewer.VerticalOffset, 1);
                MouseWheelScrolling.Scroll(viewer, -30, 0, smooth: false);
                Pump();
                Assert.Equal(300, viewer.VerticalOffset, 1);
            });
        });
    }

    private static void WaitForAnimation()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Pump();
    }

    private static MouseWheelEventArgs Wheel(UIElement source, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = Mouse.PreviewMouseWheelEvent
        };
        source.RaiseEvent(args);
        if (!args.Handled)
            source.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
            {
                RoutedEvent = Mouse.MouseWheelEvent
            });
        Pump();
        return args;
    }

    private static void WithWindow(ScrollViewer content, Action test)
    {
        var window = new Window { Width = 400, Height = 300, Content = content, ShowInTaskbar = false };
        MouseWheelScrolling.SetIsEnabled(window, true);
        try
        {
            window.Show();
            Pump();
            test();
        }
        finally { window.Close(); }
    }

    private static ScrollViewer FindViewer(DependencyObject root)
        => FindViewerOrNull(root) ?? throw new InvalidOperationException("Expected a rendered ScrollViewer.");

    private static ScrollViewer? FindViewerOrNull(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var result = FindViewerOrNull(VisualTreeHelper.GetChild(root, index));
            if (result is not null) return result;
        }
        return null;
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Mouse wheel regression exceeded 30 seconds.");
        Assert.Null(failure);
    }
}
