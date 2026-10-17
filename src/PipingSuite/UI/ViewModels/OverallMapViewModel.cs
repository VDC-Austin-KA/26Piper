using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using PipingSuite.Services;

namespace PipingSuite.UI.ViewModels;

/// <summary>Overall key map name / colours / transparency. Transparency is remembered for the Revit session.</summary>
public sealed partial class OverallMapViewModel : DialogViewModelBase
{
    private static int _lastTransparency = 50;

    public OverallMapViewModel(string summary, IReadOnlyList<string> packages)
    {
        Summary = summary;
        Packages = string.Join(", ", packages);
        var prefix = CommonPrefix(packages).TrimEnd(' ', '-', '_');
        _viewName = prefix.Length > 0 ? $"ISO MAP - OVERALL - {prefix}" : "ISO MAP - OVERALL";
        _transparency = _lastTransparency;
    }

    public string Summary { get; }
    public string Packages { get; }

    [ObservableProperty] private string _viewName;
    [ObservableProperty] private bool _rainbow = true;
    [ObservableProperty] private int _transparency;
    [ObservableProperty] private bool _legend = true;

    protected override bool CanAccept() => !string.IsNullOrWhiteSpace(ViewName);

    partial void OnViewNameChanged(string value) => AcceptCommand.NotifyCanExecuteChanged();

    public OverallMapOptions ToOptions()
    {
        _lastTransparency = Transparency;
        return new OverallMapOptions(ViewName.Trim(), Rainbow, Transparency, Legend);
    }

    /// <summary>"PKG 6 - A", "PKG 6 - B" → "PKG 6 - "; a single value is its own prefix.</summary>
    private static string CommonPrefix(IReadOnlyList<string> values) =>
        values.Count == 0 ? string.Empty
        : values.Aggregate((a, b) => new string(a.Zip(b).TakeWhile(p => p.First == p.Second).Select(p => p.First).ToArray()));
}
