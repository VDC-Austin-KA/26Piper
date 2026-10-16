using System.Windows;
using PipingSuite.UI.ViewModels;

namespace PipingSuite.UI.Views;

public partial class AssemblyOptionsWindow : Window
{
    public AssemblyOptionsWindow(AssemblyOptionsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += result => DialogResult = result;
    }
}
