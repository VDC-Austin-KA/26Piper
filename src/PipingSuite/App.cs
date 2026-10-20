using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using PipingSuite.Commands;

namespace PipingSuite;

/// <summary>Add-in entry point: builds the "Piping Suite" ribbon tab.</summary>
public sealed class App : IExternalApplication
{
    private const string TabName = "Piping Suite";

    private static readonly string AssemblyPath = Assembly.GetExecutingAssembly().Location;
    private static readonly string ResourceDir = Path.Combine(Path.GetDirectoryName(AssemblyPath)!, "Resources");

    // Button name -> tooltip clip; attached once Revit has built its ribbon controls.
    private readonly Dictionary<string, string> _videoTips = [];

    public Result OnStartup(UIControlledApplication application)
    {
        application.CreateRibbonTab(TabName);

        var assemblies = application.CreateRibbonPanel(TabName, "Assemblies & Parameters");
        AddButton<CreateAssemblyCommand>(assemblies, "Create\nAssemblies", "assemblies", "assemblies",
            "Group selected pipes into one assembly per connected run, split by one or more parameters (e.g. PKG + LINENUMBER).");
        assemblies.AddSeparator();
        AddButton<CopyParametersCommand>(assemblies, "Copy\nParameters", "copy", "copy_paste",
            "Copy chosen parameter values from a source element.");
        AddButton<PasteParametersCommand>(assemblies, "Paste\nParameters", "paste", "copy_paste",
            "Paste copied parameter values onto the selected elements.");
        AddButton<CombineParametersCommand>(assemblies, "Combine\nParameters", "combine", "combine",
            "Create a shared parameter that joins two existing parameters, e.g. PKG + LINENUMBER = PKG9-1.");

        var routing = application.CreateRibbonPanel(TabName, "Routing");
        AddButton<AlignConnectCommand>(routing, "Align &\nConnect", "align", "align",
            "Fix small offsets and connect two piping elements. Moves the smaller pipe; the main and fittings stay put. Tees into the main when needed.");
        AddButton<InsertOletCommand>(routing, "Insert\nOlet", "olet", "olet",
            "Click a pipe to place a centred olet (no branch pipe left behind), or click an existing olet to resize it in place.");
        AddButton<CopyOletCommand>(routing, "Copy\nOlet", "copy_olet", "copy_olet",
            "Copy an olet's placement (distance, rotation, size, type) onto other pipes in one go. Extends pipes that are too short.");
        AddButton<ConnectDisconnectsCommand>(routing, "Connect\nDisconnects", "heal", "heal",
            "Find every open end in a piping system that touches something it isn't connected to, zoom to each, and connect them: direct, elbow, transition, takeoff or re-attached olet.");
        AddButton<ReplaceConfigurationCommand>(routing, "Replace\nConfig", "replace", "replace",
            "Swap every branch laid out like a picked one (same system) for a correctly built branch or assembly, reconnected to its header, with optional numbering.");
        AddButton<EzIsoCheckCommand>(routing, "EZ-ISO\nCheck", "ez_iso", "ez_iso",
            "Check line numbers ({BC_SYSTEM}-{BC_PACKAGE}-{BC_LINE SEGMENT}) for EZ-ISO's Auto Line Nos: correct lines are left alone; empty values, outliers, duplicate and unnumbered lines and touching-but-unconnected taps are listed with a fix.");

        var view = application.CreateRibbonPanel(TabName, "Visualize");
        AddButton<ColorSplasherCommand>(view, "Color\nSplasher", "color", "color",
            "Color elements in the active view by the value of any parameter. Save and load color schemes.");
        AddButton<IsoMapCommand>(view, "ISO\nMap", "iso_map", "iso_map",
            "Make a locked isometric of the selection named \"ISO MAP - <PACKAGE>\": shaded, colored and tagged by BC_LINE SEGMENT, cropped square for sheets.");
        AddButton<OverallMapCommand>(view, "Overall\nMap", "overall_map", "overall_map",
            "Key plan of the selection: duplicates the active plan and boxes each spool in a translucent color per PACKAGE value, with a legend.");
        AddButton<IsoViewsCommand>(view, "ISO\nViews", "iso_views", "iso_views",
            "One locked isometric per DETAIL type (or per configuration) in the selection: rainbow-colored and tagged by line segment, cropped square for sheets.");
        AddButton<AutoDimensionCommand>(view, "Auto\nDimension", "auto_dim", "auto_dim",
            "Dimension the piping in the active view (or the selected piping) like a spool iso: one chained dimension per straight leg, centre to centre of its fittings and valves. Works in locked 3D views, plans and sections.");
        AddButton<AutoTagCommand>(view, "Auto\nTag", "auto_tag", "auto_tag",
            "Tag the active view once per unique line number ({BC_SYSTEM}-{BC_PACKAGE}-{BC_LINE SEGMENT}) or other parameter value: a header and its identical branches get one tag each. Pick the tag type and parameter; tags stack in a column with leaders, and re-running replaces them.");
        AddButton<IsoBomCommand>(view, "ISO\nBOM", "iso_bom", "iso_bom",
            "Add a bill of materials table to the active ISO view (selected or visible piping): item, quantity (pipe length or count), size and description, drawn in the view so it goes onto the sheet with it. Pick a text size or fit it to the piping, and a side; run again to resize or move it.");

        application.ControlledApplication.ApplicationInitialized += (_, _) => AttachVideoTooltips();
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    private void AddButton<TCommand>(RibbonPanel panel, string text, string icon, string clip, string tooltip)
        where TCommand : IExternalCommand
    {
        var type = typeof(TCommand);
        var data = new PushButtonData(type.Name, text, AssemblyPath, type.FullName)
        {
            ToolTip = tooltip,
            Image = LoadImage($"Icons/{icon}_16.png"),
            LargeImage = LoadImage($"Icons/{icon}_32.png"),
            ToolTipImage = LoadImage($"Tooltips/{clip}.png"), // static fallback if video can't play
        };
        panel.AddItem(data);
        _videoTips[type.Name] = Path.Combine(ResourceDir, "Tooltips", $"{clip}.mp4");
    }

    private static BitmapImage? LoadImage(string relativePath)
    {
        var path = Path.Combine(ResourceDir, relativePath);
        if (!File.Exists(path)) return null;

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path);
        image.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// Swaps each button's still tooltip image for its looping clip, like Revit's own tools.
    /// AdWindows is internal UI plumbing, so any failure just leaves the still image in place.
    /// </summary>
    private void AttachVideoTooltips()
    {
        try
        {
            var tab = Autodesk.Windows.ComponentManager.Ribbon?.Tabs.FirstOrDefault(t => t.Id == TabName);
            if (tab is null) return;

            foreach (var item in tab.Panels.SelectMany(p => p.Source.Items))
            {
                // API buttons get ids like "CustomCtrl_%CustomCtrl_%Piping Suite%Routing%AlignConnectCommand".
                var name = item.Id?.Split('%').LastOrDefault();
                if (name is null || !_videoTips.TryGetValue(name, out var clip) || !File.Exists(clip)) continue;
                if (item.ToolTip is not Autodesk.Windows.RibbonToolTip tip) continue;

                tip.ExpandedVideo = new Uri(clip);
                tip.ExpandedImage = null;
            }
        }
        catch (Exception)
        {
            // Tooltip video is cosmetic; never block add-in startup over it.
        }
    }
}
