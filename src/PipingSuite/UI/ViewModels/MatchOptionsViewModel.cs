using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PipingSuite.Services;

namespace PipingSuite.UI.ViewModels;

/// <summary>How strict Replace Configuration's matching is. Choices are remembered for the Revit session.</summary>
public sealed partial class MatchOptionsViewModel : DialogViewModelBase
{
    private static string _lastTolerance = "1";
    private static bool _lastAnyLength, _lastAnyDirection;

    public MatchOptionsViewModel(string summary)
    {
        Summary = summary;
        _toleranceInches = _lastTolerance;
        _anyLength = _lastAnyLength;
        _anyDirection = _lastAnyDirection;
    }

    public string Summary { get; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AcceptCommand))] private string _toleranceInches;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AcceptCommand))] private bool _anyLength;
    [ObservableProperty] private bool _anyDirection;

    protected override bool CanAccept() => AnyLength || Inches() is not null;

    private double? Inches() =>
        double.TryParse(ToleranceInches, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) && v >= 0 ? v : null;

    public MatchRules ToRules()
    {
        (_lastTolerance, _lastAnyLength, _lastAnyDirection) = (ToleranceInches, AnyLength, AnyDirection);
        return new MatchRules(AnyLength ? null : Inches()!.Value / 12, AnyDirection);
    }
}
