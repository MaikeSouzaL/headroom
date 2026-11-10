using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Headroom.App.Services;
using Headroom.Core.Cleanup.Deletion;
using Headroom.Core.Cleanup.Logging;
using Headroom.Core.Cleanup.Safety;
using Headroom.Core.Formatting;
using Headroom.Core.Scanning;

namespace Headroom.App.ViewModels;

/// <summary>Item de lista que representa um arquivo ou pasta (para o menu de contexto).</summary>
public interface IFileItem
{
    string FullPath { get; }
    bool IsDirectory { get; }
    long Size { get; }

    /// <summary>Nó correspondente na árvore do scan (quando houver).</summary>
    ScanNode? Node { get; }
}

/// <summary>
/// Base das páginas que listam arquivos/pastas: comandos do menu de contexto (Abrir no Explorer,
/// Copiar caminho, Propriedades, Mover para a Lixeira, Excluir permanentemente) e a barra de
/// ações dos itens marcados com a caixa de seleção.
/// </summary>
public abstract partial class FileActionsViewModel : ObservableObject
{
    private int _suspendChecked;

    protected FileActionsViewModel(AppServices services)
    {
        Services = services;
    }

    protected AppServices Services { get; }

    protected ScanResult? Result => Services.Session.Result;

    /// <summary>Verdadeiro enquanto uma exclusão está em andamento.</summary>
    protected bool IsDeleting { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCheckedToRecycleBinCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCheckedPermanentlyCommand))]
    [NotifyCanExecuteChangedFor(nameof(UncheckAllCommand))]
    private bool _hasChecked;

    [ObservableProperty]
    private string _checkedText = "Nenhum item marcado";

    /// <summary>Texto do botão "marcar todos" da barra de ações.</summary>
    public virtual string CheckAllLabel => "Marcar todos";

    /// <summary>Se a barra mostra o botão "marcar todos" (a árvore não mostra: marcaria o disco inteiro).</summary>
    public virtual bool CanCheckAll => true;

    /// <summary>Dica exibida na barra de ações.</summary>
    public virtual string CheckHint => "Espaço marca/desmarca as linhas selecionadas · botão direito para mais opções";

    /// <summary>Itens marcados na página (vazio quando a página não usa caixas de seleção).</summary>
    protected virtual IReadOnlyList<IFileItem> GetCheckedItems() => Array.Empty<IFileItem>();

    /// <summary>Marca ou desmarca todos os itens visíveis da página.</summary>
    protected virtual void SetAllChecked(bool value)
    {
    }

    /// <summary>Última verificação antes de apagar os marcados; a página pode recusar.</summary>
    protected virtual Task<bool> ValidateCheckedAsync(IReadOnlyList<IFileItem> items) => Task.FromResult(true);

    /// <summary>Recalcula o total marcado. Chamado pelas linhas quando a caixa muda.</summary>
    public void RefreshChecked()
    {
        if (_suspendChecked > 0)
        {
            return;
        }

        var items = RemoveNested(GetCheckedItems());
        HasChecked = items.Count > 0;
        CheckedText = items.Count == 0
            ? "Nenhum item marcado"
            : $"{ByteSize.FormatCount(items.Count)} marcado(s) · {ByteSize.Format(items.Sum(i => i.Size))}";
    }

    /// <summary>Executa várias marcações e recalcula o total uma única vez no fim.</summary>
    protected void BatchChecked(Action action)
    {
        _suspendChecked++;
        try
        {
            action();
        }
        finally
        {
            _suspendChecked--;
            RefreshChecked();
        }
    }

    [RelayCommand]
    private void CheckAll() => BatchChecked(() => SetAllChecked(true));

    [RelayCommand(CanExecute = nameof(HasChecked))]
    private void UncheckAll() => BatchChecked(() => SetAllChecked(false));

    [RelayCommand(CanExecute = nameof(HasChecked))]
    private Task DeleteCheckedToRecycleBinAsync() => DeleteCheckedAsync(DeletionMode.RecycleBin);

    [RelayCommand(CanExecute = nameof(HasChecked))]
    private Task DeleteCheckedPermanentlyAsync() => DeleteCheckedAsync(DeletionMode.Permanent);

    private async Task DeleteCheckedAsync(DeletionMode mode)
    {
        var items = GetCheckedItems();
        if (items.Count > 0 && await ValidateCheckedAsync(items))
        {
            await DeleteItemsAsync(items, mode);
        }
    }

    [RelayCommand]
    private void OpenInExplorer(object? parameter)
    {
        if (Resolve(parameter) is { } item)
        {
            Run(() => Services.Shell.OpenInExplorer(item.FullPath));
        }
    }

    [RelayCommand]
    private void CopyPath(object? parameter)
    {
        if (Resolve(parameter) is { } item)
        {
            Run(() => Services.Shell.CopyPath(item.FullPath));
        }
    }

    [RelayCommand]
    private void ShowProperties(object? parameter)
    {
        if (Resolve(parameter) is { } item)
        {
            Run(() => Services.Shell.ShowProperties(item.FullPath));
        }
    }

    [RelayCommand]
    private Task MoveToRecycleBinAsync(object? parameter) => DeleteItemsAsync(ResolveMany(parameter), DeletionMode.RecycleBin);

    [RelayCommand]
    private Task DeletePermanentlyAsync(object? parameter) => DeleteItemsAsync(ResolveMany(parameter), DeletionMode.Permanent);

    /// <summary>Apaga itens escolhidos pelo usuário, com confirmação, guarda de segurança e log.</summary>
    protected async Task<DeletionResult?> DeleteItemsAsync(IReadOnlyList<IFileItem> items, DeletionMode mode)
    {
        items = RemoveNested(items);
        if (items.Count == 0 || IsDeleting)
        {
            return null;
        }

        IsDeleting = true;
        try
        {
            return await DeleteCoreAsync(items, mode);
        }
        finally
        {
            IsDeleting = false;
            RefreshChecked();
        }
    }

    /// <summary>
    /// Remove itens repetidos e itens que já estão dentro de uma pasta da própria lista
    /// (apagar a pasta já leva o conteúdo; somar os dois contaria o espaço duas vezes).
    /// </summary>
    internal static IReadOnlyList<IFileItem> RemoveNested(IReadOnlyList<IFileItem> items)
    {
        var folders = items.Where(i => i.IsDirectory)
            .Select(i => i.FullPath.EndsWith('\\') ? i.FullPath : i.FullPath + "\\")
            .ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<IFileItem>(items.Count);
        foreach (var item in items)
        {
            bool insideFolder = folders.Any(f => item.FullPath.StartsWith(f, StringComparison.OrdinalIgnoreCase));
            if (!insideFolder && seen.Add(item.FullPath))
            {
                result.Add(item);
            }
        }

        return result;
    }

    private async Task<DeletionResult?> DeleteCoreAsync(IReadOnlyList<IFileItem> items, DeletionMode mode)
    {

        // Pré-verifica a lista negra para avisar antes de pedir confirmação.
        var blocked = items.Select(i => (Item: i, Check: DeletionGuard.Default.Check(i.FullPath)))
            .Where(x => x.Check.IsProtected).ToList();
        if (blocked.Count == items.Count)
        {
            await Services.Dialogs.InfoAsync("Item protegido", blocked[0].Check.Reason);
            return null;
        }

        long total = items.Sum(i => i.Size);
        string what = items.Count == 1
            ? $"\"{items[0].FullPath}\" ({ByteSize.Format(total)})"
            : $"{items.Count} itens ({ByteSize.Format(total)})";

        int alreadyInBin = mode == DeletionMode.RecycleBin ? items.Count(i => RecycleBinPath.Contains(i.FullPath)) : 0;
        if (alreadyInBin > 0)
        {
            await Services.Dialogs.InfoAsync("Já está na Lixeira",
                $"{(alreadyInBin == 1 ? "Um item" : $"{alreadyInBin} itens")} da seleção já {(alreadyInBin == 1 ? "está" : "estão")} na Lixeira. " +
                "Para liberar esse espaço, esvazie a Lixeira na tela Limpeza ou use \"Excluir permanentemente\". " +
                "Desmarque esses itens para mover o restante para a Lixeira.");
            return null;
        }

        bool noRecycleBin = mode == DeletionMode.RecycleBin && items.Any(i => !FileProbe.SupportsRecycleBin(i.FullPath));
        if (noRecycleBin)
        {
            await Services.Dialogs.InfoAsync("Disco sem Lixeira",
                "Pelo menos um item está em um disco removível ou de rede, que não tem Lixeira. " +
                "Use \"Excluir permanentemente\" se tiver certeza.");
            return null;
        }

        bool confirmed = mode == DeletionMode.RecycleBin
            ? await Services.Dialogs.ConfirmAsync("Mover para a Lixeira", $"Mover {what} para a Lixeira?", "Mover para a Lixeira")
            : await Services.Dialogs.ConfirmAsync("Excluir permanentemente",
                $"Excluir PERMANENTEMENTE {what}?\n\nEsta ação não pode ser desfeita: os itens não vão para a Lixeira.",
                "Excluir permanentemente", danger: true);
        if (!confirmed)
        {
            return null;
        }

        using var log = CleanupLog.Create("exclusao-manual");
        var deleter = new FileDeleter(log: log);
        var targets = items.Select(i => new DeletionTarget(i.FullPath, i.IsDirectory, i.Size)).ToList();
        DeletionResult result;
        try
        {
            result = await deleter.DeleteAsync(targets, new DeletionOptions { Mode = mode, LogLabel = "Exclusão manual" });
        }
        catch (Exception ex)
        {
            await Services.Dialogs.ErrorAsync("Não foi possível apagar", ex);
            return null;
        }

        // Atualiza a árvore do scan com o que realmente saiu.
        if (Result is { } scan)
        {
            foreach (var item in items)
            {
                if (!File.Exists(item.FullPath) && !Directory.Exists(item.FullPath))
                {
                    var node = item.Node ?? scan.FindNode(item.FullPath);
                    if (node is not null)
                    {
                        scan.RemoveNode(node);
                    }
                }
            }
        }

        OnItemsDeleted(items, result);
        if (result.TotalSkipped > 0 || result.Errors > 0)
        {
            await Services.Dialogs.InfoAsync("Exclusão concluída com avisos",
                $"{ByteSize.Format(result.BytesDeleted)} {(mode == DeletionMode.RecycleBin ? "movidos para a Lixeira" : "liberados")}.\n" +
                $"Pulados: {result.SkippedInUse} em uso, {result.SkippedAccessDenied} sem permissão, " +
                $"{result.SkippedProtected} protegidos. Erros: {result.Errors}.\n\nLog: {log.FilePath}");
        }

        return result;
    }

    /// <summary>Permite à página reagir após uma exclusão (ex.: remover linhas).</summary>
    protected virtual void OnItemsDeleted(IReadOnlyList<IFileItem> items, DeletionResult result)
    {
    }

    protected virtual IFileItem? Resolve(object? parameter) => parameter switch
    {
        IFileItem item => item,
        ScanNode node => new NodeItem(node),
        System.Collections.IList { Count: > 0 } list => Resolve(list[0]),
        _ => null,
    };

    private IReadOnlyList<IFileItem> ResolveMany(object? parameter)
    {
        if (parameter is System.Collections.IList list)
        {
            return list.Cast<object>().Select(Resolve).OfType<IFileItem>().ToList();
        }

        return Resolve(parameter) is { } single ? new[] { single } : Array.Empty<IFileItem>();
    }

    private async void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            await Services.Dialogs.ErrorAsync("Não foi possível concluir a ação", ex);
        }
    }

    /// <summary>Adaptador de <see cref="ScanNode"/> para <see cref="IFileItem"/>.</summary>
    public sealed record NodeItem(ScanNode ScanNode) : IFileItem
    {
        public string FullPath => ScanNode.FullPath;
        public bool IsDirectory => ScanNode.IsDirectory;
        public long Size => ScanNode.Size;
        public ScanNode? Node => ScanNode;
    }
}
