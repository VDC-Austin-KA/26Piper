using System.Windows;
using PipingSuite.UI.ViewModels;

namespace PipingSuite.UI.Views;

public partial class EzIsoWindow : Window
{
    public EzIsoWindow(EzIsoViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
