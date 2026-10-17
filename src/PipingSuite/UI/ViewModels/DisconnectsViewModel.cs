using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PipingSuite.Services;

namespace PipingSuite.UI.ViewModels;

/// <summary>Revit-side operations the Connect Disconnects window needs; implemented by the command.</summary>
public interface IDisconnectHost
{
    IReadOnlyList<Disconnect> Scan(double toleranceFeet, bool sameSystemType);
    void Show(Disconnect item);
    (int Fixed, IReadOnlyList<string> Notes) Fix(IReadOnlyList<Disconnect> items);
}

public sealed partial class DisconnectItem(Disconnect item) : ObservableObject
{
    public Disconnect Item { get; } = item;
    public bool CanFix => Item.CanFix;
    public string What => Item.What;
    public string Gap => Item.Other is null ? string.Empty : $"{Item.Gap * 12:0.###}\"";

    public string Action => Item.Kind switch
    {
        DisconnectKind.Ends => "Connect",
        DisconnectKind.Elbow => "Add elbow",
        DisconnectKind.Transition => "Add transition",
        DisconnectKind.Takeoff => "Add takeoff",
        DisconnectKind.Olet => "Re-attach olet",
        _ => "Check",
    };

    [ObservableProperty] private bool _isChecked = item.CanFix;
}

public sealed partial class DisconnectsViewModel : ObservableObject
{
    private readonly IDisconnectHost _host;

    public DisconnectsViewModel(string scope, IDisconnectHost host)
    {
        Scope = scope;
        _host = host;
        Scan();
    }

    public string Scope { get; }
    public ObservableCollection<DisconnectItem> Items { get; } = [];

    /// <summary>How far apart (inches) two ends may be and still count as touching.</summary>
    [ObservableProperty] private string _toleranceInches = "0.125";
    [ObservableProperty] private bool _sameSystemType = true;
    [ObservableProperty] private string _status = string.Empty;

    [RelayCommand]
    private void Scan()
    {
        if (!double.TryParse(ToleranceInches, NumberStyles.Float, CultureInfo.CurrentCulture, out var inches) || inches < 0)
        {
            Status = "Tolerance must be a number of inches, e.g. 0.125.";
            return;
        }
        Items.Clear();
        foreach (var d in _host.Scan(inches / 12, SameSystemType)) Items.Add(new DisconnectItem(d));
        int fixable = Items.Count(i => i.CanFix);
        Status = Items.Count == 0 ? "No disconnects in the selection." : $"{fixable} can be connected, {Items.Count - fixable} to check.";
    }

    [RelayCommand]
    private void Zoom(DisconnectItem item) => _host.Show(item.Item);

    [RelayCommand]
    private void CheckAll()
    {
        bool on = Items.Where(i => i.CanFix).Any(i => !i.IsChecked);
        foreach (var i in Items.Where(i => i.CanFix)) i.IsChecked = on;
    }

    [RelayCommand]
    private void Connect()
    {
        var chosen = Items.Where(i => i.IsChecked && i.CanFix).Select(i => i.Item).ToList();
        if (chosen.Count == 0)
        {
            Status = "Tick the items to connect first.";
            return;
        }
        var (done, notes) = _host.Fix(chosen);
        Scan(); // what's left
        Status = $"Connected {done} of {chosen.Count}." + (notes.Count > 0 ? " " + string.Join(" ", notes.Take(3)) + (notes.Count > 3 ? " …" : string.Empty) : string.Empty)
                 + " " + Status;
    }
}
