using System.ComponentModel;
using DerivedOutputDownloader3DX.Models;

namespace DerivedOutputDownloader3DX.UI.Models;

/// <summary>Wrapper d'un DerivedOutputDescriptor avec état de sélection pour les CheckBox.</summary>
public sealed class DerivedOutputItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public DerivedOutputDescriptor Descriptor { get; }
    public string Format => Descriptor.Format;
    public string FileName => Descriptor.FileName;
    public bool IsExchangeFormat => Descriptor.IsExchangeFormat;

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    public DerivedOutputItem(DerivedOutputDescriptor descriptor, bool selected = false)
    {
        Descriptor = descriptor;
        _isSelected = selected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
