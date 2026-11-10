using System.Windows.Controls;
using System.Windows.Input;
using Headroom.App.ViewModels;

namespace Headroom.App.Views;

public partial class LargestFilesView : UserControl
{
    public LargestFilesView()
    {
        InitializeComponent();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && DataContext is LargestFilesViewModel vm)
        {
            vm.ToggleChecked(Grid.SelectedItems);
            e.Handled = true;
        }
    }
}
