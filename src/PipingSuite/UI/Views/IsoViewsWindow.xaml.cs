using System.Windows;
using PipingSuite.UI.ViewModels;

namespace PipingSuite.UI.Views;

public partial class IsoViewsWindow : Window
{
    public IsoViewsWindow(IsoViewsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += result => DialogResult = result;
    }
}
