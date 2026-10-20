using System.Windows;
using PipingSuite.UI.ViewModels;

namespace PipingSuite.UI.Views;

public partial class ReplaceConfigurationWindow : Window
{
    /// <param name="preview">Revit's live view of the branches (a PreviewControl), when one could be made.</param>
    public ReplaceConfigurationWindow(ReplaceConfigurationViewModel viewModel, UIElement? preview)
    {
        InitializeComponent();
        DataContext = viewModel;
        if (preview is not null) PreviewHost.Content = preview;
        viewModel.CloseRequested += result => DialogResult = result;
    }
}
