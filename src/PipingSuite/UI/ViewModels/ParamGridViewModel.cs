using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PipingSuite.Core;
using PipingSuite.Services;

namespace PipingSuite.UI.ViewModels;

/// <summary>Revit-side operations the Parameter Grid needs; implemented by the command (the window is modal).</summary>
public interface IParamGridHost
{
    GridData Load(bool selectionOnly, bool pipingOnly);
    /// <summary>Connected run and line of each loaded piping element; slower, so only asked for when needed.</summary>
    IReadOnlyDictionary<long, (string Run, string Line)> Topology();
    IReadOnlyList<string> SourceParameters();
    IReadOnlyDictionary<long, string?> SourceValues(string source, IReadOnlyCollection<long> ids);
    (int Written, IReadOnlyList<string> Errors) Apply(IReadOnlyList<CellWrite> writes);
    void Show(IReadOnlyCollection<long> ids);
    string DefaultFileName { get; }
}

public enum FillMode { CopyDown, Series }

/// <summary>One parameter value: what Revit has, what the grid will write, and any consistency issue found by Check.</summary>
public sealed partial class GridCell(string? original) : ObservableObject
{
    /// <summary>Null when the element doesn't have this parameter: the cell can't be edited.</summary>
    public string? Original { get; } = original;
    public bool Editable => Original is not null;
    public bool IsDirty => Editable && !string.Equals(Value ?? string.Empty, Original, StringComparison.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string? _value = original;

    [ObservableProperty] private string? _issue;
    public string? Suggestion { get; set; }
}

public sealed partial class GridItem(GridRow row) : ObservableObject
{
    public long Id => row.Id;
    public string Category => row.Category;
    public string FamilyType => row.FamilyType;
    public string SystemType => row.SystemType;
    /// <summary>Connected run and line, filled in the first time a feature needs them.</summary>
    [ObservableProperty] private string _run = string.Empty;
    [ObservableProperty] private string _line = string.Empty;
    public GridCell[] Cells { get; } = row.Values.Select(v => new GridCell(v)).ToArray();
}

/// <summary>
/// Spreadsheet-style editing of the "Set" parameters: set, paste, fill down (copy or series), copy from another
/// parameter, select like rows, check consistency per connected run and system type, export / import .xlsx or .csv.
/// Nothing reaches Revit until Apply, which writes only the changed (yellow) cells.
/// </summary>
public sealed partial class ParamGridViewModel : ObservableObject
{
    public static readonly string[] InfoHeaders = ["ElementId", "Category", "Family : Type", "System Type", "Run", "Line"];
    public const string SameLine = "Line (branch)", SameRun = "Connected piping", SameSystemType = "System type";

    private readonly IParamGridHost _host;
    private List<(GridItem Item, int Col)> _lastFill = [];
    private bool _topology;

    public ParamGridViewModel(IParamGridHost host)
    {
        _host = host;
        Reload(confirm: false);
    }

    /// <summary>Raised after a load, when the editable columns may have changed.</summary>
    public event Action? ColumnsChanged;
    /// <summary>Asks the user whether to drop unapplied edits; set by the window.</summary>
    public Func<bool>? ConfirmDiscard { get; set; }

    public IReadOnlyList<string> Columns { get; private set; } = [];
    public ObservableCollection<GridItem> Items { get; private set; } = [];
    [ObservableProperty] private ICollectionView _view = CollectionViewSource.GetDefaultView(new List<GridItem>());
    public IReadOnlyList<LabeledValue<FillMode>> FillModes { get; } = [new("Copy down", FillMode.CopyDown), new("Fill series", FillMode.Series)];
    public IReadOnlyList<string> SameChoices => [SameLine, SameRun, SameSystemType, .. Columns];
    /// <summary>Parameters to copy from: read the first time the list is opened, not on load.</summary>
    public IReadOnlyList<string> Sources => _sources ?? [];
    private IReadOnlyList<string>? _sources;

    public void LoadSources()
    {
        if (_sources is not null) return;
        _sources = _host.SourceParameters();
        OnPropertyChanged(nameof(Sources));
    }

    /// <summary>Fills in every row's run and line, once.</summary>
    public void EnsureTopology()
    {
        if (_topology) return;
        _topology = true;
        var map = _host.Topology();
        foreach (var i in Items)
            if (map.TryGetValue(i.Id, out var t)) (i.Run, i.Line) = t;
    }

