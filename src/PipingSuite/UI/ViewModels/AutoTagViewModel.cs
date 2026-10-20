using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PipingSuite.UI.ViewModels;

/// <summary>Auto Tag tag type and grouping parameter, remembered for the Revit session; previews the tags it will place.</summary>
public sealed partial class AutoTagViewModel : DialogViewModelBase
{
    private static string? _lastTagType, _lastParameter;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _values;

    /// <param name="values">Each parameter (line number first) and its unique values on the piping, in order.</param>
    public AutoTagViewModel(string summary, IReadOnlyList<LabeledValue<ElementId>> tagTypes, IReadOnlyDictionary<string, IReadOnlyList<string>> values)
    {
        Summary = summary;
        TagTypes = tagTypes;
        _values = values;
        Parameters = values.Keys.ToList();
        _selectedTagType = tagTypes.FirstOrDefault(t => t.Label == _lastTagType) ?? tagTypes[0];
        _selectedParameter = Parameters.FirstOrDefault(p => p == _lastParameter) ?? Parameters[0];
    }

    public string Summary { get; }
    public IReadOnlyList<LabeledValue<ElementId>> TagTypes { get; }
    public IReadOnlyList<string> Parameters { get; }

    [ObservableProperty] private LabeledValue<ElementId> _selectedTagType;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    private string _selectedParameter;

    public string Preview => _values[SelectedParameter] is { Count: > 0 } v
        ? $"{v.Count} tag(s): {string.Join(", ", v.Take(8))}{(v.Count > 8 ? ", …" : string.Empty)}"
        : "No piping here has a value for this; pick another.";

    protected override bool CanAccept() => _values[SelectedParameter].Count > 0;

    public (ElementId TagType, string Parameter) ToOptions()
    {
        (_lastTagType, _lastParameter) = (SelectedTagType.Label, SelectedParameter);
        return (SelectedTagType.Value, SelectedParameter);
    }
}
