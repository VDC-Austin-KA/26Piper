using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using PipingSuite.Services;

namespace PipingSuite.UI.ViewModels;

/// <summary>ISO Views corner / scale / DETAIL grouping. Corner and scale are remembered for the Revit session.</summary>
public sealed partial class IsoViewsViewModel : DialogViewModelBase
{
    private static IsoCorner _lastCorner = IsoCorner.SouthWest;
    private static int _lastScale = 24;

    public IsoViewsViewModel(string summary, IReadOnlyList<string> types, string detailParameter, string tagStatus)
    {
        Summary = summary;
        Types = types;
        TagStatus = tagStatus;
        _detailParameter = detailParameter;
        _selectedCorner = Corners.First(c => c.Value == _lastCorner);
        _selectedScale = Scales.First(c => c.Value == _lastScale);
    }

    public string Summary { get; }
    public IReadOnlyList<string> Types { get; }
    public string TagStatus { get; }
    public IReadOnlyList<LabeledValue<IsoCorner>> Corners => IsoMapViewModel.CornerChoices;
    public IReadOnlyList<LabeledValue<int>> Scales => IsoMapViewModel.ScaleChoices;

    [ObservableProperty] private string _detailParameter;
    [ObservableProperty] private bool _onePerType = true;
    [ObservableProperty] private LabeledValue<IsoCorner> _selectedCorner;
    [ObservableProperty] private LabeledValue<int> _selectedScale;
    [ObservableProperty] private bool _tag = true;
    [ObservableProperty] private bool _dimension = true;

    public IsoViewsOptions ToOptions()
    {
        _lastCorner = SelectedCorner.Value;
        _lastScale = SelectedScale.Value;
        return new IsoViewsOptions(SelectedCorner.Value, SelectedScale.Value, DetailParameter.Trim(), OnePerType, Tag, Dimension);
    }
}
