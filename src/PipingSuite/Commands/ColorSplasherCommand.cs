using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PipingSuite.Core;
using PipingSuite.Services;
using PipingSuite.UI.ViewModels;
using PipingSuite.UI.Views;
using WpfColor = System.Windows.Media.Color;

namespace PipingSuite.Commands;

/// <summary>
/// Colour elements in the active view by parameter value (BIM One Color Splasher workflow).
/// The window is modal, so its buttons run inside this command's API context.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ColorSplasherCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var view = uiDoc.ActiveView;

        if (view is ViewSchedule || view.ViewType is ViewType.ProjectBrowser or ViewType.SystemBrowser or ViewType.DrawingSheet)
        {
            TaskDialog.Show("Color Splasher", "Open a model view (plan, section, 3D) first.");
            return Result.Cancelled;
        }

        try
        {
            var service = new ColorSplasherService(uiDoc.Document, view);
            var categories = service.GetCategories();
            if (categories.Count == 0)
            {
                TaskDialog.Show("Color Splasher", "No model elements are visible in this view.");
                return Result.Cancelled;
            }

            var vm = new ColorSplasherViewModel(categories, new Host(service, uiDoc));
            WindowHelper.ShowModal(new ColorSplasherWindow(vm), commandData.Application);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }

    private sealed class Host(ColorSplasherService service, UIDocument uiDoc) : IColorSplasherHost
    {
        public IReadOnlyList<SplashValue> GetValues(IReadOnlyCollection<ElementId> categoryIds, string parameter) =>
            service.GetValues(categoryIds, parameter);

        public void Apply(IEnumerable<(IReadOnlyList<ElementId> Ids, WpfColor Color)> entries)
        {
            service.Apply(entries.Select(e => (e.Ids, new Color(e.Color.R, e.Color.G, e.Color.B))));
            uiDoc.RefreshActiveView(); // visible behind the still-open dialog
        }

        public int Clear(IReadOnlyCollection<ElementId> categoryIds)
        {
            int n = service.Clear(categoryIds);
            uiDoc.RefreshActiveView();
            return n;
        }

        public void Select(IReadOnlyCollection<ElementId> ids) => uiDoc.Selection.SetElementIds(ids.ToList());

        /// <summary>Revit's own colour dialog, so it matches the rest of the UI.</summary>
        public WpfColor? PickColor(WpfColor current)
        {
            using var dlg = new ColorSelectionDialog { OriginalColor = new Color(current.R, current.G, current.B) };
            if (dlg.Show() != ItemSelectionDialogResult.Confirmed) return null;
            var c = dlg.SelectedColor;
            return WpfColor.FromRgb(c.Red, c.Green, c.Blue);
        }
    }
}
