using System.Collections.Concurrent;

namespace Headroom.Core.Cleanup.Safety;

/// <summary>Pasta onde uma regra tem permissão de apagar.</summary>
/// <param name="Path">Raiz permitida.</param>
/// <param name="AllowRootItself">Se a própria raiz pode ser apagada (ex.: Windows.old, node_modules).</param>
public sealed record AllowedRoot(string Path, bool AllowRootItself = false);

/// <summary>
/// Porteiro de toda exclusão: aplica a lista negra (<see cref="ProtectedPaths"/>), exige que o item
/// esteja dentro das pastas permitidas pela regra e recusa caminhos que passem por junctions ou
/// links simbólicos abaixo da raiz permitida (nunca seguir links ao apagar).
/// </summary>
public sealed class DeletionGuard
{
    private readonly ConcurrentDictionary<string, bool> _reparseCache = new(StringComparer.OrdinalIgnoreCase);

    public DeletionGuard(ProtectedPaths policy)
    {
        Policy = policy;
    }

    public static DeletionGuard Default { get; } = new(ProtectedPaths.Default);

    public ProtectedPaths Policy { get; }

    /// <summary>
    /// Verifica se <paramref name="path"/> pode ser apagado. Sem <paramref name="allowedRoots"/> (exclusão
    /// manual pela interface), vale só a lista negra; com raízes, o item precisa estar dentro de uma delas.
    /// </summary>
    public ProtectionResult Check(string path, IReadOnlyList<AllowedRoot>? allowedRoots = null,
        bool allowBrowserPrivacyData = false)
    {
        string full;
        try
        {
            full = ProtectedPaths.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ProtectionResult.Blocked("Caminho inválido.");
        }

        var policy = Policy.Check(full, allowBrowserPrivacyData);
        if (policy.IsProtected)
        {
            return policy;
        }

        if (allowedRoots is null)
        {
            return ProtectionResult.Allowed;
        }

        foreach (var allowed in allowedRoots)
        {
            string root;
            try
            {
                root = ProtectedPaths.Normalize(allowed.Path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!ProtectedPaths.IsSameOrInside(full, root))
            {
                continue;
            }

            bool isRoot = full.Length == root.TrimEnd('\\').Length;
            if (isRoot && !allowed.AllowRootItself)
            {
                return ProtectionResult.Blocked("A pasta-raiz desta regra não pode ser apagada, só o conteúdo.");
            }

            if (PassesThroughLink(full, root))
            {
                return ProtectionResult.Blocked("O caminho passa por um link simbólico/junction — links nunca são seguidos ao apagar.");
            }

            return ProtectionResult.Allowed;
        }

        return ProtectionResult.Blocked("O item está fora das pastas permitidas para esta limpeza.");
    }

    /// <summary>true se alguma pasta entre a raiz (exclusive) e o item (exclusive) for um reparse point.</summary>
    private bool PassesThroughLink(string full, string root)
    {
        string? current = Path.GetDirectoryName(full);
        string rootTrimmed = root.TrimEnd('\\');
        while (current is not null && current.Length > rootTrimmed.Length)
        {
            bool isLink = _reparseCache.GetOrAdd(current, static dir =>
            {
                try
                {
                    var attributes = File.GetAttributes(dir);
                    return (attributes & FileAttributes.ReparsePoint) != 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            });

            if (isLink)
            {
                return true;
            }

            current = Path.GetDirectoryName(current);
        }

        return false;
    }
}
