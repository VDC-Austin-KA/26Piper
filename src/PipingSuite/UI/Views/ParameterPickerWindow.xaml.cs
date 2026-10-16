using System.Windows;
using PipingSuite.UI.ViewModels;

namespace PipingSuite.UI.Views;

public partial class ParameterPickerWindow : Window
{
    public ParameterPickerWindow(ParameterPickerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += result => DialogResult = result;
    }
}
