using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PipingSuite.Core;
using PipingSuite.Services;

namespace PipingSuite.UI.ViewModels;

/// <summary>One branch in the review: where it is, how its legs compare with the correct branch's, and whether it'll be replaced.</summary>
public sealed partial class ReplaceRow : ObservableObject
{
    /// <param name="target">Where the correct branch lands; null when it can't be fitted (<paramref name="status"/> says why).</param>
    public ReplaceRow(int no, Target? target, IReadOnlyList<ElementId> ids, string status, string grid, string header, string legs, string stretch, string pipe)
    {
        (No, Target, Ids, Status, Grid, Header, Legs, Stretch, Pipe) = (no, target, ids, status, grid, header, legs, stretch, pipe);
        _include = target is not null;
    }

    public int No { get; }
    public Target? Target { get; }
    public IReadOnlyList<ElementId> Ids { get; }
    public bool CanReplace => Target is not null;
    public string Status { get; }
    public string Grid { get; }
    public string Header { get; }
    /// <summary>The old branch's legs, header out.</summary>
    public string Legs { get; }
    /// <summary>How much each leg of the correct branch is stretched (+) or shortened (−) to land where the old one did.</summary>
    public string Stretch { get; }
    public string Pipe { get; }

    [ObservableProperty] private bool _include;
}

/// <summary>
/// The review before replacing: every branch found, with its location, legs and how the correct branch is stretched
/// onto it, next to a preview of them all; plus optional per-placement numbering of one parameter.
/// </summary>
public sealed partial class ReplaceConfigurationViewModel : DialogViewModelBase
{
    private readonly IReadOnlyDictionary<string, string> _sourceValues;
    private readonly Action<IReadOnlyList<ElementId>> _show;

    /// <param name="sourceValues">Writable parameters on the correct branch and their current values (start values derive from these).</param>
    /// <param name="rows">Branches in placement (and numbering) order.</param>
    public ReplaceConfigurationViewModel(string summary, IReadOnlyDictionary<string, string> sourceValues, IReadOnlyList<ReplaceRow> rows,
        string correct, Action<IReadOnlyList<ElementId>> show)
    {
        Summary = summary;
        Correct = correct;
        Rows = rows;
        _show = show;
        _sourceValues = sourceValues;
        Parameters = sourceValues.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        _selectedMode = Modes[0];
        foreach (var r in rows) r.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(Count)); AcceptCommand.NotifyCanExecuteChanged(); };
    }

    public string Summary { get; }
    /// <summary>The correct branch's legs as built.</summary>
    public string Correct { get; }
    public IReadOnlyList<ReplaceRow> Rows { get; }
    public string Count => $"{Rows.Count(r => r.Include)} of {Rows.Count} branch(es) ticked to replace.";
    [ObservableProperty] private bool _keepReviewView;

    protected override bool CanAccept() => Rows.Any(r => r.Include);

    [RelayCommand]
    private void Show(ReplaceRow row) => _show(row.Ids);

    [RelayCommand]
    private void TickAll()
    {
        bool on = Rows.Where(r => r.CanReplace).Any(r => !r.Include);
        foreach (var r in Rows.Where(r => r.CanReplace)) r.Include = on;
    }

    public List<Target> Chosen() => Rows.Where(r => r.Include && r.Target is not null).Select(r => r.Target!).ToList();
    public IReadOnlyList<string> Parameters { get; }

    public IReadOnlyList<LabeledValue<IncrementMode>> Modes { get; } =
    [
        new("Last number (01 → 02)", IncrementMode.Number),
        new("Last letter (DETAIL A → DETAIL B)", IncrementMode.Letter),
    ];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview))] private bool _number;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview))] private string? _selectedParameter;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview))] private LabeledValue<IncrementMode> _selectedMode;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Preview))] private string _start = string.Empty;

    /// <summary>First few values, in placement order (along the header, lowest first).</summary>
    public string Preview => Number && SelectedParameter is not null
        ? string.Join(", ", Enumerable.Range(0, 4).Select(i => ValueSequence.Step(Start, SelectedMode.Value, i))) + ", …"
        : string.Empty;

    partial void OnSelectedParameterChanged(string? value)
    {
        if (value is null) return;
        var current = _sourceValues[value];
        SelectedMode = Modes.First(m => m.Value == ValueSequence.Guess(current));
        Start = ValueSequence.Step(current, SelectedMode.Value, 1); // the correct branch keeps its own value
        Number = true;
    }

    partial void OnSelectedModeChanged(LabeledValue<IncrementMode> value)
    {
        if (SelectedParameter is not null) Start = ValueSequence.Step(_sourceValues[SelectedParameter], value.Value, 1);
    }

    public Numbering? ToNumbering() =>
        Number && SelectedParameter is not null ? new Numbering(SelectedParameter, SelectedMode.Value, Start) : null;
}
