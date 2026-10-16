using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using PipingSuite.Services;

namespace PipingSuite.UI.ViewModels;

public sealed partial class SelectableName(string name, bool isSelected) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    private bool _isSelected = isSelected;
}

public sealed partial class AssemblyOptionsViewModel : DialogViewModelBase
{
    public AssemblyOptionsViewModel(int elementCount, IEnumerable<string> parameterNames)
    {
        Summary = $"{elementCount} piping element(s) selected.";
        // Pre-tick a line-number style parameter when the model has one.
        Parameters = new ObservableCollection<SelectableName>(parameterNames.Select(n =>
            new SelectableName(n, n.Replace(" ", "").Contains("LINENUMBER", StringComparison.OrdinalIgnoreCase))));

        ParametersView = CollectionViewSource.GetDefaultView(Parameters);
        ParametersView.Filter = o => o is SelectableName p
                                     && (p.IsSelected || string.IsNullOrWhiteSpace(FilterText)
                                         || p.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
    }

    public string Summary { get; }
    public ObservableCollection<SelectableName> Parameters { get; }
    public ICollectionView ParametersView { get; }

    [ObservableProperty]
    private string _filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => ParametersView.Refresh();

    [ObservableProperty]
    private bool _includeConnectedFittings = true;

    [ObservableProperty]
    private string _namePrefix = "PA-";

    /// <summary>No parameter ticked = group by connectivity only.</summary>
    public AssemblyOptions ToOptions() => new(
        Parameters.Where(p => p.IsSelected).Select(p => p.Name).ToList(),
        IncludeConnectedFittings,
        NamePrefix ?? string.Empty);
}
