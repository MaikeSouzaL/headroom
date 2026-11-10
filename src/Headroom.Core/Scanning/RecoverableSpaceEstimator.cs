namespace Headroom.Core.Scanning;

/// <summary>Um local conhecido de lixo/caches encontrado na árvore do scan.</summary>
public sealed record RecoverableItem(string Label, string Path, long Bytes);

/// <summary>
/// Estima o "espaço recuperável": soma locais conhecidos de temporários, caches e restos de
/// atualização que existem na árvore escaneada, mais os artefatos de desenvolvimento.
/// É uma estimativa (a tela de Limpeza mostra o valor exato depois de "Analisar").
/// </summary>
public static class RecoverableSpaceEstimator
{
    private static readonly string[] BrowserCacheFolders = ["Cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache"];

    public static IReadOnlyList<RecoverableItem> Estimate(ScanResult result)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string driveRoot = Path.GetPathRoot(result.RootPath) ?? result.RootPath;

        var candidates = new List<(string Label, string Path)>
        {
            ("Temporários do usuário", Path.TrimEndingDirectorySeparator(Path.GetTempPath())),
            ("Temporários do Windows", Path.Combine(windows, "Temp")),
            ("Cache do Windows Update", Path.Combine(windows, "SoftwareDistribution", "Download")),
            ("Otimização de Entrega", Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local",
                "Microsoft", "Windows", "DeliveryOptimization", "Cache")),
            ("Lixeira", Path.Combine(driveRoot, "$Recycle.Bin")),
            ("Windows.old", Path.Combine(driveRoot, "Windows.old")),
            ("Restos de atualização ($Windows.~BT)", Path.Combine(driveRoot, "$Windows.~BT")),
            ("Restos de atualização ($Windows.~WS)", Path.Combine(driveRoot, "$Windows.~WS")),
            ("Arquivos de instalação (ESD)", Path.Combine(driveRoot, "ESD")),
            ("Relatórios de erro do Windows", Path.Combine(programData, "Microsoft", "Windows", "WER")),
            ("Dump de memória", Path.Combine(windows, "MEMORY.DMP")),
            ("Minidumps", Path.Combine(windows, "Minidump")),
            ("LiveKernelReports", Path.Combine(windows, "LiveKernelReports")),
            ("Dumps de aplicativos", Path.Combine(local, "CrashDumps")),
            ("Cache de shaders DirectX", Path.Combine(local, "D3DSCache")),
            ("Cache de shaders NVIDIA", Path.Combine(local, "NVIDIA", "DXCache")),
            ("Cache OpenGL NVIDIA", Path.Combine(local, "NVIDIA", "GLCache")),
            ("Cache de shaders AMD", Path.Combine(local, "AMD", "DxCache")),
            ("Miniaturas e ícones", Path.Combine(local, "Microsoft", "Windows", "Explorer")),
            ("Cache do npm", Path.Combine(local, "npm-cache")),
            ("Cache do Yarn", Path.Combine(local, "Yarn", "Cache")),
            ("Cache do pip", Path.Combine(local, "pip", "cache")),
            ("Cache HTTP do NuGet", Path.Combine(local, "NuGet", "v3-cache")),
            ("Cache do Gradle", Path.Combine(profile, ".gradle", "caches")),
            ("Instaladores de drivers NVIDIA", Path.Combine(driveRoot, "NVIDIA")),
            ("Instaladores de drivers AMD", Path.Combine(driveRoot, "AMD")),
        };

        var items = new List<RecoverableItem>();
        var included = new List<ScanNode>();

        void TryAdd(string label, string path)
        {
            var node = result.FindNode(path);
            if (node is null || node.Size <= 0)
            {
                return;
            }

            if (included.Any(n => ReferenceEquals(n, node) || n.IsAncestorOf(node) || node.IsAncestorOf(n)))
            {
                return;
            }

            included.Add(node);
            items.Add(new RecoverableItem(label, node.FullPath, node.Size));
        }

        foreach (var (label, path) in candidates)
        {
            TryAdd(label, path);
        }

        // Caches de navegadores (todos os perfis).
        foreach (var browserRoot in Cleanup.Safety.BrowserLocations.ProfileRoots(local,
                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)))
        {
            var rootNode = result.FindNode(browserRoot);
            if (rootNode is null)
            {
                continue;
            }

            foreach (var cache in BrowserCacheFolders)
            {
                TryAdd("Cache de navegador", Path.Combine(browserRoot, cache));
            }

            foreach (var profileNode in rootNode.Children.Where(c => c.IsDirectory))
            {
                foreach (var cache in BrowserCacheFolders.Append("cache2"))
                {
                    TryAdd("Cache de navegador", Path.Combine(profileNode.FullPath, cache));
                }
            }
        }

        foreach (var artifact in result.DevArtifacts)
        {
            if (!included.Any(n => n.IsAncestorOf(artifact.Node) || ReferenceEquals(n, artifact.Node)))
            {
                included.Add(artifact.Node);
                items.Add(new RecoverableItem($"Artefato de desenvolvimento ({artifact.KindName})", artifact.Path, artifact.Size));
            }
        }

        return items.OrderByDescending(i => i.Bytes).ToList();
    }
}
