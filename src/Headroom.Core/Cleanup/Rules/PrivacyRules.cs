using Headroom.Core.Cleanup.Safety;
using Headroom.Core.Settings;
using Headroom.Core.SystemTools;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace Headroom.Core.Cleanup.Rules;

/// <summary>
/// Limpa tabelas específicas de bancos SQLite de navegadores (ex.: histórico de downloads, dados de
/// formulário) sem apagar o arquivo, preservando favoritos e senhas. O navegador precisa estar fechado.
/// </summary>
public sealed class BrowserSqlRule : CleanupRuleBase
{
    private readonly IReadOnlyList<string> _databasePatterns;
    private readonly IReadOnlyList<string> _statements;

    public BrowserSqlRule(string id, string name, string description, IReadOnlyList<string> databasePatterns,
        IReadOnlyList<string> statements, string processName)
        : base(id, CleanupCategory.Browsers, name, description, RiskLevel.Review)
    {
        _databasePatterns = databasePatterns;
        _statements = statements;
        EnabledByDefault = false;
        RequiresProcessesClosed = true;
        ProcessesToClose = [processName];
        AlwaysPermanent = true;
        AllowBrowserPrivacyData = true;
    }

    public override bool IsApplicable(CleanupContext context) => _databasePatterns.Any(p => context.Paths.Expand(p).Count > 0);

    public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken)
    {
        var items = _databasePatterns
            .SelectMany(p => context.Paths.Expand(p))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new CleanupItem(p, DiskMeasure.SafeFileLength(p), false, File.GetLastWriteTime(p))
            {
                DisplayName = $"{Path.GetFileName(Path.GetDirectoryName(p))} — {Path.GetFileName(p)}",
                Reason = "Os registros são apagados de dentro do banco; o arquivo continua existindo.",
            })
            .ToList();

        // Estimativa conservadora: só o espaço que a compactação (VACUUM) costuma devolver.
        return Task.FromResult(new CleanupAnalysis(this, items, items.Sum(i => i.Size) / 10,
            "Requer o navegador fechado. Estimativa: o ganho real aparece após a limpeza."));
    }

    public override Task<CleanupRunResult> CleanAsync(CleanupAnalysis analysis, CleanupExecution execution,
        IProgress<CleanupProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var result = new CleanupRunResult(this);
            execution.Log.Section($"{Name} [{Id}]");
            foreach (var item in analysis.Items.Where(i => i.IsSelected))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var check = DeletionGuard.Default.Check(item.Path, null, allowBrowserPrivacyData: true);
                // O arquivo do banco em si não é apagado; só verificamos que é um banco de perfil de navegador.
                long before = DiskMeasure.SafeFileLength(item.Path);
                try
                {
                    progress?.Report(new CleanupProgress(Name, item.Path, 0.5));
                    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = item.Path,
                        Mode = SqliteOpenMode.ReadWrite,
                        Pooling = false,
                    }.ToString());
                    connection.Open();
                    foreach (var sql in _statements)
                    {
                        using var command = connection.CreateCommand();
                        command.CommandText = sql;
                        try
                        {
                            command.ExecuteNonQuery();
                        }
                        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
                        {
                            // Tabela inexistente nesta versão do navegador: ignora.
                        }
                    }

                    using (var vacuum = connection.CreateCommand())
                    {
                        vacuum.CommandText = "VACUUM;";
                        vacuum.ExecuteNonQuery();
                    }

                    connection.Close();
                    long after = DiskMeasure.SafeFileLength(item.Path);
                    result.BytesFreed += Math.Max(0, before - after);
                    execution.Log.Item("REGISTROS LIMPOS", Math.Max(0, before - after), item.Path, check.IsProtected ? "banco protegido: só tabelas" : null);
                }
                catch (SqliteException ex)
                {
                    result.Success = false;
                    result.Message = ex.SqliteErrorCode == 5
                        ? "O banco está em uso: feche o navegador e tente de novo."
                        : "Erro no banco de dados: " + ex.Message;
                    execution.Log.Item("ERRO", 0, item.Path, ex.Message);
                }
            }

            return result;
        }, cancellationToken);
}

