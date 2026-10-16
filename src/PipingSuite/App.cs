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
