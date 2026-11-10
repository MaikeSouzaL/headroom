using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Headroom.App.Services;
using Headroom.App.ViewModels;
using Headroom.App.Views;
using Headroom.Core.Settings;
using LiveChartsCore.SkiaSharpView;

namespace Headroom.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var culture = CultureInfo.GetCultureInfo("pt-BR");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash(args.Exception);
            args.SetObserved();
        };

        Services = new AppServices();

        // Gráficos com fundo transparente: as cores de texto/eixos são definidas pelos ViewModels conforme o tema.
        LiveChartsCore.LiveCharts.Configure(config => config
            .AddSkiaSharp()
            .AddDefaultMappers()
            .AddDefaultTheme(theme => theme.VirtualBackroundColor = new LiveChartsCore.Drawing.LvcColor(0, 0, 0, 0)));

        var mainViewModel = new MainViewModel(Services);
        var window = new MainWindow(mainViewModel, Services);
        MainWindow = window;
        window.Show();

        // Reabre as últimas análises salvas (sem escanear), se a opção estiver ligada.
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () => await mainViewModel.OpenLastAnalysesOnStartAsync()));
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        e.Handled = true;
        MessageBox.Show(
            "Ocorreu um erro inesperado e a operação foi interrompida.\n\n" + DialogService.FriendlyMessage(e.Exception) +
            "\n\nOs detalhes foram gravados em " + AppPaths.LogsDirectory,
            "Headroom", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    internal static void LogCrash(Exception ex)
    {
        try
        {
            string dir = AppPaths.EnsureDirectory(AppPaths.LogsDirectory);
            File.AppendAllText(Path.Combine(dir, "erros.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
