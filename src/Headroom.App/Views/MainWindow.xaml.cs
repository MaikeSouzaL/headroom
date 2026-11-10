using System.Windows;
using Headroom.App.Services;
using Headroom.App.ViewModels;
using Wpf.Ui.Controls;

namespace Headroom.App.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainViewModel viewModel, AppServices services)
    {
        InitializeComponent();
        DataContext = viewModel;
        WindowBackdropType = ThemeService.Backdrop;
        if (WindowBackdropType == WindowBackdropType.None)
        {
            // Windows 10: sem Mica, usa o fundo sólido do tema.
            SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        }

        services.Theme.Attach(this);
        services.Theme.Apply(services.Settings.Theme);
    }
}
