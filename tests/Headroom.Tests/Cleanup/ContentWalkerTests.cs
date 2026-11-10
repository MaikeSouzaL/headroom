using Headroom.Core.Cleanup;
using Headroom.Core.Cleanup.Rules;
using Headroom.Core.Settings;
using Headroom.Tests.TestHelpers;

namespace Headroom.Tests.Cleanup;

/// <summary>
/// O percurso paralelo das regras de pasta (<see cref="ContentWalker"/>) precisa devolver exatamente o que o
/// percurso recursivo sequencial devolvia: mesmos itens, mesma ordem, mesmos tamanhos e datas, e nunca entrar
/// em junctions. A referência abaixo é o algoritmo anterior, copiado sem mudanças.
/// </summary>
public class ContentWalkerTests
{
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-40);

    /// <summary>Árvore no formato de um cache de dev: muitas pastas, profundidade variável, idades e extensões misturadas.</summary>
    private static void BuildCacheTree(TempDirectory temp, string outsideTarget)
    {
        var random = new Random(42);
        string[] extensions = [".jar", ".bin", ".log", ".lock", ".tmp"];
        for (int a = 0; a < 6; a++)
        {
            for (int b = 0; b < 5; b++)
            {
                int depth = random.Next(1, 4);
                string dir = Path.Combine("cache", $"v{a}", $"mod{b}");
                for (int d = 0; d < depth; d++)
                {
                    dir = Path.Combine(dir, $"n{d}");
                }

                int files = random.Next(1, 6);
                for (int f = 0; f < files; f++)
                {
                    // ~1/4 dos arquivos são recentes: as pastas que os contêm não podem virar um item só com idade mínima.
                    DateTime? when = random.Next(4) == 0 ? null : Old.AddHours(-random.Next(500));
                    temp.CreateFile(Path.Combine(dir, $"f{f}{extensions[random.Next(extensions.Length)]}"), random.Next(1, 400), lastWriteUtc: when);
                }
            }
        }

        temp.CreateFile(@"cache\v0\keep.lock", 7, lastWriteUtc: Old);
        temp.CreateFile(@"cache\v1\excluida\x.jar", 50, lastWriteUtc: Old);
        temp.CreateFile(@"cache\raiz.bin", 11, lastWriteUtc: Old);
        temp.CreateDirectory(@"cache\v2\vazia-recente");
        string emptyOld = temp.CreateDirectory(@"cache\v3\vazia-antiga");
        Directory.SetLastWriteTimeUtc(emptyOld, Old);
        TempDirectory.CreateJunction(temp.Combine("cache", "v4", "mod0", "link"), outsideTarget);
    }

    public static TheoryData<string, PathTarget> Targets => new()
    {
        { "tudo", new PathTarget() },
        { "idade", new PathTarget { MinAgeDays = 7 } },
        { "extensao", new PathTarget { Extensions = ["jar", ".bin"] } },
        { "excluir", new PathTarget { Exclude = ["excluida", "*.lock"] } },
        { "padrao-raso", new PathTarget { Pattern = "*.bin", Recursive = false } },
        { "tamanho", new PathTarget { MinSize = 200, MinAgeDays = 1 } },
    };

    [Theory]
    [MemberData(nameof(Targets))]
    public void Collect_ParallelEqualsSequentialReference(string name, PathTarget target)
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        outside.CreateFile("importante.docx", 999);
        BuildCacheTree(temp, outside.Path);
        string root = temp.Combine("cache");

        var expected = Reference.CollectContents(root, target);
        Assert.NotEmpty(expected.Items);

        foreach (int threads in new[] { 1, 2, 8 })
        {
            var actual = ContentWalker.Collect(root, target, CancellationToken.None, threads);
            Assert.True(expected.Items.Count == actual.Items.Count, $"{name}, {threads} thread(s): {actual.Items.Count} itens, esperado {expected.Items.Count}");
            for (int i = 0; i < expected.Items.Count; i++)
            {
                var e = expected.Items[i];
                var a = actual.Items[i];
                Assert.Equal((e.Path, e.Size, e.IsDirectory, e.LastModified, e.FileCount), (a.Path, a.Size, a.IsDirectory, a.LastModified, a.FileCount));
            }

            Assert.Equal((expected.AllMatch, expected.Size, expected.Files, expected.Newest), (actual.AllMatch, actual.Size, actual.Files, actual.Newest));
            Assert.DoesNotContain(actual.Items, i => i.Path.Contains(@"\link", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Collect_GroupsWholeFoldersSoHugeCachesBecomeFewItems()
    {
        using var temp = new TempDirectory();
        for (int v = 0; v < 3; v++)
        {
            for (int m = 0; m < 40; m++)
            {
                temp.CreateFile($@"caches\{v}.0\modules\m{m}\a\b\lib.jar", 10);
                temp.CreateFile($@"caches\{v}.0\modules\m{m}\a\pom.xml", 1);
            }
        }

        var result = ContentWalker.Collect(temp.Combine("caches"), new PathTarget(), CancellationToken.None, 8);

        // Como em %USERPROFILE%\.gradle\caches: uma linha por versão, nada de 240 arquivos soltos.
        Assert.Equal(new[] { "0.0", "1.0", "2.0" }, result.Items.Select(i => i.Name).OrderBy(n => n));
        Assert.All(result.Items, i => Assert.True(i.IsDirectory));
        Assert.All(result.Items, i => Assert.Equal(80, i.FileCount));
        Assert.Equal(3 * 40 * 11, result.Items.Sum(i => i.Size));
    }

    [Fact]
    public void Measure_EqualsSequentialReferenceAndSkipsJunctions()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        outside.CreateFile("importante.docx", 999);
        BuildCacheTree(temp, outside.Path);

        var expected = Reference.MeasureDirectory(temp.Combine("cache"));
        foreach (int threads in new[] { 1, 8 })
        {
            Assert.Equal(expected, ContentWalker.Measure(temp.Combine("cache"), CancellationToken.None, threads));
        }

        Assert.Equal(expected, PathRule.MeasureDirectory(temp.Combine("cache"), CancellationToken.None));
    }

    [Fact]
    public async Task PathRule_ParallelAnalysisKeepsDeletionInsideTargetAndJunctionTargetIntact()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        outside.CreateFile("importante.docx", 999);
        BuildCacheTree(temp, outside.Path);

        var paths = new PathExpander();
        paths.SetVariable("TESTROOT", temp.Path);
        var rule = new PathRule(new PathRuleDefinition
        {
            Id = "t.walker",
            Name = "Teste",
            Targets = [new PathTarget { Path = @"%TESTROOT%\cache" }],
        });
        var analysis = await rule.AnalyzeAsync(new CleanupContext(new AppSettings(), false, paths: paths), CancellationToken.None);
        var result = await rule.CleanAsync(analysis,
            new CleanupExecution(Core.Cleanup.Deletion.DeletionMode.Permanent, Core.Cleanup.Logging.CleanupLog.Null(), false),
            null, CancellationToken.None);

        Assert.Equal(analysis.TotalBytes, result.BytesFreed);
        Assert.True(File.Exists(outside.Combine("importante.docx")));
        Assert.True(Directory.Exists(temp.Combine("cache")));
    }

    [Fact]
    public void Collect_HonorsCancellation()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(@"cache\a\b.bin", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ContentWalker.Collect(temp.Combine("cache"), new PathTarget(), cts.Token, 4));
    }

    [Fact]
    public void ProgressTracker_ShowsRulesStillRunningNotTheLastOneThatFinished()
    {
        var tracker = new AnalysisProgressTracker();
        tracker.Update(new CleanupProgress("uv: cache", CleanupEngine.AnalyzingDetail, 0));
        tracker.Update(new CleanupProgress("Cargo", CleanupEngine.AnalyzingDetail, 0));
        tracker.Update(new CleanupProgress("Gradle: caches", CleanupEngine.AnalyzingDetail, 0));

        // O caso relatado: o Gradle termina e o rótulo ficava "Analisando: Gradle: caches" por 20 minutos.
        string text = tracker.Update(new CleanupProgress("Gradle: caches", CleanupEngine.AnalyzedDetail, 0.5));
        Assert.Equal("Analisando: uv: cache (+1)", text);

        tracker.Update(new CleanupProgress("uv: cache", CleanupEngine.AnalyzedDetail, 0.7));
        Assert.Equal("Analisando: Cargo", tracker.Text);
        tracker.Update(new CleanupProgress("Cargo", CleanupEngine.AnalyzedDetail, 1));
        Assert.Empty(tracker.Running);
    }

    /// <summary>Algoritmo anterior do PathRule (sequencial, com FileInfo/DirectoryInfo), usado como referência.</summary>
    private static class Reference
    {
        public sealed record Result(bool AllMatch, long Size, int Files, DateTime Newest, List<CleanupItem> Items);

        public static Result CollectContents(string directory, PathTarget target)
        {
            var items = new List<CleanupItem>();
            bool allMatch = true;
            long size = 0;
            int files = 0;
            DateTime newest = DateTime.MinValue;
            DateTime cutoff = DateTime.Now.AddDays(-target.MinAgeDays);

            foreach (var entry in SafeEntries(directory))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    allMatch = false;
                    continue;
                }

                if (entry is DirectoryInfo dir)
                {
                    if (!target.Recursive || IsExcluded(dir.Name, target))
                    {
                        allMatch = false;
                        continue;
                    }

                    var sub = CollectContents(dir.FullName, target);
                    if (sub.Files == 0 && target.MinAgeDays > 0 && dir.LastWriteTime > cutoff)
                    {
                        allMatch = false;
                        continue;
                    }

                    if (sub.AllMatch)
                    {
                        items.Add(new CleanupItem(dir.FullName, sub.Size, true, sub.Newest == DateTime.MinValue ? dir.LastWriteTime : sub.Newest, sub.Files));
                    }
                    else
                    {
                        allMatch = false;
                        items.AddRange(sub.Items);
                    }

                    size += sub.Size;
                    files += sub.Files;
                    if (sub.Newest > newest)
                    {
                        newest = sub.Newest;
                    }

                    continue;
                }

                var file = (FileInfo)entry;
                if (!FileMatches(file, target, cutoff))
                {
                    allMatch = false;
                    continue;
                }

                items.Add(new CleanupItem(file.FullName, file.Length, false, file.LastWriteTime));
                size += file.Length;
                files++;
                if (file.LastWriteTime > newest)
                {
                    newest = file.LastWriteTime;
                }
            }

            return new Result(allMatch, size, files, newest, items);
        }

        public static (long Size, int Files, DateTime Newest) MeasureDirectory(string path)
        {
            long size = 0;
            int files = 0;
            DateTime newest = DateTime.MinValue;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                size += file.Length;
                files++;
                if (file.LastWriteTime > newest)
                {
                    newest = file.LastWriteTime;
                }
            }

            return (size, files, newest);
        }

        private static bool FileMatches(FileInfo file, PathTarget target, DateTime cutoff)
        {
            if (!PathExpander.MatchesWildcard(file.Name, target.Pattern) || IsExcluded(file.Name, target))
            {
                return false;
            }

            if (target.Extensions is { Count: > 0 } exts &&
                !exts.Any(e => file.Name.EndsWith(e.StartsWith('.') ? e : "." + e, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (target.MinAgeDays > 0 && file.LastWriteTime > cutoff)
            {
                return false;
            }

            return file.Length >= target.MinSize;
        }

        private static bool IsExcluded(string name, PathTarget target) =>
            target.Exclude is { Count: > 0 } excludes && excludes.Any(x => PathExpander.MatchesWildcard(name, x));

        private static List<FileSystemInfo> SafeEntries(string directory) =>
            new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                RecurseSubdirectories = false,
            }).ToList();
    }
}
