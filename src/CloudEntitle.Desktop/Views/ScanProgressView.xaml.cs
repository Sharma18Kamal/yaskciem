using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace CloudEntitle.Desktop.Views;

public partial class ScanProgressView : UserControl
{
    private Storyboard? _animation;

    public ScanProgressView()
    {
        InitializeComponent();
    }

    private void ScanProgress_Loaded(object sender, RoutedEventArgs e) => UpdateAnimation();
    private void ScanProgress_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateAnimation();
    private void ScanProgress_Unloaded(object sender, RoutedEventArgs e) => StopAnimation();

    private void UpdateAnimation()
    {
        if (!IsLoaded || !IsVisible)
        {
            StopAnimation();
            return;
        }
        if (_animation is not null) return;
        _animation = ((Storyboard)ScanHourglass.Resources["HourglassCycle"]).Clone();
        _animation.Begin(this, HandoffBehavior.SnapshotAndReplace, true);
    }

    private void StopAnimation()
    {
        _animation?.Remove(this);
        _animation = null;
    }
}
