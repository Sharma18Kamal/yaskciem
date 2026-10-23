using CloudEntitle.Desktop.ViewModels;

namespace CloudEntitle.Desktop.Services;

public interface INavigationService
{
    object CurrentViewModel { get; }
    event EventHandler? CurrentViewModelChanged;
    void NavigateTo<TViewModel>() where TViewModel : class;
}

public sealed class NavigationService : INavigationService
{
    private readonly Dictionary<Type, object> _viewModels = new();
    private readonly IServiceProvider _services;
    private object _currentViewModel;

    public NavigationService(IServiceProvider services)
    {
        _services = services;
        _currentViewModel = GetOrCreate<DashboardViewModel>();
    }

    public object CurrentViewModel => _currentViewModel;
    public event EventHandler? CurrentViewModelChanged;

    public void NavigateTo<TViewModel>() where TViewModel : class
    {
        var next = GetOrCreate<TViewModel>();
        if (ReferenceEquals(_currentViewModel, next)) return;
        _currentViewModel = next;
        CurrentViewModelChanged?.Invoke(this, EventArgs.Empty);
    }

    private TViewModel GetOrCreate<TViewModel>() where TViewModel : class
    {
        if (_viewModels.TryGetValue(typeof(TViewModel), out var existing))
        {
            return (TViewModel)existing;
        }

        var viewModel = (TViewModel)_services.GetService(typeof(TViewModel))!;
        _viewModels[typeof(TViewModel)] = viewModel;
        return viewModel;
    }
}
