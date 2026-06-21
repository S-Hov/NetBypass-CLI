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

    private static readonly TimeSpan VerificationLifetime = TimeSpan.FromHours(6);

    private readonly HostsFileService _hostsService = new();
    private readonly SettingsService _settingsService = new();
    private readonly DiagnosticStore _diagnosticStore = new();
    private readonly NetworkDiagnosticService _diagnosticService = new(
        new CloudflareGoogleDohResolver(),
        new EndpointProbe());
    private HostsState _hostsState;
    private VerificationState _verificationState;
    private string _operationMessage = string.Empty;
    private AppPage _currentPage = AppPage.Home;
    private bool _isBusy;
    private int _diagnosticCompleted;
    private int _diagnosticTotal;
    private string _currentDiagnosticService = string.Empty;
    private HashSet<string> _unavailableServiceIds = new(StringComparer.OrdinalIgnoreCase);

    public MainViewModel()
    {
        var modulesPath = Path.Combine(AppContext.BaseDirectory, "Modules");
        var profiles = new ServiceProfileLoader().LoadDirectory(modulesPath);
        var settings = _settingsService.Load();

        Services = new ObservableCollection<ServiceItemViewModel>(
            profiles.Select(profile => new ServiceItemViewModel(
                profile,
                settings?.SelectedModuleIds?.Contains(profile.Id)
                    ?? !DisabledByDefault.Contains(profile.Id))));

        foreach (var service in Services)
            service.PropertyChanged += OnServicePropertyChanged;

        ServicesView = CollectionViewSource.GetDefaultView(Services);
        ServicesView.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(ServiceItemViewModel.Category)));

        Diagnostics = new ObservableCollection<DiagnosticItemViewModel>();
        LoadStoredDiagnostics();

        PowerCommand = new AsyncRelayCommand(
            TogglePowerAsync,
            () => !IsBusy && HostsState != HostsState.Corrupted);
        ApplyCommand = new AsyncRelayCommand(
            ApplySelectedServicesAsync,
            () => !IsBusy
                  && Services.Any(item => item.IsSelected)
                  && HostsState != HostsState.Corrupted);
        DiagnoseCommand = new AsyncRelayCommand(
            DiagnoseSelectedAsync,
            () => !IsBusy && Services.Any(item => item.IsSelected));
        ApplyReachableCommand = new AsyncRelayCommand(
            ApplyReachableServicesAsync,
            () => !IsBusy && _unavailableServiceIds.Count > 0);
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        ClearAllCommand = new RelayCommand(() => SetAll(false));
        ShowHomeCommand = new RelayCommand(() => CurrentPage = AppPage.Home);
        ShowServicesCommand = new RelayCommand(() => CurrentPage = AppPage.Services);
        ShowDiagnosticsCommand = new RelayCommand(() => CurrentPage = AppPage.Diagnostics);

        RefreshState();
    }

    public ObservableCollection<ServiceItemViewModel> Services { get; }
    public ObservableCollection<DiagnosticItemViewModel> Diagnostics { get; }
    public ICollectionView ServicesView { get; }
    public AsyncRelayCommand PowerCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand DiagnoseCommand { get; }
    public AsyncRelayCommand ApplyReachableCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearAllCommand { get; }
    public RelayCommand ShowHomeCommand { get; }
    public RelayCommand ShowServicesCommand { get; }
    public RelayCommand ShowDiagnosticsCommand { get; }

    public HostsState HostsState
    {
        get => _hostsState;
        private set
        {
            if (!SetProperty(ref _hostsState, value))
                return;

            RaiseStateProperties();
        }
    }

    public VerificationState VerificationState
    {
        get => _verificationState;
        private set
        {
            if (!SetProperty(ref _verificationState, value))
                return;

            RaiseStateProperties();
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
            OnPropertyChanged(nameof(IsDiagnosticsPage));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
                return;

            RaiseStateProperties();
            OnPropertyChanged(nameof(HasDiagnosticProgress));
            OnPropertyChanged(nameof(DiagnosticButtonText));
        }
    }

    public bool IsHomePage => CurrentPage == AppPage.Home;
    public bool IsServicesPage => CurrentPage == AppPage.Services;
    public bool IsDiagnosticsPage => CurrentPage == AppPage.Diagnostics;
    public bool IsPowerOn => HostsState is HostsState.Active or HostsState.ChangesPending;
    public bool IsCorrupted => HostsState == HostsState.Corrupted;
    public bool HasUnavailableServices => _unavailableServiceIds.Count > 0;
    public bool HasDiagnosticProgress => IsBusy && DiagnosticTotal > 0;
    public string DiagnosticButtonText => IsBusy ? "Идёт проверка" : "Проверить выбранное";

    public int DiagnosticCompleted
    {
        get => _diagnosticCompleted;
        private set
        {
            if (SetProperty(ref _diagnosticCompleted, value))
            {
                OnPropertyChanged(nameof(DiagnosticProgressText));
                OnPropertyChanged(nameof(DiagnosticProgressPercent));
            }
        }
    }

    public int DiagnosticTotal
    {
        get => _diagnosticTotal;
        private set
        {
            if (SetProperty(ref _diagnosticTotal, value))
            {
                OnPropertyChanged(nameof(DiagnosticProgressText));
                OnPropertyChanged(nameof(DiagnosticProgressPercent));
                OnPropertyChanged(nameof(HasDiagnosticProgress));
            }
        }
    }

    public double DiagnosticProgressPercent =>
        DiagnosticTotal == 0 ? 0 : (double)DiagnosticCompleted / DiagnosticTotal * 100;

    public string CurrentDiagnosticService
    {
        get => _currentDiagnosticService;
        private set
        {
            if (SetProperty(ref _currentDiagnosticService, value))
                OnPropertyChanged(nameof(DiagnosticProgressText));
        }
    }

    public string DiagnosticProgressText => DiagnosticTotal == 0
        ? string.Empty
        : $"Проверено {DiagnosticCompleted} из {DiagnosticTotal}"
          + (string.IsNullOrWhiteSpace(CurrentDiagnosticService)
              ? string.Empty
              : $" · {CurrentDiagnosticService}");

    public string PowerButtonLabel => IsBusy
        ? "Проверка..."
        : HostsState switch
        {
            HostsState.Inactive => "Включить",
            HostsState.Active or HostsState.ChangesPending => "Отключить",
            _ => "Недоступно"
        };

    public string StateTitle
    {
        get
        {
            if (IsBusy)
                return "Диагностика подключения";

            if (HostsState != HostsState.Corrupted
                && VerificationState == VerificationState.Unavailable)
            {
                return "Проверка доступности не пройдена";
            }

            return HostsState switch
            {
                HostsState.Inactive => "Не настроено",
                HostsState.ChangesPending => "Требуется применить изменения",
                HostsState.Corrupted => "Файл hosts требует внимания",
                HostsState.Active when VerificationState == VerificationState.Verified =>
                    "Записи применены и проверены",
                HostsState.Active => "Записи применены, проверка устарела",
                _ => "Неизвестное состояние"
            };
        }
    }

    public string StateDescription
    {
        get
        {
            if (IsBusy)
                return "Проверяем DoH, TCP и TLS для выбранных сервисов.";

            if (HostsState != HostsState.Corrupted
                && VerificationState == VerificationState.Unavailable)
            {
                return "Один или несколько статических адресов не прошли TCP/TLS-проверку. Конфигурация не будет применена повторно.";
            }

            return HostsState switch
            {
                HostsState.Inactive => "Перед включением NetBypass проверит доступность адресов.",
                HostsState.ChangesPending => "Откройте «Сервисы» и сохраните выбранный список.",
                HostsState.Corrupted => "Используйте восстановление управляемого блока.",
                HostsState.Active when VerificationState == VerificationState.Verified =>
                    "Адреса доступны. Приложение можно закрыть.",
                HostsState.Active => "Откройте диагностику и повторите проверку.",
                _ => string.Empty
            };
        }
    }

    public string StateAccent => IsBusy
        ? "#7C5CFC"
        : HostsState switch
        {
            HostsState.Active when VerificationState == VerificationState.Verified => "#61D6A3",
            _ when VerificationState == VerificationState.Unavailable => "#FF6B7A",
            HostsState.Active => "#F2B84B",
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

    private async Task TogglePowerAsync()
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

        await ApplySelectedServicesAsync();
    }

    private async Task ApplySelectedServicesAsync()
    {
        var selected = Services.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0)
            return;

        IsBusy = true;
        OperationMessage = string.Empty;
        try
        {
            var results = await DiagnoseWithProgressAsync(selected);
            SaveAndDisplayDiagnostics(results);

            var failed = results.Where(result => !result.IsReachable).ToArray();
            if (failed.Length > 0)
            {
                VerificationState = VerificationState.Unavailable;
                OperationMessage =
                    $"Применение отменено: недоступно сервисов — {failed.Length}. "
                    + "Исключите их, чтобы применить остальные рабочие сервисы.";
                CurrentPage = AppPage.Diagnostics;
                return;
            }

            _hostsService.Apply(selected.Select(item => item.Module));
            DnsCacheService.Flush();
            _settingsService.Save(selected.Select(item => item.Module.Id));
            OperationMessage = $"Проверено и применено сервисов: {selected.Length}.";
            RefreshState();
        }
        catch (Exception exception)
        {
            OperationMessage = $"Ошибка диагностики: {exception.Message}";
            VerificationState = VerificationState.Unavailable;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DiagnoseSelectedAsync()
    {
        var selected = Services.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0)
            return;

        IsBusy = true;
        OperationMessage = string.Empty;
        try
        {
            var results = await DiagnoseWithProgressAsync(selected);
            SaveAndDisplayDiagnostics(results);
            VerificationState = results.All(result => result.IsReachable)
                ? VerificationState.Verified
                : VerificationState.Unavailable;
            OperationMessage = results.All(result => result.IsReachable)
                ? "Все выбранные адреса прошли TCP/TLS-проверку."
                : "Часть адресов недоступна. Их можно исключить и применить остальные сервисы.";
        }
        catch (Exception exception)
        {
            OperationMessage = $"Ошибка диагностики: {exception.Message}";
            VerificationState = VerificationState.Unavailable;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyReachableServicesAsync()
    {
        if (_unavailableServiceIds.Count == 0)
            return;

        foreach (var service in Services.Where(item =>
                     _unavailableServiceIds.Contains(item.Profile.Id)))
        {
            service.IsSelected = false;
        }

        var remaining = Services.Count(item => item.IsSelected);
        if (remaining == 0)
        {
            OperationMessage = "Все выбранные сервисы недоступны. Применять нечего.";
            return;
        }

        OperationMessage =
            $"Недоступные сервисы исключены. Проверяем оставшиеся: {remaining}.";
        await ApplySelectedServicesAsync();
    }

    private async Task<IReadOnlyList<ServiceDiagnosticResult>> DiagnoseWithProgressAsync(
        IReadOnlyCollection<ServiceItemViewModel> selected)
    {
        DiagnosticTotal = selected.Count;
        DiagnosticCompleted = 0;
        CurrentDiagnosticService = string.Empty;
        Diagnostics.Clear();

        using var semaphore = new SemaphoreSlim(6);
        var pending = selected.Select(async item =>
        {
            await semaphore.WaitAsync();
            try
            {
                var result = await _diagnosticService.DiagnoseAsync(item.Profile);
                return (Item: item, Result: result);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        var results = new List<ServiceDiagnosticResult>(selected.Count);
        while (pending.Count > 0)
        {
            var completedTask = await Task.WhenAny(pending);
            pending.Remove(completedTask);
            var completed = await completedTask;
            results.Add(completed.Result);
            DiagnosticCompleted++;
            CurrentDiagnosticService = completed.Item.Name;
            Diagnostics.Add(new DiagnosticItemViewModel(completed.Result));
        }

        CurrentDiagnosticService = string.Empty;
        return results;
    }

    private void SaveAndDisplayDiagnostics(
        IReadOnlyList<ServiceDiagnosticResult> results)
    {
        var snapshot = new DiagnosticSnapshot(DateTimeOffset.UtcNow, results);
        _diagnosticStore.Save(snapshot);
        _unavailableServiceIds = results
            .Where(result => !result.IsReachable)
            .Select(result => result.ServiceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(HasUnavailableServices));
        ApplyReachableCommand.RaiseCanExecuteChanged();
    }

    private void LoadStoredDiagnostics()
    {
        var snapshot = _diagnosticStore.Load();
        if (snapshot is null)
            return;

        foreach (var result in snapshot.Services.OrderBy(result => result.ServiceName))
            Diagnostics.Add(new DiagnosticItemViewModel(result));

        _unavailableServiceIds = snapshot.Services
            .Where(result => !result.IsReachable)
            .Select(result => result.ServiceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        DiagnoseCommand.RaiseCanExecuteChanged();
        ApplyReachableCommand.RaiseCanExecuteChanged();
    }

    private void RefreshState()
    {
        var selected = Services.Where(item => item.IsSelected).ToArray();
        HostsState = _hostsService.GetState(selected.Select(item => item.Module));
        VerificationState = DetermineVerificationState(selected);
    }

    private VerificationState DetermineVerificationState(
        IReadOnlyCollection<ServiceItemViewModel> selected)
    {
        var snapshot = _diagnosticStore.Load();
        if (snapshot is null
            || DateTimeOffset.UtcNow - snapshot.CreatedAt > VerificationLifetime)
        {
            return VerificationState.NotChecked;
        }

        var selectedIds = selected.Select(item => item.Profile.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resultIds = snapshot.Services.Select(result => result.ServiceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!selectedIds.SetEquals(resultIds))
            return VerificationState.NotChecked;

        return snapshot.Services.All(result => result.IsReachable)
            ? VerificationState.Verified
            : VerificationState.Unavailable;
    }

    private void RaiseStateProperties()
    {
        OnPropertyChanged(nameof(StateTitle));
        OnPropertyChanged(nameof(StateDescription));
        OnPropertyChanged(nameof(StateAccent));
        OnPropertyChanged(nameof(PowerButtonLabel));
        OnPropertyChanged(nameof(IsPowerOn));
        OnPropertyChanged(nameof(IsCorrupted));
        PowerCommand?.RaiseCanExecuteChanged();
        ApplyCommand?.RaiseCanExecuteChanged();
        DiagnoseCommand?.RaiseCanExecuteChanged();
        ApplyReachableCommand?.RaiseCanExecuteChanged();
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
    Services,
    Diagnostics
}

public enum VerificationState
{
    NotChecked,
    Verified,
    Unavailable
}