    [ObservableProperty] private bool _selectionOnly;
    [ObservableProperty] private bool _pipingOnly = true;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private bool _changedOnly;
    [ObservableProperty] private bool _issuesOnly;
    [ObservableProperty] private FillMode _fillMode = FillMode.CopyDown;
    [ObservableProperty] private string _sameBy = SameLine;
    [ObservableProperty] private string? _source;
    [ObservableProperty] private string _setValue = string.Empty;
    [ObservableProperty] private string _status = string.Empty;

    partial void OnFilterTextChanged(string value) => View.Refresh();
    partial void OnChangedOnlyChanged(bool value) => View.Refresh();
    partial void OnIssuesOnlyChanged(bool value) => View.Refresh();
    partial void OnFillModeChanged(FillMode value) => Fill(_lastFill, remember: false); // like Excel's AutoFill options

    public int Pending => Items.Sum(i => i.Cells.Count(c => c.IsDirty));
    public IEnumerable<GridItem> Visible => View.Cast<GridItem>();

    [RelayCommand]
    private void Load() => Reload(confirm: true);

    private void Reload(bool confirm)
    {
        if (confirm && Pending > 0 && ConfirmDiscard?.Invoke() == false) return;
        var data = _host.Load(SelectionOnly, PipingOnly);
        Columns = data.Columns;
        _sources = null;
        _lastFill = [];
        _topology = false;
        // One new collection and view, not row-by-row adds: each add on a live view re-filters and redraws.
        Items = new ObservableCollection<GridItem>(data.Rows.Select(r => new GridItem(r)));
        var view = new ListCollectionView(Items) { Filter = o => o is GridItem i && Passes(i) };
        View = view;
        OnPropertyChanged(nameof(SameChoices));
        OnPropertyChanged(nameof(Sources));
        ColumnsChanged?.Invoke();
        Status = $"{Items.Count} element(s) {(SelectionOnly ? "selected" : "in the active view")}, {Columns.Count} \"{ParamGridService.SetGroup}\" parameter(s)."
                 + (Columns.Count == 0 ? $" None of these elements has a writable parameter in the \"{ParamGridService.SetGroup}\" group." : string.Empty);
    }

    private bool Passes(GridItem i)
    {
        if (ChangedOnly && !i.Cells.Any(c => c.IsDirty)) return false;
        if (IssuesOnly && !i.Cells.Any(c => c.Issue is not null)) return false;
        if (FilterText.Length == 0) return true;
        return FilterText.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
            new[] { i.Id.ToString(), i.Category, i.FamilyType, i.SystemType, i.Run, i.Line }.Concat(i.Cells.Select(c => c.Value))
                .Any(v => v?.Contains(word, StringComparison.OrdinalIgnoreCase) == true));
    }

    public void SetCells(IReadOnlyList<(GridItem Item, int Col)> cells, string value)
    {
        int n = Edit(cells, (_, _) => value);
        Status = $"Set {n} cell(s) to \"{value}\". {PendingText}";
    }

    /// <summary>Each column's top selected cell fills the ones below it: copied, or stepped as a series (DETAIL A, B, C… / 01, 02…).</summary>
    public void FillDown(IReadOnlyList<(GridItem Item, int Col)> cells) => Fill(cells, remember: true);

    private void Fill(IReadOnlyList<(GridItem Item, int Col)> cells, bool remember)
    {
        if (remember) _lastFill = [.. cells];
        int n = 0;
        foreach (var column in cells.GroupBy(c => c.Col))
        {
            var list = column.ToList();
            var seed = list[0].Item.Cells[column.Key].Value ?? string.Empty;
            var mode = ValueSequence.Guess(seed);
            int k = 0;
            foreach (var (item, col) in list.Skip(1))
            {
                k++;
                var cell = item.Cells[col];
                if (!cell.Editable) continue;
                cell.Value = FillMode == FillMode.CopyDown ? seed : ValueSequence.Step(seed, mode, k);
                n++;
            }
        }
        if (cells.Count > 0) Status = $"{(FillMode == FillMode.CopyDown ? "Copied down" : "Filled a series")} into {n} cell(s). {PendingText}";
    }

    /// <summary>Clipboard text from Excel (tabs and lines): one value goes into every selected cell, a block pastes from the top-left one.</summary>
    public void Paste(IReadOnlyList<(GridItem Item, int Col)> cells, string text)
    {
        var block = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(l => l.Split('\t')).ToList();
        if (cells.Count == 0 || block.Count == 0) return;
        if (block.Count == 1 && block[0].Length == 1)
        {
            SetCells(cells, block[0][0]);
            return;
        }
        var visible = Visible.ToList();
        int top = cells.Min(c => visible.IndexOf(c.Item)), left = cells.Min(c => c.Col), n = 0;
        for (int r = 0; r < block.Count && top + r < visible.Count; r++)
        for (int c = 0; c < block[r].Length && left + c < Columns.Count; c++)
        {
            var cell = visible[top + r].Cells[left + c];
            if (!cell.Editable) continue;
            cell.Value = block[r][c];
            n++;
        }
        Status = $"Pasted {n} cell(s). {PendingText}";
    }

