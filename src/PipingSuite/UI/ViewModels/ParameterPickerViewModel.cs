using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PipingSuite.Core;

namespace PipingSuite.UI.ViewModels;

public sealed partial class ParameterItemViewModel(ParameterSnapshot snapshot, bool isSelected) : ObservableObject
{
    public ParameterSnapshot Snapshot { get; } = snapshot;
    public string Name => Snapshot.Name;
    public string Value => Snapshot.DisplayValue;
    public string Origin => Snapshot.SharedGuid is not null ? "Shared"
        : Snapshot.BuiltIn != Autodesk.Revit.DB.BuiltInParameter.INVALID ? "Built-in" : "Project";

    [ObservableProperty]
    private bool _isSelected = isSelected;
}

/// <summary>Checklist of source parameters to copy, with text filtering.</summary>
public sealed partial class ParameterPickerViewModel : DialogViewModelBase
{
    public ParameterPickerViewModel(string sourceDescription, IEnumerable<ParameterSnapshot> snapshots)
    {
        SourceDescription = sourceDescription;
        // Default: user/shared parameters checked; built-ins (offsets, levels, sizes) opt-in,
        // since pasting those moves or resizes the targets.
        Items = new ObservableCollection<ParameterItemViewModel>(
            snapshots.Select(s => new ParameterItemViewModel(s, s.BuiltIn == Autodesk.Revit.DB.BuiltInParameter.INVALID)));

        foreach (var item in Items)
            item.PropertyChanged += (_, _) => AcceptCommand.NotifyCanExecuteChanged();

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => o is ParameterItemViewModel i
                                && (string.IsNullOrWhiteSpace(FilterText)
                                    || i.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
    }

    public string SourceDescription { get; }
    public ObservableCollection<ParameterItemViewModel> Items { get; }
    public ICollectionView ItemsView { get; }

    [ObservableProperty]
    private string _filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => ItemsView.Refresh();

    public IReadOnlyList<ParameterSnapshot> SelectedSnapshots =>
        Items.Where(i => i.IsSelected).Select(i => i.Snapshot).ToList();

    protected override bool CanAccept() => Items.Any(i => i.IsSelected);

    /// <summary>Acts on the visible (filtered) rows only.</summary>
    [RelayCommand]
    private void SelectAll(bool select)
    {
        foreach (var item in ItemsView.Cast<ParameterItemViewModel>())
            item.IsSelected = select;
    }
}
