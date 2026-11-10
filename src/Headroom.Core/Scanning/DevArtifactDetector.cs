namespace Headroom.Core.Scanning;

/// <summary>
/// Detecta pastas de artefatos de desenvolvimento (node_modules, bin/obj, .venv, __pycache__,
/// .gradle, Pods) na árvore já montada. Só a pasta mais externa é contada
/// (node_modules dentro de node_modules, ou __pycache__ dentro de um venv, não).
/// </summary>
public static class DevArtifactDetector
{
    [Flags]
    public enum ProjectMarkers
    {
        None = 0,
        DotnetProject = 1,
        Podfile = 2,
    }

    /// <summary>Marcador de projeto correspondente a um nome de arquivo.</summary>
    public static ProjectMarkers MarkerFromFileName(string fileName)
    {
        if (fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectMarkers.DotnetProject;
        }

        if (string.Equals(fileName, "Podfile", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectMarkers.Podfile;
        }

        return ProjectMarkers.None;
    }

    /// <summary>Marcadores observados entre os arquivos de uma pasta.</summary>
    public static ProjectMarkers MarkersOf(ScanNode directory)
    {
        var markers = ProjectMarkers.None;
        foreach (var child in directory.Children)
        {
            if (!child.IsDirectory)
            {
                markers |= MarkerFromFileName(child.Name);
            }
        }

        return markers;
    }

    /// <summary>
    /// Classifica uma subpasta pelo nome, pelos marcadores da pasta-pai e, para nomes ambíguos
    /// ("venv", "env", "Pods"), por um arquivo-marcador dentro dela.
    /// </summary>
    public static DevArtifactKind? Classify(ScanNode directory, ProjectMarkers parentMarkers)
    {
        string name = directory.Name;
        if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
        {
            return DevArtifactKind.NodeModules;
        }

        if (name.Equals("__pycache__", StringComparison.OrdinalIgnoreCase))
        {
            return DevArtifactKind.PyCache;
        }

        if (name.Equals(".venv", StringComparison.OrdinalIgnoreCase))
        {
            return DevArtifactKind.PythonVenv;
        }

        if (name.Equals(".gradle", StringComparison.OrdinalIgnoreCase))
        {
            return DevArtifactKind.Gradle;
        }

        if ((name.Equals("venv", StringComparison.OrdinalIgnoreCase) || name.Equals("env", StringComparison.OrdinalIgnoreCase)) &&
            directory.FindChild("pyvenv.cfg") is { IsDirectory: false })
        {
            return DevArtifactKind.PythonVenv;
        }

        if ((parentMarkers & ProjectMarkers.DotnetProject) != 0)
        {
            if (name.Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                return DevArtifactKind.DotnetBin;
            }

            if (name.Equals("obj", StringComparison.OrdinalIgnoreCase))
            {
                return DevArtifactKind.DotnetObj;
            }
        }

        if (name.Equals("Pods", StringComparison.Ordinal) &&
            ((parentMarkers & ProjectMarkers.Podfile) != 0 || directory.FindChild("Manifest.lock") is { IsDirectory: false }))
        {
            return DevArtifactKind.Pods;
        }

        return null;
    }

    /// <summary>Percorre a árvore de cima para baixo, sem entrar em artefatos já encontrados.</summary>
    public static List<DevArtifact> FindAll(ScanNode root)
    {
        var found = new List<DevArtifact>();
        var stack = new Stack<ScanNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var markers = MarkersOf(dir);
            foreach (var child in dir.Children)
            {
                if (!child.IsDirectory)
                {
                    continue;
                }

                var kind = Classify(child, markers);
                if (kind is not null)
                {
                    found.Add(new DevArtifact(child, kind.Value));
                }
                else
                {
                    stack.Push(child);
                }
            }
        }

        return found;
    }
}
