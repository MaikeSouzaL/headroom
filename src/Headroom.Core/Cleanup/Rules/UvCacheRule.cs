namespace Headroom.Core.Cleanup.Rules;

/// <summary>
/// Cache do uv: igual a uma regra de conteúdo de pasta (cada balde do cache vira um item e hardlinks contam uma
/// vez), com um cuidado a mais para <c>builds-v0</c>.
/// </summary>
/// <remarks>
/// <para>
/// O uv cria um ambiente virtual temporário em <c>builds-v0\.tmpXXXXXX</c> a cada <c>uvx</c>, <c>uv run --with</c>
/// ou build isolado e o apaga ao terminar. Quando o processo é encerrado à força (por exemplo, servidores MCP
/// fechados junto com o app que os abriu), o ambiente fica para trás. Há PCs com milhares deles: 9 mil ambientes,
/// 6 milhões de pastas (a maioria vazias) e 25 milhões de caminhos, o que levava a análise a 14 minutos.
/// </para>
/// <para>
/// Acima de <see cref="DefaultMeasuredEnvironments"/> ambientes, uma amostra espalhada é medida de verdade e o
/// tamanho de <c>builds-v0</c> é estimado a partir dela; o motivo do item diz que é estimativa. O resto do cache é
/// medido antes e inteiro, para que os arquivos que os ambientes compartilham com <c>archive-v0</c> (hardlinks)
/// sejam contados lá, uma vez. A limpeza não muda: a pasta <c>builds-v0</c> inteira sai pelo caminho normal
/// (Lixeira por padrão, DeletionGuard, ProtectedPaths, links não seguidos, arquivos em uso pulados).
/// </para>
/// </remarks>
public sealed class UvCacheRule : PathRule
{
    /// <summary>Balde dos ambientes temporários do uv.</summary>
    public const string BuildsBucket = "builds-v0";

    /// <summary>Até quantos ambientes temporários são medidos um a um (acima disso, é o tamanho da amostra).</summary>
    public const int DefaultMeasuredEnvironments = 384;

    /// <summary>Nome das pastas temporárias do uv (os ambientes e as sobras vazias dentro deles).</summary>
    internal const string TemporaryFolderPattern = ".tmp*";

    private readonly int _measuredEnvironments;

    public UvCacheRule(PathRuleDefinition definition, int measuredEnvironments = DefaultMeasuredEnvironments)
        : base(definition)
    {
        _measuredEnvironments = Math.Max(1, measuredEnvironments);
    }

    private protected override IEnumerable<CleanupItem> CollectContents(string directory, PathTarget target, HardLinkSet links,
        CancellationToken ct)
    {
        string builds = Path.Join(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), BuildsBucket);
        var contents = IsPlainDirectory(builds) && TakesEverything(target) ? ListBuilds(builds) : null;
        if (contents is null)
        {
            // Sem builds-v0, com filtros, ou builds-v0 é link/junction (nunca seguido): percurso comum.
            return base.CollectContents(directory, target, links, ct);
        }

        // Primeiro o resto do cache, inteiro: os arquivos que os ambientes compartilham com archive-v0 (hardlinks)
        // ficam contados lá, sempre no mesmo item; builds-v0 fica só com o que é dele.
        var items = ContentWalker.Collect(directory, target, ct, links: links, skipDirectory: builds).Items;
        if (contents.Environments.Count > _measuredEnvironments)
        {
            items.Add(EstimateBuilds(builds, contents, links, ct));
            return items;
        }

        // Poucos ambientes: medidos inteiros, com o mesmo agrupamento que a pasta teria no percurso do cache.
        var measured = ContentWalker.Collect(builds, target, ct, links: links);
        if (measured.AllMatch)
        {
            var newest = measured.Newest == DateTime.MinValue ? new DirectoryInfo(builds).LastWriteTime : measured.Newest;
            items.Add(new CleanupItem(builds, measured.Size, true, newest, measured.Files));
        }
        else
        {
            items.AddRange(measured.Items);
        }

