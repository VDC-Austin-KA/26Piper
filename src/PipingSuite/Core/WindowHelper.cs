using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.UI;

namespace PipingSuite.Core;

public static class WindowHelper
{
    /// <summary>Shows a modal WPF window parented to Revit so it stays on top and centred.</summary>
    public static bool ShowModal(Window window, UIApplication uiApp)
    {
        new WindowInteropHelper(window).Owner = uiApp.MainWindowHandle;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        return window.ShowDialog() == true;
    }
}