/// <summary>
/// Apaga valores/chaves de registro de privacidade (RunMRU, TypedPaths...) com backup .reg obrigatório.
/// </summary>
public sealed class RegistryPrivacyRule : CleanupRuleBase
{
    private readonly IReadOnlyList<string> _keys;
    private readonly bool _valuesOnly;

    /// <param name="keys">Chaves de HKCU (sem o prefixo), ex.: Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU.</param>
    /// <param name="valuesOnly">Apaga só os valores (mantém a chave).</param>
    public RegistryPrivacyRule(string id, string name, string description, IReadOnlyList<string> keys, bool valuesOnly = true)
        : base(id, CleanupCategory.Privacy, name, description, RiskLevel.Safe)
    {
        _keys = keys;
        _valuesOnly = valuesOnly;
        EnabledByDefault = false;
        AlwaysPermanent = true;
    }

    public override Task<CleanupAnalysis> AnalyzeAsync(CleanupContext context, CancellationToken cancellationToken)
    {
        var items = new List<CleanupItem>();
        foreach (var keyPath in _keys)
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            if (key is null)
            {
                continue;
            }

            int count = key.GetValueNames().Count(n => n.Length > 0) + (_valuesOnly ? 0 : key.SubKeyCount);
            if (count > 0)
            {
                items.Add(new CleanupItem(@"HKCU\" + keyPath, 0, false, DateTime.Now, count)
                {
                    DisplayName = $@"HKCU\{keyPath} ({count} entradas)",
                    Reason = "Backup .reg é criado antes de apagar.",
                });
            }
        }

        return Task.FromResult(new CleanupAnalysis(this, items, 0, items.Count == 0 ? "Nada a limpar." : "Libera pouco espaço; melhora a privacidade."));
    }

    public override async Task<CleanupRunResult> CleanAsync(CleanupAnalysis analysis, CleanupExecution execution,
        IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new CleanupRunResult(this);
        execution.Log.Section($"{Name} [{Id}]");
        string backupDir = AppPaths.EnsureDirectory(Path.Combine(AppPaths.RegistryBackupsDirectory, $"privacidade-{DateTime.Now:yyyyMMdd-HHmmss}"));
        foreach (var item in analysis.Items.Where(i => i.IsSelected))
        {
            string keyPath = item.Path[5..];
            string backup = Path.Combine(backupDir, $"{Id}-{Math.Abs(keyPath.GetHashCode()):X}.reg");
            var export = await CommandRunner.RunAsync(CommandRunner.SystemTool("reg.exe"), $"export \"{item.Path}\" \"{backup}\" /y",
                TimeSpan.FromMinutes(1), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!export.Success)
            {
                result.Success = false;
                result.Message = "Não foi possível criar o backup do registro; nada foi apagado.";
                execution.Log.Item("ERRO (BACKUP)", 0, item.Path, export.Combined.Trim());
                continue;
            }

            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key is null)
            {
                continue;
            }

            foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }

            if (!_valuesOnly)
            {
                foreach (var sub in key.GetSubKeyNames())
                {
                    key.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
                }
            }

            execution.Log.Item("REGISTRO LIMPO", 0, item.Path, "Backup: " + backup);
        }

        return result;
    }
}

/// <summary>Executa uma ação de privacidade sem arquivos (ex.: limpar histórico da área de transferência, cache DNS).</summary>
public sealed class PrivacyCommandRule : CommandRule
{
    public PrivacyCommandRule(string id, string name, string description, params CommandStep[] steps)
        : base(id, CleanupCategory.Privacy, name, description, RiskLevel.Safe)
    {
        Steps = steps;
        EnabledByDefault = false;
        ActionText = "Libera pouco ou nenhum espaço; melhora a privacidade.";
    }
}
