using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using PipingSuite.Services;

namespace PipingSuite.UI.ViewModels;

/// <summary>ISO Map name / corner / scale / colouring. Corner and scale are remembered for the Revit session.</summary>
public sealed partial class IsoMapViewModel : DialogViewModelBase
{
    private static IsoCorner _lastCorner = IsoCorner.SouthWest;
    private static int _lastScale = 24;

    public IsoMapViewModel(string summary, IReadOnlyList<string> packages, string tagStatus)
    {
        Summary = summary;
        Packages = packages;
        TagStatus = tagStatus;
        _package = packages.FirstOrDefault() ?? string.Empty;
        _selectedCorner = Corners.First(c => c.Value == _lastCorner);
        _selectedScale = Scales.First(c => c.Value == _lastScale);
    }

    public string Summary { get; }
    public IReadOnlyList<string> Packages { get; }
    public string TagStatus { get; }

    public static IReadOnlyList<LabeledValue<IsoCorner>> CornerChoices { get; } =
    [
        new("South-west (looking north-east)", IsoCorner.SouthWest),
        new("South-east (looking north-west)", IsoCorner.SouthEast),
        new("North-east (looking south-west)", IsoCorner.NorthEast),
        new("North-west (looking south-east)", IsoCorner.NorthWest),
    ];

    public static IReadOnlyList<LabeledValue<int>> ScaleChoices { get; } =
    [
        new("1/8\" = 1'-0\"", 96),
        new("3/16\" = 1'-0\"", 64),
        new("1/4\" = 1'-0\"", 48),
        new("3/8\" = 1'-0\"", 32),
        new("1/2\" = 1'-0\"", 24),
        new("3/4\" = 1'-0\"", 16),
        new("1\" = 1'-0\"", 12),
    ];

    public IReadOnlyList<LabeledValue<IsoCorner>> Corners => CornerChoices;
    public IReadOnlyList<LabeledValue<int>> Scales => ScaleChoices;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewName))]
    private string _package;

    [ObservableProperty] private LabeledValue<IsoCorner> _selectedCorner;
    [ObservableProperty] private LabeledValue<int> _selectedScale;
    [ObservableProperty] private string _colorParameter = "BC_LINE SEGMENT";
    [ObservableProperty] private bool _rainbow = true;
    [ObservableProperty] private bool _tagSegments = true;

    public string ViewName => string.IsNullOrWhiteSpace(Package) ? "ISO MAP" : $"ISO MAP - {Package.Trim()}";

    public IsoMapOptions ToOptions()
    {
        _lastCorner = SelectedCorner.Value;
        _lastScale = SelectedScale.Value;
        return new IsoMapOptions(ViewName, SelectedCorner.Value, SelectedScale.Value, ColorParameter.Trim(), Rainbow, TagSegments);
    }
}
