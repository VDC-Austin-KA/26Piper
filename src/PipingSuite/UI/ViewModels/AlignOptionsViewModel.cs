using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using PipingSuite.Services;

namespace PipingSuite.UI.ViewModels;

/// <summary>Axis filter, mover override and tolerance for Align &amp; Connect.</summary>
public sealed partial class AlignOptionsViewModel(string firstDescription, string secondDescription, string toleranceText)
    : DialogViewModelBase
{
    public string FirstDescription { get; } = firstDescription;
    public string SecondDescription { get; } = secondDescription;

    public IReadOnlyList<string> MoverOptions { get; } =
    [
        "Auto: move the smaller pipe, never the main or fitting",
        "Move the first picked element",
        "Move the second picked element",
    ];

    [ObservableProperty]
    private int _moverIndex;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    private bool _alignX = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    private bool _alignY = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    private bool _alignZ = true;

    /// <summary>Parsed by the command with the project's length units (e.g. 6", 150 mm).</summary>
    [ObservableProperty]
    private string _toleranceText = toleranceText;

    protected override bool CanAccept() => AlignX || AlignY || AlignZ;

    public MoverChoice Mover => (MoverChoice)MoverIndex;
}
