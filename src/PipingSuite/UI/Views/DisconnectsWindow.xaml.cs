using System.Windows;
using PipingSuite.UI.ViewModels;

namespace PipingSuite.UI.Views;

public partial class DisconnectsWindow : Window
{
    public DisconnectsWindow(DisconnectsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
