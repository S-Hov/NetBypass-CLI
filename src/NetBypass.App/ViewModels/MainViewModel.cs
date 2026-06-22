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
    private PowerOperation _powerOperation;
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

    public PowerOperation PowerOperation
    {
        get => _powerOperation;
        private set
        {
            if (!SetProperty(ref _powerOperation, value))
                return;

            OnPropertyChanged(nameof(IsConnecting));
            OnPropertyChanged(nameof(IsDisconnecting));
            OnPropertyChanged(nameof(IsPowerTransitioning));
            OnPropertyChanged(nameof(PowerButtonLabel));
        }
    }

    public bool IsHomePage => CurrentPage == AppPage.Home;
    public bool IsServicesPage => CurrentPage == AppPage.Services;
    public bool IsDiagnosticsPage => CurrentPage == AppPage.Diagnostics;
    public bool IsPowerOn => HostsState is HostsState.Active or HostsState.ChangesPending;
    public bool IsCorrupted => HostsState == HostsState.Corrupted;
    public bool IsConnecting => PowerOperation == PowerOperation.Connecting;
    public bool IsDisconnecting => PowerOperation == PowerOperation.Disconnecting;
    public bool IsPowerTransitioning => PowerOperation != PowerOperation.None;
    public bool HasUnavailableServices => _unavailableServiceIds.Count > 0;
    public bool HasAvailabilitySummary => IsPowerOn && SelectedServiceCount > 0;
    public bool HasPartialAvailability =>
        HasAvailabilitySummary && AvailableServiceCount < SelectedServiceCount;
    public int SelectedServiceCount => Services.Count(item => item.IsSelected);
    public int AvailableServiceCount
    {
        get
        {
            var snapshot = _diagnosticStore.Load();
            if (snapshot is null)
                return 0;

            var selectedIds = Services.Where(item => item.IsSelected)
                .Select(item => item.Profile.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return snapshot.Services.Count(result =>
                result.IsReachable && selectedIds.Contains(result.ServiceId));
        }
    }
    public string AvailabilitySummary =>
        $"Доступно сервисов: {AvailableServiceCount} из {SelectedServiceCount}";
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

    public string PowerButtonLabel => PowerOperation switch
    {
        PowerOperation.Connecting => "Подключение...",
        PowerOperation.Disconnecting => "Отключение...",
        _ => IsBusy
        ? "Проверка..."
        : HostsState switch
        {
            HostsState.Inactive => "Включить",
            HostsState.Active or HostsState.ChangesPending => "Отключить",
            _ => "Недоступно"
        }
    };

    public string StateTitle
    {
        get
        {
            if (IsBusy)
                return "Диагностика подключения";

            if (HostsState == HostsState.Inactive)
                return "Не настроено";

            if (HostsState != HostsState.Corrupted
                && VerificationState == VerificationState.Unavailable)
            {
                return IsPowerOn
                    ? "Записи применены частично"
                    : "Нет доступных сервисов";
            }

            return HostsState switch
            {
                HostsState.ChangesPending => "Требуется применить изменения",
                HostsState.Corrupted => "Файл hosts требует внимания",
                HostsState.Active when VerificationState == VerificationState.Verified =>
                    HasPartialAvailability
                        ? "Записи применены частично"
                        : "Все выбранные сервисы доступны",
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

            if (HostsState == HostsState.Inactive)
                return "Перед включением NetBypass проверит доступность адресов.";

            if (HostsState != HostsState.Corrupted
                && VerificationState == VerificationState.Unavailable)
            {
                return IsPowerOn
                    ? AvailabilitySummary
                    : "Ни один выбранный сервис не прошёл проверку доступности.";
            }

            return HostsState switch
            {
                HostsState.ChangesPending => "Откройте «Сервисы» и сохраните выбранный список.",
                HostsState.Corrupted => "Используйте восстановление управляемого блока.",
                HostsState.Active when VerificationState == VerificationState.Verified =>
                    AvailabilitySummary,
                HostsState.Active => "Откройте диагностику и повторите проверку.",
                _ => string.Empty
            };
        }
    }

    public string StateAccent => IsBusy
        ? "#7C5CFC"
        : HostsState switch
        {
            HostsState.Inactive => "#7C5CFC",
            HostsState.Active when VerificationState == VerificationState.Verified => "#61D6A3",
            HostsState.Active when VerificationState == VerificationState.Unavailable => "#F2B84B",
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
            PowerOperation = PowerOperation.Disconnecting;
            try
            {
                _hostsService.Disable();
                DnsCacheService.Flush();
                OperationMessage = string.Empty;
                RefreshState();
                await Task.Delay(450);
            }
            catch (Exception exception)
            {
                OperationMessage = $"Ошибка: {exception.Message}";
                RefreshState();
            }
            finally
            {
                PowerOperation = PowerOperation.None;
            }
            return;
        }

        PowerOperation = PowerOperation.Connecting;
        try
        {
            await ApplySelectedServicesAsync();
            await Task.Delay(450);
        }
        finally
        {
            PowerOperation = PowerOperation.None;
        }
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
            var reachableIds = results.Where(result => result.IsReachable)
                .Select(result => result.ServiceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reachable = selected.Where(item => reachableIds.Contains(item.Profile.Id))
                .ToArray();

            if (reachable.Length == 0)
            {
                VerificationState = VerificationState.Unavailable;
                OperationMessage =
                    "Не удалось применить записи: ни один выбранный сервис не прошёл проверку.";
                return;
            }

            _hostsService.Apply(reachable.Select(item => item.Module));
            DnsCacheService.Flush();
            _settingsService.Save(selected.Select(item => item.Module.Id));
            OperationMessage = failed.Length == 0
                ? $"Все выбранные сервисы доступны: {reachable.Length} из {selected.Length}."
                : $"Записи применены. Доступно сервисов: {reachable.Length} из {selected.Length}.";
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
        RaiseAvailabilityProperties();
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
        RaiseAvailabilityProperties();
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
        HostsState = _hostsService.GetState(GetExpectedActiveModules(selected));
        VerificationState = DetermineVerificationState(selected);
        RaiseAvailabilityProperties();
    }

    private IEnumerable<ServiceModule> GetExpectedActiveModules(
        IReadOnlyCollection<ServiceItemViewModel> selected)
    {
        var snapshot = _diagnosticStore.Load();
        if (snapshot is null)
            return selected.Select(item => item.Module);

        var selectedIds = selected.Select(item => item.Profile.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resultIds = snapshot.Services.Select(result => result.ServiceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!selectedIds.SetEquals(resultIds))
            return selected.Select(item => item.Module);

        var reachableIds = snapshot.Services
            .Where(result => result.IsReachable)
            .Select(result => result.ServiceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return selected.Where(item => reachableIds.Contains(item.Profile.Id))
            .Select(item => item.Module);
    }

    private VerificationState DetermineVerificationState(
        IReadOnlyCollection<ServiceItemViewModel> selected)
    {
        // Диагностика описывает доступность адресов, но не состояние NetBypass.
        // После удаления управляемого блока отключённый экран должен быть таким же,
        // как при первом запуске, независимо от сохранённых результатов проверки.
        if (HostsState == HostsState.Inactive)
            return VerificationState.NotChecked;

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
        RaiseAvailabilityProperties();
    }

    private void RaiseAvailabilityProperties()
    {
        OnPropertyChanged(nameof(SelectedServiceCount));
        OnPropertyChanged(nameof(AvailableServiceCount));
        OnPropertyChanged(nameof(AvailabilitySummary));
        OnPropertyChanged(nameof(HasAvailabilitySummary));
        OnPropertyChanged(nameof(HasPartialAvailability));
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

public enum PowerOperation
{
    None,
    Connecting,
    Disconnecting
}
