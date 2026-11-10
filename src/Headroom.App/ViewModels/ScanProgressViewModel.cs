using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Headroom.Core.Formatting;
using Headroom.Core.Scanning;
using Headroom.Core.Settings;

namespace Headroom.App.ViewModels;

/// <summary>Tela exibida durante o scan: contadores, pasta atual, velocidade e botão Cancelar.</summary>
public sealed partial class ScanProgressViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();

    public ScanProgressViewModel(string targetPath, int index = 1, int count = 1, SavedScanInfo? saved = null)
    {
        TargetPath = targetPath;
        Index = index;
        Count = count;
        Saved = saved;
    }

    public string TargetPath { get; }

    /// <summary>Posição deste disco na fila (1 a <see cref="Count"/>).</summary>
    public int Index { get; }

    public int Count { get; }

    /// <summary>Quando preenchido, abre a análise salva em vez de escanear.</summary>
    public SavedScanInfo? Saved { get; }

    public string Title => (Saved is null ? $"Escaneando {TargetPath}" : $"Abrindo a análise salva de {TargetPath}") +
                           (Count > 1 ? $" ({Index} de {Count})" : string.Empty);

    public string? SavedNote => Saved is null
        ? null
        : $"Análise feita em {Saved.ScannedAt:dd/MM/yyyy HH:mm}: {ByteSize.Format(Saved.TotalBytes)} em {ByteSize.FormatCount(Saved.FileCount)} arquivos. " +
          "Não é preciso escanear de novo; use \"Nova análise\" quando quiser atualizar.";

    [ObservableProperty]
    private string _filesText = "0";

    [ObservableProperty]
    private string _directoriesText = "0";

    [ObservableProperty]
    private string _bytesText = "0 B";

    [ObservableProperty]
    private string _speedText = "—";

    [ObservableProperty]
    private string _elapsedText = "0,0 s";

    [ObservableProperty]
    private string _currentPath = string.Empty;

    [ObservableProperty]
    private string _inaccessibleText = "0";

    [ObservableProperty]
    private bool _isCancelling;

    public Task<ScanResult> RunAsync(AppSettings settings)
    {
        var options = new ScanOptions
        {
            TopFilesCount = settings.TopFilesCount,
            ExcludedPaths = settings.ExcludedPaths,
        };

        var progress = new Progress<ScanProgress>(Update);
        return Saved is null
            ? new DiskScanner().ScanAsync(TargetPath, options, progress, _cts.Token)
            : ScanArchive.LoadAsync(Saved.FilePath, progress, _cts.Token);
    }

    private void Update(ScanProgress p)
    {
        FilesText = ByteSize.FormatCount(p.FilesScanned);
        DirectoriesText = ByteSize.FormatCount(p.DirectoriesScanned);
        BytesText = ByteSize.Format(p.BytesCounted);
        SpeedText = $"{ByteSize.FormatCount((long)p.FilesPerSecond)} arquivos/s · {ByteSize.Format((long)p.BytesPerSecond)}/s";
        ElapsedText = ByteSize.FormatDuration(p.Elapsed);
        CurrentPath = p.CurrentPath;
        InaccessibleText = ByteSize.FormatCount(p.InaccessibleCount);
    }

    [RelayCommand]
    private void Cancel()
    {
        IsCancelling = true;
        _cts.Cancel();
    }
}
