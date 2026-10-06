using CommunityToolkit.Mvvm.ComponentModel;
using KodizSignage.Services;

namespace KodizSignage.ViewModels;

/// <summary>A ComboBox entry whose label follows the UI language live.</summary>
public sealed partial class OptionItem<T> : ObservableObject
{
    private readonly string _key;

    public OptionItem(T value, string key, ILocalizationService loc)
    {
        Value = value;
        _key = key;
        _label = loc.Get(key);
    }

    public T Value { get; }

    [ObservableProperty]
    private string _label;

    public void Refresh(ILocalizationService loc) => Label = loc.Get(_key);

    public override string ToString() => Label;
}