    public void Revert(IReadOnlyList<(GridItem Item, int Col)> cells)
    {
        int n = Edit(cells, (item, col) => item.Cells[col].Original);
        Status = $"Reverted {n} cell(s). {PendingText}";
    }

    /// <summary>Each selected cell takes its element's value of the chosen source parameter (e.g. System Abbreviation).</summary>
    public void CopyFrom(IReadOnlyList<(GridItem Item, int Col)> cells)
    {
        if (Source is null) { Status = "Pick a parameter to copy from first."; return; }
        var values = _host.SourceValues(Source, cells.Select(c => c.Item.Id).ToHashSet());
        int n = Edit(cells, (item, _) => values.GetValueOrDefault(item.Id) is { } v ? v : null);
        Status = $"Copied {Source} into {n} cell(s). {PendingText}";
    }

    /// <summary>Visible rows sharing a seed row's line (branch), connected run, system type or value in the chosen column.</summary>
    public List<GridItem> SameAs(IReadOnlyCollection<GridItem> seeds)
    {
        if (SameBy is SameLine or SameRun) EnsureTopology();
        int col = Columns.ToList().FindIndex(c => c == SameBy);
        string? Key(GridItem i) => SameBy switch
        {
            SameLine => i.Line.Length > 0 ? i.Line : null,
            SameRun => i.Run.Length > 0 ? i.Run : null,
            SameSystemType => i.SystemType.Length > 0 ? i.SystemType : null,
            _ => col >= 0 ? i.Cells[col].Value ?? string.Empty : null,
        };
        var keys = seeds.Select(Key).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Visible.Where(i => Key(i) is { } k && keys.Contains(k)).ToList();
    }

    /// <summary>
    /// Flags values that disagree with the rest of their line (branch) or connected run, and BC_SYSTEM values that
    /// disagree with the rest of their system type. Line numbers and segments change from line to line, so those
    /// columns are checked per line; the rest per connected run.
    /// </summary>
    [RelayCommand]
    private void Check()
    {
        EnsureTopology();
        foreach (var c in Items.SelectMany(i => i.Cells)) (c.Issue, c.Suggestion) = (null, null);
        int issues = 0;
        void Flag(IEnumerable<GridItem> group, int col, string where)
        {
            var cells = group.Select(i => i.Cells[col]).Where(c => c.Editable).ToList();
            var major = cells.Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v))
                .GroupBy(v => v!, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            if (major is null) return;
            foreach (var c in cells.Where(c => c.Issue is null && !string.Equals(c.Value, major, StringComparison.OrdinalIgnoreCase)))
            {
                (c.Issue, c.Suggestion) = ($"{where} mostly has {Columns[col]} = \"{major}\"", major);
                issues++;
            }
        }
        for (int col = 0; col < Columns.Count; col++)
            if (Columns[col].Contains("LINE", StringComparison.OrdinalIgnoreCase))
                foreach (var line in Items.Where(i => i.Line.Length > 0).GroupBy(i => i.Line)) Flag(line, col, $"Line {line.Key}");
            else
                foreach (var run in Items.Where(i => i.Run.Length > 0).GroupBy(i => i.Run)) Flag(run, col, $"Connected piping {run.Key}");
        int system = Columns.ToList().FindIndex(c => c.Equals(EzIsoService.SystemParam, StringComparison.OrdinalIgnoreCase));
        if (system >= 0)
            foreach (var type in Items.Where(i => i.SystemType.Length > 0).GroupBy(i => i.SystemType)) Flag(type, system, $"System type \"{type.Key}\"");

