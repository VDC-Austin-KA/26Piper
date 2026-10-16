using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PipingSuite.UI.ViewModels;

/// <summary>OK plumbing; the view sets DialogResult from <see cref="CloseRequested"/>. Cancel uses the button's IsCancel.</summary>
public abstract partial class DialogViewModelBase : ObservableObject
{
    public event Action<bool>? CloseRequested;

    protected virtual bool CanAccept() => true;

    [RelayCommand(CanExecute = nameof(CanAccept))]
    private void Accept() => CloseRequested?.Invoke(true);
}
