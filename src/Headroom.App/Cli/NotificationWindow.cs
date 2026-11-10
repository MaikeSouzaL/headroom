using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Headroom.App.Cli;

/// <summary>
/// Notificação discreta no canto inferior direito (usada pela limpeza agendada e pelo alerta de espaço).
/// Some sozinha após 12 segundos ou ao clicar. Não depende de pacotes de notificação do Windows.
/// </summary>
internal sealed class NotificationWindow : Window
{
    private NotificationWindow(string title, string message)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = 380;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x23, 0x28)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x6C, 0xBD)),
            BorderThickness = new Thickness(0, 0, 0, 3),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 14),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Headroom", Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)), FontSize = 12 },
                    new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 4) },
                    new TextBlock { Text = message, Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD4, 0xD9)), FontSize = 13, TextWrapping = TextWrapping.Wrap },
                },
            },
        };
        Content = border;
        MouseLeftButtonUp += (_, _) => Close();
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Close();
        };
        timer.Start();
    }

    /// <summary>Mostra a notificação e bloqueia até ela fechar (precisa de thread STA).</summary>
    public static void ShowAndWait(string title, string message)
    {
        void Show()
        {
            var window = new NotificationWindow(title, message);
            window.Closed += (_, _) => Dispatcher.CurrentDispatcher.InvokeShutdown();
            window.Show();
            Dispatcher.Run();
        }

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA && Application.Current is null)
        {
            Show();
            return;
        }

        var thread = new Thread(Show) { IsBackground = false };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}
