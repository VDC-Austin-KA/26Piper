using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using PipingSuite.Services;

namespace PipingSuite.UI.ViewModels;

public sealed partial class CopyOletViewModel : DialogViewModelBase
{
    public CopyOletViewModel(string summary)
    {
        Summary = summary;
        _selectedMode = Modes[0];
    }

    public string Summary { get; }

    public IReadOnlyList<LabeledValue<OletCopyMode>> Modes { get; } =
    [
        new("Same distance from the start of each pipe", OletCopyMode.FromStart),
        new("Same distance from the end of each pipe", OletCopyMode.FromEnd),
        new("Same position (projected onto each pipe)", OletCopyMode.SamePosition),
    ];

    [ObservableProperty]
    private LabeledValue<OletCopyMode> _selectedMode;

    [ObservableProperty]
    private bool _extendShortPipes = true;

    public OletCopyMode Mode => SelectedMode.Value;
}
