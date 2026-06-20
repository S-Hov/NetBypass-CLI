using NetBypass.App.Infrastructure;
using NetBypass.Core.Models;

namespace NetBypass.App.ViewModels;

public sealed class ServiceItemViewModel : ObservableObject
{
    private bool _isSelected;

    public ServiceItemViewModel(ServiceProfile profile, bool isSelected)
    {
        Profile = profile;
        _isSelected = isSelected;
    }

    public ServiceProfile Profile { get; }
    public ServiceModule Module => Profile.Module;
    public string Name => Profile.Name;
    public string Category => Profile.Category;
    public int EntryCount => Module.Entries.Count;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