        IssuesOnly = issues > 0;
        View.Refresh();
        Status = issues == 0
            ? "No inconsistencies: each line agrees on its line columns, connected piping on the rest, and each system type on BC_SYSTEM."
            : $"{issues} value(s) disagree with their line, connected piping or system type (red). Hover for the usual value; Fix issues sets them to it.";
    }

    [RelayCommand]
    private void FixIssues()
    {
        int n = 0;
        foreach (var c in Items.SelectMany(i => i.Cells).Where(c => c.Suggestion is not null))
        {
            c.Value = c.Suggestion;
            (c.Issue, c.Suggestion) = (null, null);
            n++;
        }
        IssuesOnly = false;
        Status = $"Set {n} flagged cell(s) to the usual value. {PendingText}";
    }

    [RelayCommand]
    private void Apply()
    {
        var writes = Items.SelectMany(i => i.Cells.Select((c, col) => (i, c, col)))
            .Where(x => x.c.IsDirty).Select(x => new CellWrite(x.i.Id, Columns[x.col], x.c.Value ?? string.Empty)).ToList();
        if (writes.Count == 0) { Status = "Nothing to apply: change some cells first."; return; }
        var (written, errors) = _host.Apply(writes);
        Reload(confirm: false); // show what Revit now holds
        Status = $"Applied {written} of {writes.Count} change(s) in one undo." +
                 (errors.Count > 0 ? $" Not written: {string.Join("; ", errors.Take(4))}{(errors.Count > 4 ? " …" : string.Empty)}" : string.Empty);
    }

    public void Show(IEnumerable<GridItem> items) => _host.Show(items.Select(i => i.Id).ToHashSet());

    public string DefaultFileName => _host.DefaultFileName;

    /// <summary>The visible rows, with their pending values, under headers the import recognises (ElementId + parameter names).</summary>
    public void Export(string path)
    {
        EnsureTopology();
        var rows = new List<IReadOnlyList<string>> { InfoHeaders.Concat(Columns).ToList() };
        rows.AddRange(Visible.Select(i => (IReadOnlyList<string>)new[] { i.Id.ToString(), i.Category, i.FamilyType, i.SystemType, i.Run, i.Line }
            .Concat(i.Cells.Select(c => c.Value ?? string.Empty)).ToList()));
        SheetFile.Write(path, rows);
        Status = $"Exported {rows.Count - 1} row(s) to {Path.GetFileName(path)}. Edit the parameter columns, keep ElementId, then Import.";
    }

    /// <summary>Reads a sheet back by its ElementId column and parameter-name headers; changed values become pending edits to review.</summary>
    public void Import(string path)
    {
        var rows = SheetFile.Read(path);
        if (rows.Count == 0) { Status = "That file is empty."; return; }
        var header = rows[0].Select(h => h.Trim()).ToList();
        int idCol = header.FindIndex(h => h.Replace(" ", string.Empty).Equals("ElementId", StringComparison.OrdinalIgnoreCase));
        if (idCol < 0) { Status = "No ElementId column: import a sheet exported from this grid."; return; }

        var map = header.Select((h, i) => (Col: i, Set: Columns.ToList().FindIndex(c => c.Equals(h, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Set >= 0).ToList();
        var unknown = header.Where((h, i) => i != idCol && h.Length > 0 && !InfoHeaders.Contains(h, StringComparer.OrdinalIgnoreCase) && map.All(m => m.Col != i)).ToList();
        var byId = Items.ToDictionary(i => i.Id);
        int changed = 0, missing = 0;
        foreach (var row in rows.Skip(1))
        {
            if (idCol >= row.Count || !long.TryParse(row[idCol].Trim(), out var id)) continue;
            if (!byId.TryGetValue(id, out var item)) { missing++; continue; }
            foreach (var (col, set) in map)
            {
                var cell = item.Cells[set];
                var value = col < row.Count ? row[col] : string.Empty;
                if (!cell.Editable || string.Equals(cell.Value ?? string.Empty, value, StringComparison.Ordinal)) continue;
                cell.Value = value;
                changed++;
            }
        }
        ChangedOnly = changed > 0;
        View.Refresh();
        Status = $"Imported {changed} change(s) from {Path.GetFileName(path)} ({map.Count} parameter column(s)); review the yellow cells, then Apply."
                 + (missing > 0 ? $" {missing} row(s) aren't in this grid (load the view or selection they came from)." : string.Empty)
                 + (unknown.Count > 0 ? $" Ignored column(s): {string.Join(", ", unknown.Take(5))}." : string.Empty);
    }

    private int Edit(IReadOnlyList<(GridItem Item, int Col)> cells, Func<GridItem, int, string?> value)
    {
        int n = 0;
        foreach (var (item, col) in cells)
        {
            var cell = item.Cells[col];
            if (!cell.Editable || value(item, col) is not { } v) continue;
            cell.Value = v;
            n++;
        }
        return n;
    }

    private string PendingText => $"{Pending} change(s) waiting to Apply.";
}
