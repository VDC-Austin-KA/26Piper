using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Autodesk.Revit.DB;
using PipingSuite.Services;
using Color = System.Windows.Media.Color;

namespace PipingSuite.UI.ViewModels;

/// <summary>Revit-side operations the Color Splasher window needs; implemented by the command.</summary>
public interface IColorSplasherHost
{
    IReadOnlyList<SplashValue> GetValues(IReadOnlyCollection<ElementId> categoryIds, string parameter);
    void Apply(IEnumerable<(IReadOnlyList<ElementId> Ids, Color Color)> entries);
    int Clear(IReadOnlyCollection<ElementId> categoryIds);
    void Select(IReadOnlyCollection<ElementId> ids);
    Color? PickColor(Color current);
}

public sealed partial class SplashCategoryItem(SplashCategory category) : ObservableObject
{
    public SplashCategory Category { get; } = category;
    public string Name => Category.Name;

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class SplashValueItem(SplashValue value, Color color) : ObservableObject
{
    public string Value { get; } = value.Value;
    public IReadOnlyList<ElementId> ElementIds { get; } = value.ElementIds;
    public int Count => ElementIds.Count;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Brush))]
    private Color _color = color;

    public Brush Brush => new SolidColorBrush(Color);
}

public sealed partial class ColorSplasherViewModel : ObservableObject
{
    private readonly IColorSplasherHost _host;

    // Colours chosen so far, by value: switching parameter and back, or loading a scheme, keeps them.
    private readonly Dictionary<string, Color> _remembered = new(StringComparer.OrdinalIgnoreCase);

    public ColorSplasherViewModel(IReadOnlyList<SplashCategory> categories, IColorSplasherHost host)
    {
        _host = host;
        Categories = new ObservableCollection<SplashCategoryItem>(categories.Select(c => new SplashCategoryItem(c)));
        foreach (var c in Categories) c.PropertyChanged += (_, _) => RefreshParameters();

        CategoriesView = CollectionViewSource.GetDefaultView(Categories);
        CategoriesView.Filter = o => Matches(((SplashCategoryItem)o).Name, CategoryFilter) || ((SplashCategoryItem)o).IsSelected;

        ParametersView = CollectionViewSource.GetDefaultView(Parameters);
        ParametersView.Filter = o => Matches((string)o, ParameterFilter);
    }

    public ObservableCollection<SplashCategoryItem> Categories { get; }
    public ICollectionView CategoriesView { get; }
    public ObservableCollection<string> Parameters { get; } = [];
    public ICollectionView ParametersView { get; }
    public ObservableCollection<SplashValueItem> Values { get; } = [];

    [ObservableProperty] private string _categoryFilter = string.Empty;
    [ObservableProperty] private string _parameterFilter = string.Empty;
    [ObservableProperty] private string? _selectedParameter;
    [ObservableProperty] private string _status = "Pick one or more categories, then a parameter.";

    /// <summary>When on, every value list is coloured red→violet automatically; remembered for the session.</summary>
    [ObservableProperty] private bool _autoRainbow = _autoRainbowSession;
    private static bool _autoRainbowSession;

    partial void OnAutoRainbowChanged(bool value)
    {
        _autoRainbowSession = value;
        if (value) Rainbow();
    }

    partial void OnCategoryFilterChanged(string value) => CategoriesView.Refresh();
    partial void OnParameterFilterChanged(string value) => ParametersView.Refresh();
    partial void OnSelectedParameterChanged(string? value) => LoadValues();

    private IReadOnlyCollection<ElementId> SelectedCategoryIds =>
        Categories.Where(c => c.IsSelected).Select(c => c.Category.Id).ToList();

