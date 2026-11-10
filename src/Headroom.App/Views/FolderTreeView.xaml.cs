using System.Windows.Controls;
using System.Windows.Input;
using Headroom.App.ViewModels;

namespace Headroom.App.Views;

public partial class FolderTreeView : UserControl
{
    public FolderTreeView()
    {
        InitializeComponent();
    }

    private FolderTreeViewModel? ViewModel => DataContext as FolderTreeViewModel;

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel?.SelectedRow is { } row)
        {
            ViewModel.Toggle(row);
            e.Handled = true;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel?.SelectedRow is not { } row)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Right:
                ViewModel.Expand(row);
                e.Handled = true;
                break;
            case Key.Left:
                ViewModel.CollapseOrGoToParent(row);
                e.Handled = true;
                break;
            case Key.Enter:
                ViewModel.Toggle(row);
                e.Handled = true;
                break;
            case Key.Space:
                ViewModel.ToggleChecked(row);
                e.Handled = true;
                break;
        }
    }
}
