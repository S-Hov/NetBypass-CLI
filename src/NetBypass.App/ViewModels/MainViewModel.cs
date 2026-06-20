using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using NetBypass.App.Infrastructure;
using NetBypass.Core.Models;
using NetBypass.Core.Services;

namespace NetBypass.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly HashSet<string> DisabledByDefault =
    [
        "guided-hacking",
        "tria-ge",
        "openbittorrent",
        "rutor",
        "pump-fun"
    ];

    private readonly HostsFileService _hostsService = new();
    private readonly SettingsService _settingsService = new();
    private HostsState _hostsState;
    private string _operationMessage = string.Empty;
    private AppPage _currentPage = AppPage.Home;

    public MainViewModel()
    {
        var modulesPath = Path.Combine(AppContext.BaseDirectory, "Modules");
        var modules = new ModuleLoader().LoadDirectory(modulesPath);
        var settings = _settingsService.Load();

        Services = new ObservableCollection<ServiceItemViewModel>(
            modules.Select(module => new ServiceItemViewModel(
                module,
                settings?.SelectedModuleIds?.Contains(module.Id)
                    ?? !DisabledByDefault.Contains(module.Id))));

        foreach (var service in Services)
            service.PropertyChanged += OnServicePropertyChanged;

        ServicesView = CollectionViewSource.GetDefaultView(Services);
        ServicesView.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(ServiceItemViewModel.Category)));

        PowerCommand = new RelayCommand(TogglePower, () => HostsState != HostsState.Corrupted);
        ApplyCommand = new RelayCommand(
            ApplySelectedServices,
            () => Services.Any(item => item.IsSelected) && HostsState != HostsState.Corrupted);
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        ClearAllCommand = new RelayCommand(() => SetAll(false));
        ShowHomeCommand = new RelayCommand(() => CurrentPage = AppPage.Home);
        ShowServicesCommand = new RelayCommand(() => CurrentPage = AppPage.Services);

        RefreshState();
    }

    public ObservableCollection<ServiceItemViewModel> Services { get; }
    public ICollectionView ServicesView { get; }
    public RelayCommand PowerCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearAllCommand { get; }
    public RelayCommand ShowHomeCommand { get; }
    public RelayCommand ShowServicesCommand { get; }

    public HostsState HostsState
    {
        get => _hostsState;
        private set
        {
            if (!SetProperty(ref _hostsState, value))
                return;

            OnPropertyChanged(nameof(StateTitle));
            OnPropertyChanged(nameof(StateDescription));
            OnPropertyChanged(nameof(StateAccent));
            OnPropertyChanged(nameof(PowerButtonLabel));
            OnPropertyChanged(nameof(IsPowerOn));
            OnPropertyChanged(nameof(IsCorrupted));
            PowerCommand.RaiseCanExecuteChanged();
            ApplyCommand.RaiseCanExecuteChanged();
        }
    }

    public AppPage CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (!SetProperty(ref _currentPage, value))
                return;

            OnPropertyChanged(nameof(IsHomePage));
            OnPropertyChanged(nameof(IsServicesPage));
        }
    }

    public bool IsHomePage => CurrentPage == AppPage.Home;
    public bool IsServicesPage => CurrentPage == AppPage.Services;
    public bool IsPowerOn => HostsState is HostsState.Active or HostsState.ChangesPending;
    public bool IsCorrupted => HostsState == HostsState.Corrupted;

    public string PowerButtonLabel => HostsState switch
    {
        HostsState.Inactive => "Включить",
        HostsState.Active or HostsState.ChangesPending => "Отключить",
        _ => "Недоступно"
    };

    public string StateTitle => HostsState switch
    {
        HostsState.Inactive => "Не настроено",
        HostsState.Active => "Защита активна",
        HostsState.ChangesPending => "Требуется применить изменения",
        HostsState.Corrupted => "Файл hosts требует внимания",
        _ => "Неизвестное состояние"
    };

    public string StateDescription => HostsState switch
    {
        HostsState.Inactive => "Нажмите большую кнопку, чтобы применить выбранные сервисы.",
        HostsState.Active => "Выбранные сервисы настроены. Приложение можно закрыть.",
        HostsState.ChangesPending => "Активные записи отличаются от выбранных сервисов. Откройте «Сервисы» и сохраните изменения.",
        HostsState.Corrupted => "Управляемый блок NetBypass повреждён или был изменён вручную. Используйте восстановление.",
        _ => string.Empty
    };

    public string StateAccent => HostsState switch
    {
        HostsState.Active => "#61D6A3",
        HostsState.ChangesPending => "#F2B84B",
        HostsState.Corrupted => "#FF6B7A",
        _ => "#7C5CFC"
    };

    public string OperationMessage
    {
        get => _operationMessage;
        private set
        {
            if (SetProperty(ref _operationMessage, value))
                OnPropertyChanged(nameof(HasOperationMessage));
        }
    }

    public bool HasOperationMessage => !string.IsNullOrWhiteSpace(OperationMessage);

    public void RestoreConfirmed()
    {
        RunSafely(() =>
        {
            _hostsService.Restore(Services.Select(item => item.Module));
            DnsCacheService.Flush();
            OperationMessage = "Изменения NetBypass удалены. Остальные записи hosts сохранены.";
            RefreshState();
        });
    }

    private void TogglePower()
    {
        if (IsPowerOn)
        {
            RunSafely(() =>
            {
                _hostsService.Disable();
                DnsCacheService.Flush();
                OperationMessage = "NetBypass отключён.";
                RefreshState();
            });
            return;
        }

        ApplySelectedServices();
    }

    private void ApplySelectedServices()
    {
        RunSafely(() =>
        {
            var selected = Services.Where(item => item.IsSelected).ToArray();
            _hostsService.Apply(selected.Select(item => item.Module));
            DnsCacheService.Flush();
            _settingsService.Save(selected.Select(item => item.Module.Id));
            OperationMessage = $"Применено сервисов: {selected.Length}.";
            RefreshState();
        });
    }

    private void SetAll(bool value)
    {
        foreach (var service in Services)
            service.IsSelected = value;
    }

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ServiceItemViewModel.IsSelected))
            return;

        OperationMessage = string.Empty;
        RefreshState();
        ApplyCommand.RaiseCanExecuteChanged();
    }

    private void RefreshState()
    {
        HostsState = _hostsService.GetState(
            Services.Where(item => item.IsSelected).Select(item => item.Module));
    }

    private void RunSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            OperationMessage = $"Ошибка: {exception.Message}";
            RefreshState();
        }
    }
}

public enum AppPage
{
    Home,
    Services
}
