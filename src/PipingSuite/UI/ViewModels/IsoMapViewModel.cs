using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using PipingSuite.Services;

namespace PipingSuite.UI.ViewModels;

/// <summary>ISO Map name / corner / colouring. The corner is remembered for the Revit session.</summary>
public sealed partial class IsoMapViewModel : DialogViewModelBase
{
    private static IsoCorner _lastCorner = IsoCorner.SouthWest;

    public IsoMapViewModel(string summary, IReadOnlyList<string> packages, string tagStatus)
    {
        Summary = summary;
        Packages = packages;
        TagStatus = tagStatus;
        _package = packages.FirstOrDefault() ?? string.Empty;
        _selectedCorner = Corners.First(c => c.Value == _lastCorner);
    }

    public string Summary { get; }
    public IReadOnlyList<string> Packages { get; }
    public string TagStatus { get; }

    public IReadOnlyList<LabeledValue<IsoCorner>> Corners { get; } =
    [
        new("South-west (looking north-east)", IsoCorner.SouthWest),
        new("South-east (looking north-west)", IsoCorner.SouthEast),
        new("North-east (looking south-west)", IsoCorner.NorthEast),
        new("North-west (looking south-east)", IsoCorner.NorthWest),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewName))]
    private string _package;

    [ObservableProperty] private LabeledValue<IsoCorner> _selectedCorner;
    [ObservableProperty] private string _colorParameter = "BC_LINE SEGMENT";
    [ObservableProperty] private bool _rainbow = true;
    [ObservableProperty] private bool _tagPipes = true;

    public string ViewName => string.IsNullOrWhiteSpace(Package) ? "ISO MAP" : $"ISO MAP - {Package.Trim()}";

    public IsoMapOptions ToOptions()
    {
        _lastCorner = SelectedCorner.Value;
        return new IsoMapOptions(ViewName, SelectedCorner.Value, ColorParameter.Trim(), Rainbow, TagPipes);
    }
}
