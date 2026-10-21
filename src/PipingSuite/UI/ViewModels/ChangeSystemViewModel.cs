using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PipingSuite.UI.ViewModels;

/// <summary>Change System: the system type to move the selection to, and whether connected piping goes with it.</summary>
public sealed partial class ChangeSystemViewModel : DialogViewModelBase
{
    private static bool _lastConnected;
    private readonly Func<bool, string> _describe;

    /// <param name="describe">What would change, with or without connected piping (e.g. "12 elements: Condensate Water Supply (12)").</param>
    public ChangeSystemViewModel(IReadOnlyList<LabeledValue<ElementId>> systems, ElementId? current, Func<bool, string> describe)
    {
        Systems = systems;
        _describe = describe;
        _connected = _lastConnected;
        // Preselect a different system, preferring one whose name differs only in its last word (Supply → Return).
        var now = systems.FirstOrDefault(s => s.Value == current)?.Label;
        string Stem(string n) => n.Contains(' ') ? n[..n.LastIndexOf(' ')] : n;
        _selectedSystem = systems.FirstOrDefault(s => now is not null && s.Label != now && Stem(s.Label) == Stem(now))
                          ?? systems.FirstOrDefault(s => s.Value != current) ?? systems[0];
    }

    public IReadOnlyList<LabeledValue<ElementId>> Systems { get; }
    public string Summary => _describe(Connected);

    [ObservableProperty] private LabeledValue<ElementId> _selectedSystem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private bool _connected;

    public (ElementId System, bool Connected) ToOptions()
    {
        _lastConnected = Connected;
        return (SelectedSystem.Value, Connected);
    }
}
