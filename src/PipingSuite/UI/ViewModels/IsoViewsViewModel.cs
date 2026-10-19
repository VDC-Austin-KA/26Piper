using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using PipingSuite.Services;

namespace PipingSuite.UI.ViewModels;

/// <summary>ISO Views corner / scale / DETAIL grouping / colour / BOM. All but the grouping are remembered for the Revit session.</summary>
public sealed partial class IsoViewsViewModel : DialogViewModelBase
{
    private static IsoCorner _lastCorner = IsoCorner.SouthWest;
    private static int _lastScale = 24;
    private static bool _lastColor = true, _lastRainbow = true, _lastBom;

    public IsoViewsViewModel(string summary, IReadOnlyList<string> types, string detailParameter, string tagStatus)
    {
        Summary = summary;
        Types = types;
        TagStatus = tagStatus;
        _detailParameter = detailParameter;
        _selectedCorner = Corners.First(c => c.Value == _lastCorner);
        _selectedScale = Scales.First(c => c.Value == _lastScale);
        _color = _lastColor;
        _rainbow = _lastRainbow;
        _bom = _lastBom;
        _selectedBomSize = BomViewModel.SizeChoices.First(c => c.Value == BomViewModel.LastSize);
        _selectedBomPlacement = BomViewModel.PlacementChoices.First(c => c.Value == BomViewModel.LastPlacement);
    }

    public string Summary { get; }
    public IReadOnlyList<string> Types { get; }
    public string TagStatus { get; }
    public IReadOnlyList<LabeledValue<IsoCorner>> Corners => IsoMapViewModel.CornerChoices;
    public IReadOnlyList<LabeledValue<int>> Scales => IsoMapViewModel.ScaleChoices;
    public IReadOnlyList<LabeledValue<double?>> BomSizes => BomViewModel.SizeChoices;
    public IReadOnlyList<LabeledValue<BomPlacement>> BomPlacements => BomViewModel.PlacementChoices;

    [ObservableProperty] private string _detailParameter;
    [ObservableProperty] private bool _onePerType = true;
    [ObservableProperty] private LabeledValue<IsoCorner> _selectedCorner;
    [ObservableProperty] private LabeledValue<int> _selectedScale;
    [ObservableProperty] private bool _tag = true;
    [ObservableProperty] private bool _color;
    [ObservableProperty] private bool _rainbow;
    [ObservableProperty] private bool _bom;
    [ObservableProperty] private LabeledValue<double?> _selectedBomSize;
    [ObservableProperty] private LabeledValue<BomPlacement> _selectedBomPlacement;

    public IsoViewsOptions ToOptions()
    {
        _lastCorner = SelectedCorner.Value;
        _lastScale = SelectedScale.Value;
        (_lastColor, _lastRainbow, _lastBom) = (Color, Rainbow, Bom);
        (BomViewModel.LastSize, BomViewModel.LastPlacement) = (SelectedBomSize.Value, SelectedBomPlacement.Value);
        return new IsoViewsOptions(SelectedCorner.Value, SelectedScale.Value, DetailParameter.Trim(), OnePerType, Tag, Color, Rainbow,
            Bom ? new BomOptions(SelectedBomSize.Value, SelectedBomPlacement.Value, SquareCrop: true) : null);
    }
}
