using System.Windows;
using PipingSuite.UI.ViewModels;

namespace PipingSuite.UI.Views;

public partial class AutoDimensionWindow : Window
{
    public AutoDimensionWindow(AutoDimensionViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += result => DialogResult = result;
    }
}
