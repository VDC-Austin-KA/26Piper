using System.Windows;
using PipingSuite.UI.ViewModels;

namespace PipingSuite.UI.Views;

public partial class CopyOletWindow : Window
{
    public CopyOletWindow(CopyOletViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += result => DialogResult = result;
    }
}
