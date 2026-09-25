using System.Windows;

namespace CloudEntitle.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new DashboardViewModel();
    }
}

public class DashboardViewModel
{
    public int TotalCloudAccounts { get; set; }
    public int HighRiskFindings { get; set; }
    public int PrivilegedIdentities { get; set; }
    public int CriticalFindings { get; set; }
}