    private void RefreshParameters()
    {
        var keep = SelectedParameter;
        var names = Categories.Where(c => c.IsSelected)
            .SelectMany(c => c.Category.ParameterNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        Parameters.Clear();
        foreach (var n in names) Parameters.Add(n);
        SelectedParameter = keep is not null && Parameters.Contains(keep) ? keep : null;
        if (SelectedParameter is not null) LoadValues();
        else Values.Clear();
    }

    private void LoadValues()
    {
        Values.Clear();
        if (SelectedParameter is null || SelectedCategoryIds.Count == 0) return;

        var values = _host.GetValues(SelectedCategoryIds, SelectedParameter);
        var palette = Palette.Distinct(values.Count, seed: 0).GetEnumerator();
        foreach (var v in values)
        {
            palette.MoveNext();
            Values.Add(new SplashValueItem(v, _remembered.TryGetValue(v.Value, out var c) ? c : palette.Current));
        }
        if (AutoRainbow) Rainbow();
        Status = $"{values.Count} value(s), {values.Sum(v => v.ElementIds.Count)} element(s).";
    }

    [RelayCommand]
    private void PickColor(SplashValueItem item)
    {
        if (_host.PickColor(item.Color) is { } c) _remembered[item.Value] = item.Color = c;
    }

    [RelayCommand]
    private void RandomColors()
    {
        int i = 0;
        foreach (var c in Palette.Distinct(Values.Count, seed: Random.Shared.Next()))
            _remembered[Values[i].Value] = Values[i++].Color = c;
    }

    /// <summary>
    /// Blend from the first value's colour to the last one's around the hue wheel, so the
    /// in-between values pick up the hues in between (red→blue passes yellow and green).
    /// </summary>
    [RelayCommand]
    private void Gradient()
    {
        if (Values.Count < 3) return;
        Color a = Values[0].Color, b = Values[^1].Color;
        for (int i = 1; i < Values.Count - 1; i++)
            _remembered[Values[i].Value] = Values[i].Color = Palette.LerpHue(a, b, i / (double)(Values.Count - 1));
    }

    /// <summary>Full spectrum, red→violet, evenly across all values in list order.</summary>
    [RelayCommand]
    private void Rainbow()
    {
        int i = 0;
        foreach (var c in Palette.Rainbow(Values.Count))
            _remembered[Values[i].Value] = Values[i++].Color = c;
    }

    [RelayCommand]
    private void Apply()
    {
        _host.Apply(Values.Select(v => (v.ElementIds, v.Color)));
        Status = $"Colored {Values.Sum(v => v.Count)} element(s) in the active view.";
    }

    [RelayCommand]
    private void Clear() => Status = $"Cleared overrides on {_host.Clear(SelectedCategoryIds)} element(s).";

    [RelayCommand]
    private void SelectElements(SplashValueItem item)
    {
        _host.Select(item.ElementIds.ToList());
        Status = $"Selected {item.Count} element(s) with \"{item.Value}\".";
    }

    // ---------- schemes (.json: parameter + value -> #RRGGBB) ----------

    private sealed record Scheme(string? Parameter, Dictionary<string, string> Colors);

    [RelayCommand]
    private void SaveScheme()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Color scheme (*.json)|*.json", FileName = $"{SelectedParameter ?? "scheme"}.json" };
        if (dlg.ShowDialog() != true) return;

        var scheme = new Scheme(SelectedParameter, Values.ToDictionary(v => v.Value, v => v.Color.ToString()[3..].Insert(0, "#")));
        File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(scheme, new JsonSerializerOptions { WriteIndented = true }));
        Status = $"Saved {scheme.Colors.Count} color(s).";
    }

    [RelayCommand]
    private void LoadScheme()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Color scheme (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var scheme = JsonSerializer.Deserialize<Scheme>(File.ReadAllText(dlg.FileName));
            foreach (var (value, hex) in scheme?.Colors ?? [])
                _remembered[value] = (Color)ColorConverter.ConvertFromString(hex);

            if (scheme?.Parameter is { } p && Parameters.Contains(p)) SelectedParameter = p;
            foreach (var v in Values)
                if (_remembered.TryGetValue(v.Value, out var c)) v.Color = c;
            Status = $"Loaded {scheme?.Colors.Count ?? 0} color(s).";
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException)
        {
            Status = $"Could not read scheme: {ex.Message}";
        }
    }

    private static bool Matches(string text, string filter) =>
        string.IsNullOrWhiteSpace(filter) || text.Contains(filter, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Colour helpers: evenly spread hues read as distinct far better than raw random RGB.</summary>
public static class Palette
{
    public static IEnumerable<Color> Distinct(int count, int seed)
    {
        double hue = seed % 360;
        for (int i = 0; i < count; i++)
        {
            yield return FromHsv(hue, i % 2 == 0 ? 0.70 : 0.55, i % 3 == 2 ? 0.75 : 0.92);
            hue = (hue + 137.508) % 360; // golden angle
        }
    }

    /// <summary>Hues 0° (red) to 280° (violet); stopping short of 360° keeps the ends distinct.</summary>
    public static IEnumerable<Color> Rainbow(int count)
    {
        for (int i = 0; i < count; i++)
            yield return FromHsv(count == 1 ? 0 : 280.0 * i / (count - 1), 0.80, 0.92);
    }

    /// <summary>Interpolates hue, saturation and value separately (keeps colours vivid mid-way).</summary>
    public static Color LerpHue(Color a, Color b, double t)
    {
        var (h1, s1, v1) = ToHsv(a);
        var (h2, s2, v2) = ToHsv(b);
        double h = (h1 + (h2 - h1) * t + 360) % 360;
        return FromHsv(h, s1 + (s2 - s1) * t, v1 + (v2 - v1) * t);
    }

    private static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0
            : max == r ? 60 * ((g - b) / d % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x),
            3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