        return items;
    }

    private static bool IsPlainDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A estimativa só vale para o alvo "tudo" (sem filtros de nome, idade ou tamanho), que é o do catálogo.</summary>
    private static bool TakesEverything(PathTarget target) =>
        target.Mode == TargetMode.Contents && target.Recursive && target.Pattern == "*" && target.MinAgeDays == 0 &&
        target.MinSize == 0 && target.Extensions is not { Count: > 0 } && target.Exclude is not { Count: > 0 };

    private CleanupItem EstimateBuilds(string builds, BuildsContents contents, HardLinkSet links, CancellationToken ct)
    {
        var environments = contents.Environments;
        int sampleSize = _measuredEnvironments;

        // Amostra sistemática ao longo da ordem de criação. Os ambientes não são parecidos: enquanto um arquivo de
        // archive-v0 tem menos de 1.023 links (limite do NTFS) o uv liga e o ambiente custa ~2 MB; depois disso
        // copia e o mesmo ambiente custa ~170 MB. Os grandes se concentram em períodos, então espalhar a amostra no
        // tempo erra bem menos que espalhar por nome (num cache real de 9 mil ambientes, desvio de 11% contra 17%).
        var sample = Enumerable.Range(0, sampleSize)
            .Select(i => environments[(int)((long)i * environments.Count / sampleSize)])
            .ToList();

        long size = 0;
        long files = 0;
        var newest = contents.LooseNewest;
        var gate = new object();
        try
        {
            Parallel.ForEach(sample,
                new ParallelOptions { MaxDegreeOfParallelism = ContentWalker.ThreadsFor(builds), CancellationToken = ct },
                environment =>
                {
                    // As pastas .tmp* dentro do ambiente são sobras vazias do uv (~70% das pastas; num cache real,
                    // nenhuma de 93 mil tinha conteúdo): não abri-las deixa cada ambiente ~3x mais barato de medir.
                    var measured = ContentWalker.Measure(environment, ct, maxThreads: 1, links: links,
                        skipDirectoriesNamed: TemporaryFolderPattern);
                    lock (gate)
                    {
                        size += measured.Size;
                        files += measured.Files;
                        if (measured.Newest > newest)
                        {
                            newest = measured.Newest;
                        }
                    }
                });
        }
        catch (AggregateException) when (ct.IsCancellationRequested)
        {
            // Cancelado no meio de um ambiente: o motor espera OperationCanceledException, não "erro na análise".
            throw new OperationCanceledException(ct);
        }

        double scale = environments.Count / (double)sampleSize;
        long estimatedSize = contents.LooseSize + (long)Math.Round(size * scale);
        long estimatedFiles = contents.LooseFiles + (long)Math.Round(files * scale);
        if (newest == DateTime.MinValue)
        {
            newest = Directory.GetLastWriteTime(builds);
        }

        return new CleanupItem(builds, estimatedSize, true, newest, (int)Math.Min(int.MaxValue, estimatedFiles))
        {
            Reason = $"{environments.Count:N0} ambientes temporários que o uv deixou para trás. " +
                     $"Tamanho estimado medindo {sampleSize} deles (hardlinks contam uma vez).",
        };
    }

    /// <summary>Subpastas de builds-v0 (ambientes temporários, em ordem de criação) e arquivos soltos; links nunca entram.</summary>
    private static BuildsContents? ListBuilds(string builds)
    {
        var environments = new List<(DateTime Created, string Path)>();
        long looseSize = 0;
        int looseFiles = 0;
        var looseNewest = DateTime.MinValue;
        try
        {
            foreach (var entry in new DirectoryInfo(builds).EnumerateFileSystemInfos("*", new EnumerationOptions
                     {
                         IgnoreInaccessible = true,
                         AttributesToSkip = 0,
                         RecurseSubdirectories = false,
                     }))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    // A data vem na própria listagem da pasta (sem custo extra por ambiente).
                    environments.Add((entry.CreationTimeUtc, entry.FullName));
                }
                else if (entry is FileInfo file)
                {
                    looseSize += file.Length;
                    looseFiles++;
                    if (file.LastWriteTime > looseNewest)
                    {
                        looseNewest = file.LastWriteTime;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var ordered = environments
            .OrderBy(e => e.Created)
            .ThenBy(e => e.Path, StringComparer.Ordinal)
            .Select(e => e.Path)
            .ToList();
        return new BuildsContents(ordered, looseSize, looseFiles, looseNewest);
    }

    private sealed record BuildsContents(List<string> Environments, long LooseSize, int LooseFiles, DateTime LooseNewest);
}
