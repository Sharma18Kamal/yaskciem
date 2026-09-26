using System.Windows;
using CloudEntitle.Desktop.ViewModels;

namespace CloudEntitle.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
