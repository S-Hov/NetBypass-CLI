using NetBypass.App.Infrastructure;
using NetBypass.Core.Models;

namespace NetBypass.App.ViewModels;

public sealed class ServiceItemViewModel : ObservableObject
{
    private bool _isSelected;

    public ServiceItemViewModel(ServiceModule module, bool isSelected)
    {
        Module = module;
        _isSelected = isSelected;
    }

    public ServiceModule Module { get; }
    public string Name => Module.Name;
    public string Category => Module.Category;
    public int EntryCount => Module.Entries.Count;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
