using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using PipingSuite.Services;

namespace PipingSuite.UI.ViewModels;

/// <summary>ISO BOM size / side / crop, remembered for the Revit session (ISO Views shares the size and side).</summary>
public sealed partial class BomViewModel : DialogViewModelBase
{
    internal static double? LastSize = 3.0 / 32;
    internal static BomPlacement LastPlacement = BomPlacement.Right;
    private static bool _lastSquare = true;

    public static IReadOnlyList<LabeledValue<double?>> SizeChoices { get; } =
    [
        new("Small (3/32\" text)", 3.0 / 32),
        new("Medium (1/8\" text)", 1.0 / 8),
        new("Large (3/16\" text)", 3.0 / 16),
        new("Extra large (1/4\" text)", 1.0 / 4),
        new("Fit to the piping (as tall beside it, as wide above or below)", null),
    ];

    public static IReadOnlyList<LabeledValue<BomPlacement>> PlacementChoices { get; } =
    [
        new("Right of the piping", BomPlacement.Right),
        new("Below the piping", BomPlacement.Below),
        new("Left of the piping", BomPlacement.Left),
        new("Above the piping", BomPlacement.Above),
    ];

    public BomViewModel(string summary)
    {
        Summary = summary;
        _selectedSize = SizeChoices.First(c => c.Value == LastSize);
        _selectedPlacement = PlacementChoices.First(c => c.Value == LastPlacement);
        _squareCrop = _lastSquare;
    }

    public string Summary { get; }
    public IReadOnlyList<LabeledValue<double?>> Sizes => SizeChoices;
    public IReadOnlyList<LabeledValue<BomPlacement>> Placements => PlacementChoices;

    [ObservableProperty] private LabeledValue<double?> _selectedSize;
    [ObservableProperty] private LabeledValue<BomPlacement> _selectedPlacement;
    [ObservableProperty] private bool _squareCrop;

    public BomOptions ToOptions()
    {
        (LastSize, LastPlacement, _lastSquare) = (SelectedSize.Value, SelectedPlacement.Value, SquareCrop);
        return new BomOptions(SelectedSize.Value, SelectedPlacement.Value, SquareCrop);
    }
}
