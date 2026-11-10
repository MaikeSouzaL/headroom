namespace Headroom.Core.Cli;

/// <summary>
/// Opções de linha de comando. Exemplos:
///   Headroom.exe --profile rapida --silent --log
///   Headroom.exe --check-space --notify
///   Headroom.exe --scan D:\Projetos
/// </summary>
public sealed class CommandLineOptions
{
    public string? Profile { get; private set; }
    public bool Silent { get; private set; }
    public bool Log { get; private set; }
    public bool Notify { get; private set; }
    public bool CheckSpace { get; private set; }
    public bool Permanent { get; private set; }
    public bool Help { get; private set; }
    public string? ScanPath { get; private set; }
    public List<string> Errors { get; } = new();

    /// <summary>Há algo para fazer sem abrir a janela principal.</summary>
    public bool IsCommandLineMode => Profile is not null || CheckSpace || Help || ScanPath is not null || Errors.Count > 0;

    public static string Usage =>
        """
        Headroom — espaço em disco, limpeza e desempenho

        Uso:
          Headroom.exe                         Abre a interface.
          Headroom.exe --profile <perfil>      Limpa sem interface usando um perfil:
                                                 rapida | completa | dev | gamer | personalizada
          Headroom.exe --scan <pasta|disco>    Escaneia e mostra um resumo no console.
          Headroom.exe --check-space           Verifica o espaço livre dos discos.

        Opções:
          --silent, -s       Não pergunta nada (programas abertos: arquivos em uso são pulados).
          --log              Grava o log detalhado em %LOCALAPPDATA%\Headroom\logs (padrão em --silent).
          --notify           Mostra uma notificação com o resultado.
          --permanent        Exclui permanentemente em vez de mover para a Lixeira.
          --help, -h, /?     Mostra esta ajuda.

        Códigos de saída: 0 = sucesso, 1 = erro, 2 = argumentos inválidos.
        """;

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var options = new CommandLineOptions();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i].Trim();
            string key = arg.StartsWith("--", StringComparison.Ordinal) ? arg[2..] : arg.TrimStart('-', '/');
            switch (key.ToLowerInvariant())
            {
                case "profile":
                case "perfil":
                case "p":
                    options.Profile = NextValue(args, ref i, options, arg)?.ToLowerInvariant();
                    break;
                case "silent":
                case "silencioso":
                case "s":
                    options.Silent = true;
                    options.Log = true;
                    break;
                case "log":
                    options.Log = true;
                    break;
                case "notify":
                case "notificar":
                    options.Notify = true;
                    break;
                case "check-space":
                case "verificar-espaco":
                    options.CheckSpace = true;
                    break;
                case "permanent":
                case "permanente":
                    options.Permanent = true;
                    break;
                case "scan":
                case "escanear":
                    options.ScanPath = NextValue(args, ref i, options, arg);
                    break;
                case "help":
                case "ajuda":
                case "h":
                case "?":
                    options.Help = true;
                    break;
                default:
                    options.Errors.Add($"Argumento desconhecido: {arg}");
                    break;
            }
        }

        if (options.Profile is { } profile && profile is not ("rapida" or "completa" or "dev" or "gamer" or "personalizada"))
        {
            options.Errors.Add($"Perfil desconhecido: {profile}. Use rapida, completa, dev, gamer ou personalizada.");
        }

        return options;
    }

    private static string? NextValue(IReadOnlyList<string> args, ref int i, CommandLineOptions options, string name)
    {
        if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            i++;
            return args[i].Trim().Trim('"');
        }

        options.Errors.Add($"Faltou o valor de {name}.");
        return null;
    }
}
