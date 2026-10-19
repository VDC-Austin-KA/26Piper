using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PipingSuite.UI.ViewModels;

/// <summary>Auto Dimension: which linear dimension type to use. The command remembers the choice as the default.</summary>
public sealed partial class AutoDimensionViewModel : DialogViewModelBase
{
    public AutoDimensionViewModel(string summary, IReadOnlyList<LabeledValue<long>> types, string? defaultName)
    {
        Summary = summary;
        Types = types;
        _selectedType = types.FirstOrDefault(t => t.Label == defaultName) ?? types.FirstOrDefault();
    }

    public string Summary { get; }
    public IReadOnlyList<LabeledValue<long>> Types { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    private LabeledValue<long>? _selectedType;

    protected override bool CanAccept() => SelectedType is not null;
}
