using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PipingSuite.UI.ViewModels;

/// <summary>Resize: the new size, and whether to keep the bottom of pipe (remembered for the Revit session).</summary>
public sealed partial class ResizeViewModel : DialogViewModelBase
{
    private static bool _lastKeepBottom = true;

    public ResizeViewModel(string summary, IReadOnlyList<LabeledValue<double>> sizes, double current)
    {
        Summary = summary;
        Sizes = sizes;
        // Next size up is the usual ask; the current one if it's the largest.
        _selectedSize = sizes.FirstOrDefault(s => s.Value > current + 1e-6) ?? sizes.Last();
        _keepBottom = _lastKeepBottom;
    }

    public string Summary { get; }
    public IReadOnlyList<LabeledValue<double>> Sizes { get; }

    [ObservableProperty] private LabeledValue<double> _selectedSize;
    [ObservableProperty] private bool _keepBottom;

    public (double Size, bool KeepBottom) ToOptions()
    {
        _lastKeepBottom = KeepBottom;
        return (SelectedSize.Value, KeepBottom);
    }
}
