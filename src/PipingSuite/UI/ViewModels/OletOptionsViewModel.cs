using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.ComponentModel;
using PipingSuite.Services;

namespace PipingSuite.UI.ViewModels;

public sealed record LabeledValue<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>Olet size / family / angle. Last choices are remembered for the Revit session.</summary>
public sealed partial class OletOptionsViewModel : DialogViewModelBase
{
    private static OletSettings? _last;
    private static bool _lastKeepPlacing;

    public OletOptionsViewModel(string target, bool isResize,
        IReadOnlyList<LabeledValue<double>> sizes, IReadOnlyList<LabeledValue<ElementId?>> types)
    {
        Target = target;
        IsInsert = !isResize;
        Sizes = sizes;
        Types = types;
        Angles = Enumerable.Range(0, 8).Select(i => new LabeledValue<double>(AngleLabel(i * 45), i * 45)).ToList();

        _selectedSize = sizes.OrderBy(s => System.Math.Abs(s.Value - (_last?.BranchDiameter ?? 0))).First();
        _selectedType = types.FirstOrDefault(t => t.Value == _last?.SymbolId) ?? types[0];
        _selectedAngle = Angles.FirstOrDefault(a => a.Value == _last?.AngleDegrees) ?? Angles[0];
        _keepPlacing = _lastKeepPlacing;
    }

    public string Target { get; }
    public bool IsInsert { get; }
    public IReadOnlyList<LabeledValue<double>> Sizes { get; }
    public IReadOnlyList<LabeledValue<ElementId?>> Types { get; }
    public IReadOnlyList<LabeledValue<double>> Angles { get; }

    [ObservableProperty] private LabeledValue<double> _selectedSize;
    [ObservableProperty] private LabeledValue<ElementId?> _selectedType;
    [ObservableProperty] private LabeledValue<double> _selectedAngle;
    [ObservableProperty] private bool _keepPlacing;

    public OletSettings ToSettings()
    {
        _last = new OletSettings(SelectedSize.Value, SelectedType.Value, SelectedAngle.Value);
        _lastKeepPlacing = KeepPlacing;
        return _last;
    }

    private static string AngleLabel(int deg) => deg switch
    {
        0 => "Top (0°)",
        90 => "Side (90°)",
        180 => "Bottom (180°)",
        270 => "Side (270°)",
        _ => $"{deg}°",
    };
}
