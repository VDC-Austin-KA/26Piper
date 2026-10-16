using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using PipingSuite.Services;

namespace PipingSuite.UI.ViewModels;

public sealed partial class AssemblyOptionsViewModel : DialogViewModelBase
{
    public const string ConnectivityOnly = "(Connectivity only)";

    public AssemblyOptionsViewModel(int elementCount, IEnumerable<string> parameterNames)
    {
        Summary = $"{elementCount} piping element(s) selected.";
        GroupByOptions = [ConnectivityOnly, .. parameterNames];
        // Pre-select a line-number style parameter when the model has one.
        _selectedGroupBy = GroupByOptions.FirstOrDefault(n => n.Replace(" ", "").Contains("LINENUMBER", System.StringComparison.OrdinalIgnoreCase))
                           ?? ConnectivityOnly;
    }

    public string Summary { get; }
    public IReadOnlyList<string> GroupByOptions { get; }

    [ObservableProperty]
    private string _selectedGroupBy;

    [ObservableProperty]
    private bool _includeConnectedFittings = true;

    [ObservableProperty]
    private string _namePrefix = "PA-";

    public AssemblyOptions ToOptions() => new(
        SelectedGroupBy == ConnectivityOnly ? null : SelectedGroupBy,
        IncludeConnectedFittings,
        NamePrefix ?? string.Empty);
}
