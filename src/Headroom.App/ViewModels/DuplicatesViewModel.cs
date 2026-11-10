using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Headroom.App.Services;
using Headroom.Core.Cleanup.Deletion;
using Headroom.Core.Duplicates;
using Headroom.Core.Formatting;
using Headroom.Core.Scanning;

namespace Headroom.App.ViewModels;

/// <summary>Arquivo dentro de um grupo de duplicados; o usuário marca quais cópias excluir.</summary>
public sealed partial class DuplicateFileViewModel : ObservableObject, IFileItem
{
    private readonly DuplicateGroupViewModel _group;

    public DuplicateFileViewModel(DuplicateFile file, DuplicateGroupViewModel group, bool isSuggestedKeep)
    {
        File = file;
        _group = group;
        IsSuggestedKeep = isSuggestedKeep;
    }

    public DuplicateFile File { get; }
    public string FullPath => File.Path;
    public string Name => File.Name;
    public string Folder => File.Folder;
    public bool IsDirectory => false;
    public long Size => File.Size;
    public ScanNode? Node => null;
    public DateTime Modified => File.LastWriteUtc;
    public DateTime Created => File.CreationUtc;

    /// <summary>A cópia mais antiga (provável original), sugerida para manter.</summary>
    public bool IsSuggestedKeep { get; }

    /// <summary>Marcada para excluir.</summary>
    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => _group.OnCheckedChanged();
}

/// <summary>Grupo de arquivos idênticos.</summary>
public sealed partial class DuplicateGroupViewModel : ObservableObject
{
    private readonly FileActionsViewModel? _owner;

    public DuplicateGroupViewModel(DuplicateGroup group, FileActionsViewModel? owner = null)
    {
        Group = group;
        _owner = owner;
        string keep = group.SuggestedKeep.Path;
        Files = new ObservableCollection<DuplicateFileViewModel>(
            group.Files.Select(f => new DuplicateFileViewModel(f, this, f.Path == keep)));
        ApplySuggestion();
    }

    public DuplicateGroup Group { get; }
    public ObservableCollection<DuplicateFileViewModel> Files { get; }
    public string Header => $"{Files.Count} cópias de {ByteSize.Format(Group.Size)} · desperdício de {ByteSize.Format(Group.Size * (Files.Count - 1))}";
    public string FirstName => Files.FirstOrDefault()?.Name ?? string.Empty;
    public long WastedBytes => Group.Size * Math.Max(0, Files.Count - 1);

    /// <summary>Todas as cópias estão marcadas: excluir faria perder o arquivo.</summary>
    public bool AllChecked => Files.Count > 0 && Files.All(f => f.IsChecked);

    /// <summary>Marca todas as cópias menos a sugerida para manter.</summary>
    public void ApplySuggestion()
    {
        var keep = Files.FirstOrDefault(f => f.IsSuggestedKeep) ?? Files.FirstOrDefault();
        foreach (var file in Files)
        {
            file.IsChecked = !ReferenceEquals(file, keep);
        }
    }

    internal void OnCheckedChanged()
    {
        OnPropertyChanged(nameof(AllChecked));
        _owner?.RefreshChecked();
    }

    public IEnumerable<DuplicateFileViewModel> ToRemove => Files.Where(f => f.IsChecked);
}

/// <summary>Busca de duplicados (tamanho → hash parcial 64 KB → hash completo) sobre o resultado do scan.</summary>
public sealed partial class DuplicatesViewModel : FileActionsViewModel
{
    private CancellationTokenSource? _cts;

    public DuplicatesViewModel(AppServices services) : base(services)
    {
        _minimumSizeMb = Math.Max(0.1, services.Settings.DuplicateMinimumBytes / (double)ByteSize.MB);
        services.Session.ResultChanged += (_, _) =>
        {
            if (!IsSearching && Groups.Count > 0 && !IsDeleting)
            {
                Groups.Clear();
                HasGroups = false;
                RefreshChecked();
                Summary = "O resultado do scan mudou. Procure duplicados novamente.";
            }
        };
    }

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = new();

    public override string CheckAllLabel => "Marcar sugestão";

