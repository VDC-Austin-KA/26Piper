using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using PipingSuite.Services;

namespace PipingSuite.UI.ViewModels;

public sealed partial class CombineParametersViewModel(IReadOnlyList<string> parameterNames) : DialogViewModelBase
{
    public IReadOnlyList<string> ParameterNames { get; } = parameterNames;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    private string? _first;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    private string? _second;

    [ObservableProperty]
    private string _separator = "-";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    private string _newName = string.Empty;

    // Suggest "PKG_LINENUMBER"; the user can still type over it.
    partial void OnFirstChanged(string? value) => NewName = $"{First}_{Second}".Trim('_');
    partial void OnSecondChanged(string? value) => NewName = $"{First}_{Second}".Trim('_');

    protected override bool CanAccept() =>
        First is not null && Second is not null
        && !string.Equals(First, Second, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(NewName)
        && !string.Equals(NewName.Trim(), First, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(NewName.Trim(), Second, StringComparison.OrdinalIgnoreCase);

    public CombineRequest ToRequest() => new(First!, Second!, Separator ?? string.Empty, NewName.Trim());
}
