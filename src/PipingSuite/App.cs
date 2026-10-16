using System.Reflection;
using Autodesk.Revit.UI;
using PipingSuite.Commands;

namespace PipingSuite;

/// <summary>Add-in entry point: builds the "Piping Suite" ribbon tab.</summary>
public sealed class App : IExternalApplication
{
    private const string TabName = "Piping Suite";

    public Result OnStartup(UIControlledApplication application)
    {
        application.CreateRibbonTab(TabName);
        var panel = application.CreateRibbonPanel(TabName, "Assemblies & Parameters");

        AddButton<CreateAssemblyCommand>(panel, "Create\nAssemblies", "Group selected pipes into one assembly per connected run / line number.");
        panel.AddSeparator();
        AddButton<CopyParametersCommand>(panel, "Copy\nParameters", "Copy chosen parameter values from a source element.");
        AddButton<PasteParametersCommand>(panel, "Paste\nParameters", "Paste copied parameter values onto the selected elements.");
        AddButton<CombineParametersCommand>(panel, "Combine\nParameters", "Create a shared parameter that joins two existing parameters, e.g. PKG + LINENUMBER.");

        var routing = application.CreateRibbonPanel(TabName, "Routing");
        AddButton<AlignConnectCommand>(routing, "Align &\nConnect",
            "Fix small offsets and connect two piping elements. Moves the smaller pipe; the main and fittings stay put. Tees into the main when needed.");

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    private static void AddButton<TCommand>(RibbonPanel panel, string text, string tooltip) where TCommand : IExternalCommand
    {
        var type = typeof(TCommand);
        var data = new PushButtonData(type.Name, text, Assembly.GetExecutingAssembly().Location, type.FullName)
        {
            ToolTip = tooltip,
        };
        panel.AddItem(data);
    }
}
