using System.Windows;
using PipingSuite.UI.ViewModels;

namespace PipingSuite.UI.Views;

public partial class ColorSplasherWindow : Window
{
    public ColorSplasherWindow(ColorSplasherViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
