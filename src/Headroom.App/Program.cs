using Headroom.App.Cli;
using Headroom.Core.Cli;

namespace Headroom.App;

/// <summary>
/// Ponto de entrada. Sem argumentos abre a interface; com argumentos de linha de comando
/// (ex.: --profile rapida --silent --log) executa sem janela principal.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = CommandLineOptions.Parse(args);
        if (options.IsCommandLineMode)
        {
            return CliRunner.Run(options);
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
