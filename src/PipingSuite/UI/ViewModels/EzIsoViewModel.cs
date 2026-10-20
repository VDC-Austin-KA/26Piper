using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PipingSuite.Services;

namespace PipingSuite.UI.ViewModels;

/// <summary>Revit-side operations the EZ-ISO Check window needs; implemented by the command.</summary>
public interface IEzIsoHost
{
    EzIsoScan Scan();
    void Show(EzIsoIssue issue);
    (int Done, IReadOnlyList<string> Notes) Apply(IReadOnlyList<EzIsoIssue> issues);
    void SelectForEzIso();
}

public sealed partial class EzIsoItem(EzIsoIssue issue) : ObservableObject
{
    public EzIsoIssue Issue { get; } = issue;
    public bool CanFix => Issue.CanFix;
    public string Line => Issue.Line;
    public string What => Issue.What;

    public string Action => Issue.Kind switch
    {
        EzIsoKind.Renumber => "New number",
        EzIsoKind.Share => "Same as config",
        EzIsoKind.Fill => "Fill values",
        EzIsoKind.Outlier => "Match line",
        EzIsoKind.MatchDetail => "Match DETAIL",
        EzIsoKind.Connect => "Connect",
        _ => "Check",
    };

    // Empty values, unnumbered lines and duplicates are fixed by default; anything that changes an existing number,
    // and connections, are the user's call.
    [ObservableProperty] private bool _isChecked = issue.Kind is EzIsoKind.Fill or EzIsoKind.Renumber or EzIsoKind.Share;
}

public sealed partial class EzIsoViewModel : ObservableObject
{
    private readonly IEzIsoHost _host;

    public EzIsoViewModel(string scope, IEzIsoHost host)
    {
        Scope = scope;
        _host = host;
        Scan();
    }

    public string Scope { get; }
    public ObservableCollection<EzIsoItem> Items { get; } = [];
    [ObservableProperty] private string _status = string.Empty;

    [RelayCommand]
    private void Scan()
    {
        var scan = _host.Scan();
        Items.Clear();
        foreach (var i in scan.Issues) Items.Add(new EzIsoItem(i));
        Status = $"{scan.Lines} line(s): {scan.Correct} correct (left as they are), {Items.Count} item(s) to look at.";
    }

    [RelayCommand]
    private void Zoom(EzIsoItem item) => _host.Show(item.Issue);

    [RelayCommand]
    private void CheckAll()
    {
        bool on = Items.Where(i => i.CanFix).Any(i => !i.IsChecked);
        foreach (var i in Items.Where(i => i.CanFix)) i.IsChecked = on;
    }

    [RelayCommand]
    private void Apply()
    {
        var chosen = Items.Where(i => i.IsChecked && i.CanFix).Select(i => i.Issue).ToList();
        if (chosen.Count == 0)
        {
            Status = "Tick the items to fix first.";
            return;
        }
        var (done, notes) = _host.Apply(chosen);
        Scan(); // what's left
        Status = $"Fixed {done} of {chosen.Count}." + (notes.Count > 0 ? " " + string.Join(" ", notes.Take(3)) + (notes.Count > 3 ? " …" : string.Empty) : string.Empty)
                 + " " + Status;
    }

    [RelayCommand]
    private void Select() => _host.SelectForEzIso();
}
