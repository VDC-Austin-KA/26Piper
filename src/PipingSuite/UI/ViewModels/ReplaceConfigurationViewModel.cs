using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using PipingSuite.Core;
using PipingSuite.Services;

namespace PipingSuite.UI.ViewModels;

/// <summary>Optional per-placement numbering of one parameter (which branches is settled in the model review before this).</summary>
public sealed partial class ReplaceConfigurationViewModel : DialogViewModelBase
{
    private readonly IReadOnlyDictionary<string, string> _sourceValues;

    /// <param name="sourceValues">Writable parameters on the correct branch and their current values (start values derive from these).</param>
    public ReplaceConfigurationViewModel(string summary, IReadOnlyDictionary<string, string> sourceValues)
    {
        Summary = summary;
        _sourceValues = sourceValues;
        Parameters = sourceValues.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        _selectedMode = Modes[0];
    }

    public string Summary { get; }
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