    public override string CheckHint => "Sugestão: manter a cópia mais antiga de cada grupo e excluir as outras";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindCommand))]
    private bool _isSearching;

    [ObservableProperty] private double _minimumSizeMb;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _summary = "Clique em \"Procurar duplicados\". Só são comparados arquivos do último scan.";
    [ObservableProperty] private bool _hasGroups;

    private bool CanFind() => !IsSearching;

    [RelayCommand(CanExecute = nameof(CanFind))]
    private async Task FindAsync()
    {
        if (Result is not { } result)
        {
            await Services.Dialogs.InfoAsync("Nenhum scan", "Faça um scan antes de procurar duplicados.");
            return;
        }

        IsSearching = true;
        Groups.Clear();
        HasGroups = false;
        RefreshChecked();
        _cts = new CancellationTokenSource();
        long minBytes = (long)(MinimumSizeMb * ByteSize.MB);
        var progress = new Progress<DuplicateProgress>(p =>
        {
            ProgressText = $"{p.Stage}: {ByteSize.FormatCount(p.Processed)} de {ByteSize.FormatCount(p.Total)}";
            ProgressValue = p.Total == 0 ? 0 : p.Processed / (double)p.Total;
        });

        try
        {
            // Arquivos que já estão na Lixeira ficam de fora: o espaço deles volta ao esvaziá-la.
            var candidates = await Task.Run(() => result.Root.EnumerateFiles()
                .Where(f => f.Size >= minBytes)
                .Select(f => new FileCandidate(f.FullPath, f.Size, f.LastModifiedUtc))
                .Where(f => !RecycleBinPath.Contains(f.Path))
                .ToList());

            var groups = await new DuplicateFinder().FindAsync(candidates,
                new DuplicateOptions { MinimumSize = minBytes }, progress, _cts.Token);

            BatchChecked(() =>
            {
                foreach (var group in groups)
                {
                    Groups.Add(new DuplicateGroupViewModel(group, this));
                }
            });

            UpdateSummary();
        }
        catch (OperationCanceledException)
        {
            Summary = "Busca cancelada.";
        }
        catch (Exception ex)
        {
            await Services.Dialogs.ErrorAsync("Falha ao procurar duplicados", ex);
        }
        finally
        {
            IsSearching = false;
            ProgressText = string.Empty;
            ProgressValue = 0;
        }
    }

    [RelayCommand]
    private void CancelSearch() => _cts?.Cancel();

    protected override IReadOnlyList<IFileItem> GetCheckedItems() =>
        Groups.SelectMany(g => g.ToRemove).Cast<IFileItem>().ToList();

    protected override void SetAllChecked(bool value)
    {
        foreach (var group in Groups)
        {
            if (value)
            {
                group.ApplySuggestion();
            }
            else
            {
                foreach (var file in group.Files)
                {
                    file.IsChecked = false;
                }
            }
        }
    }

    /// <summary>Nunca apaga todas as cópias de um grupo: pelo menos uma precisa ficar.</summary>
    protected override async Task<bool> ValidateCheckedAsync(IReadOnlyList<IFileItem> items)
    {
        var allChecked = Groups.Where(g => g.AllChecked).ToList();
        if (allChecked.Count == 0)
        {
            return true;
        }

        await Services.Dialogs.InfoAsync("Mantenha pelo menos uma cópia",
            $"Em {ByteSize.FormatCount(allChecked.Count)} grupo(s) todas as cópias estão marcadas, " +
            $"por exemplo \"{allChecked[0].FirstName}\". Desmarque pelo menos uma cópia de cada grupo " +
            "(ou use \"Marcar sugestão\") para não perder o arquivo.");
        return false;
    }

    protected override void OnItemsDeleted(IReadOnlyList<IFileItem> items, DeletionResult result)
    {
        BatchChecked(() =>
        {
            foreach (var group in Groups.ToList())
            {
                foreach (var file in group.Files.Where(f => !System.IO.File.Exists(f.FullPath)).ToList())
                {
                    group.Files.Remove(file);
                }

                if (group.Files.Count < 2)
                {
                    Groups.Remove(group);
                }
                else
                {
                    group.OnCheckedChanged();
                }
            }
        });

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        HasGroups = Groups.Count > 0;
        long wasted = Groups.Sum(g => g.WastedBytes);
        Summary = Groups.Count == 0
            ? "Nenhum arquivo duplicado encontrado com esse tamanho mínimo."
            : $"{ByteSize.FormatCount(Groups.Count)} grupo(s) de arquivos idênticos · {ByteSize.Format(wasted)} desperdiçados. " +
              "Marque as cópias que quer excluir (já vem marcado: todas menos a mais antiga de cada grupo). " +
              "Arquivos que já estão na Lixeira não entram na busca.";
    }
}
