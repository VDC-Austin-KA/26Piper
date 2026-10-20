using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PipingSuite.Services;
using PipingSuite.UI.ViewModels;

namespace PipingSuite.UI.Views;

/// <summary>
/// The grid's columns are built from the loaded "Set" parameters. Selection-based tools (set, fill, paste, copy
/// from, select same) are routed here because DataGrid's selected cells are view state, then handed to the view model.
/// </summary>
public partial class ParamGridWindow : Window
{
    private static readonly Brush Changed = new SolidColorBrush(Color.FromRgb(0xFF, 0xF2, 0x9E));
    private static readonly Brush Missing = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
    private static readonly Brush Wrong = new SolidColorBrush(Color.FromRgb(0xD9, 0x3B, 0x3B));
    private readonly ParamGridViewModel _vm;
    private bool _editing;

    public ParamGridWindow(ParamGridViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
        _vm.ConfirmDiscard = () => MessageBox.Show(this, $"{_vm.Pending} change(s) haven't been applied. Reload and lose them?", "Parameter Grid",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
        _vm.ColumnsChanged += BuildColumns;
        BuildColumns();
        Closing += (_, e) => e.Cancel = _vm.Pending > 0 && MessageBox.Show(this, $"{_vm.Pending} change(s) haven't been applied. Close anyway?",
            "Parameter Grid", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK;
    }

    private void BuildColumns()
    {
        Grid.Columns.Clear();
        string[] info = ["Id", "Category", "FamilyType", "SystemType", "Run", "Line"];
        for (int i = 0; i < info.Length; i++)
            Grid.Columns.Add(new DataGridTextColumn { Header = ParamGridViewModel.InfoHeaders[i], Binding = new Binding(info[i]), IsReadOnly = true });

        for (int i = 0; i < _vm.Columns.Count; i++)
        {
            var cell = $"Cells[{i}]";
            var style = new Style(typeof(DataGridCell));
            style.Setters.Add(new Setter(ToolTipProperty, new Binding($"{cell}.Issue")));
            style.Triggers.Add(Trigger($"{cell}.IsDirty", true, new Setter(BackgroundProperty, Changed), new Setter(ForegroundProperty, Brushes.Black)));
            style.Triggers.Add(Trigger($"{cell}.Editable", false, new Setter(BackgroundProperty, Missing), new Setter(IsEnabledProperty, false)));
            style.Triggers.Add(Trigger($"{cell}.Issue", null, inverted: true, new Setter(BorderBrushProperty, Wrong), new Setter(BorderThicknessProperty, new Thickness(2))));
            Grid.Columns.Add(new DataGridTextColumn
            {
                Header = _vm.Columns[i],
                Binding = new Binding($"{cell}.Value") { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
                CellStyle = style,
                MinWidth = 90,
                SortMemberPath = $"{cell}.Value",
            });
        }
    }

    private static DataTrigger Trigger(string path, object? value, params Setter[] setters) => Trigger(path, value, false, setters);

    /// <param name="inverted">Fire when the value is anything but <paramref name="value"/> (only used for "not null").</param>
    private static DataTrigger Trigger(string path, object? value, bool inverted, params Setter[] setters)
    {
        var binding = new Binding(path);
        if (inverted) binding.Converter = new NotNull();
        var t = new DataTrigger { Binding = binding, Value = inverted ? true : value };
        foreach (var s in setters) t.Setters.Add(s);
        return t;
    }

    private sealed class NotNull : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is not null;
        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Selected editable-column cells, top to bottom then left to right.</summary>
    private List<(GridItem Item, int Col)> Selected()
    {
        var order = _vm.Visible.Select((item, i) => (item, i)).ToDictionary(x => x.item, x => x.i);
        return Grid.SelectedCells
            .Where(c => c.Item is GridItem && c.Column is not null && SetIndex(c.Column) >= 0)
            .Select(c => (Item: (GridItem)c.Item, Col: SetIndex(c.Column)))
            .Distinct()
            .OrderBy(c => order.GetValueOrDefault(c.Item, int.MaxValue)).ThenBy(c => c.Col)
            .ToList();
    }

    private int SetIndex(DataGridColumn column) => Grid.Columns.IndexOf(column) - ParamGridViewModel.InfoHeaders.Length;

    private List<GridItem> SelectedItems() => Grid.SelectedCells.Select(c => c.Item).OfType<GridItem>().Distinct().ToList();

    private void OnSet(object sender, RoutedEventArgs e) => _vm.SetCells(Selected(), _vm.SetValue);
    private void OnFill(object sender, RoutedEventArgs e) => _vm.FillDown(Selected());
    private void OnRevert(object sender, RoutedEventArgs e) => _vm.Revert(Selected());
    private void OnCopyFrom(object sender, RoutedEventArgs e) => _vm.CopyFrom(Selected());
    private void OnShow(object sender, RoutedEventArgs e) => _vm.Show(SelectedItems());

    private void OnSelectSame(object sender, RoutedEventArgs e)
    {
        var seeds = SelectedItems();
        if (seeds.Count == 0) { _vm.Status = "Select a cell in a row to match first."; return; }
        var column = Grid.CurrentCell.Column is { } current && SetIndex(current) >= 0 ? current
            : Grid.SelectedCells.Select(c => c.Column).FirstOrDefault(c => c is not null && SetIndex(c) >= 0);
        if (column is null) { _vm.Status = "Click a cell in the parameter column to select in first."; return; }
        var matches = _vm.SameAs(seeds);
        Grid.SelectedCells.Clear();
        foreach (var m in matches) Grid.SelectedCells.Add(new DataGridCellInfo(m, column));
        _vm.Status = $"Selected {matches.Count} row(s) with the same {_vm.SameBy} in {column.Header}: type a value and Set, paste, or fill.";
    }

    private void OnBeginEdit(object? sender, DataGridBeginningEditEventArgs e) => _editing = true;
    private void OnEndEdit(object? sender, DataGridCellEditEndingEventArgs e) => _editing = false;

    /// <summary>Excel keys while not typing in a cell: Ctrl+D fill down, Ctrl+V paste, Delete clear, Ctrl+Enter set.</summary>
    private void OnGridKey(object sender, KeyEventArgs e)
    {
        if (_editing) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.D) _vm.FillDown(Selected());
        else if (ctrl && e.Key == Key.V && Clipboard.ContainsText()) _vm.Paste(Selected(), Clipboard.GetText());
        else if (ctrl && e.Key == Key.Enter) _vm.SetCells(Selected(), _vm.SetValue);
        else if (e.Key == Key.Delete) _vm.SetCells(Selected(), string.Empty);
        else return;
        e.Handled = true;
    }

    /// <summary>Natural order (01, 2, 10; DETAIL A, DETAIL B) instead of plain text order.</summary>
    private void OnSorting(object? sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var dir = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var c in Grid.Columns) c.SortDirection = null;
        e.Column.SortDirection = dir;
        int set = SetIndex(e.Column), info = Grid.Columns.IndexOf(e.Column);
        Func<GridItem, string> key = set >= 0 ? i => i.Cells[set].Value ?? string.Empty : info switch
        {
            0 => i => i.Id.ToString(),
            1 => i => i.Category,
            2 => i => i.FamilyType,
            3 => i => i.SystemType,
            4 => i => i.Run,
            _ => i => i.Line,
        };
        ((ListCollectionView)_vm.View).CustomSort = new By(key, dir);
    }

    private sealed class By(Func<GridItem, string> key, ListSortDirection dir) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            int c = ColorSplasherService.NaturalComparer.Instance.Compare(key((GridItem)x!), key((GridItem)y!));
            return dir == ListSortDirection.Ascending ? c : -c;
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = _vm.DefaultFileName, Filter = "Excel workbook (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try { _vm.Export(dialog.FileName); }
        catch (Exception ex) { _vm.Status = $"Couldn't export: {ex.Message}"; }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Sheets (*.xlsx;*.csv)|*.xlsx;*.csv", FileName = _vm.DefaultFileName };
        if (dialog.ShowDialog(this) != true) return;
        try { _vm.Import(dialog.FileName); }
        catch (Exception ex) { _vm.Status = $"Couldn't import: {ex.Message}"; }
    }
}
