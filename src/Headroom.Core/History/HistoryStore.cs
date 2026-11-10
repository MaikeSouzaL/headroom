using System.Text.Json;
using System.Text.Json.Serialization;
using Headroom.Core.Scanning;
using Headroom.Core.Settings;

namespace Headroom.Core.History;

/// <summary>Resumo de um scan guardado no histórico.</summary>
public sealed record ScanHistoryEntry(
    DateTime Date,
    string RootPath,
    long TotalBytes,
    long FileCount,
    long DirectoryCount,
    long? DriveTotalBytes,
    long? DriveFreeBytes,
    double ElapsedSeconds);

/// <summary>Resumo de uma limpeza guardado no histórico.</summary>
public sealed record CleanupHistoryEntry(
    DateTime Date,
    long BytesFreed,
    long BytesToRecycleBin,
    int RulesRun,
    int Skipped,
    int Errors,
    string? LogPath,
    string Origin);

/// <summary>Instantâneo do espaço livre de um disco (gravado em scans, limpezas e verificações agendadas).</summary>
public sealed record FreeSpaceSample(DateTime Date, string Drive, long TotalBytes, long FreeBytes);

public sealed class HistoryData
{
    public List<ScanHistoryEntry> Scans { get; set; } = new();
    public List<CleanupHistoryEntry> Cleanups { get; set; } = new();
    public List<FreeSpaceSample> FreeSpace { get; set; } = new();
}

/// <summary>Histórico em JSON (%LOCALAPPDATA%\Headroom\historico.json), com limite de tamanho.</summary>
public sealed class HistoryStore
{
    private const int MaxEntries = 2000;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _path;

    public HistoryStore(string? path = null)
    {
        _path = path ?? AppPaths.HistoryFile;
    }

    public HistoryData Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<HistoryData>(File.ReadAllText(_path), Options) ?? new HistoryData();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new HistoryData();
    }

    public void AddScan(ScanResult result)
    {
        var entry = new ScanHistoryEntry(result.StartedAt, result.RootPath, result.TotalBytes, result.FileCount,
            result.DirectoryCount, result.Drive?.TotalBytes, result.Drive?.FreeBytes, result.Elapsed.TotalSeconds);
        Update(data =>
        {
            data.Scans.Add(entry);
            AddSamples(data);
        });
    }

    public void AddCleanup(CleanupHistoryEntry entry) => Update(data =>
    {
        data.Cleanups.Add(entry);
        AddSamples(data);
    });

    /// <summary>Grava o espaço livre atual de todos os discos fixos (usado pelo gráfico de evolução).</summary>
    public void RecordFreeSpace() => Update(AddSamples);

    private static void AddSamples(HistoryData data)
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            try
            {
                data.FreeSpace.Add(new FreeSpaceSample(DateTime.Now, drive.Name, drive.TotalSize, drive.TotalFreeSpace));
            }
            catch (IOException)
            {
            }
        }
    }

    private void Update(Action<HistoryData> change)
    {
        Gate.Wait();
        try
        {
            var data = Load();
            change(data);
            Trim(data.Scans);
            Trim(data.Cleanups);
            Trim(data.FreeSpace);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, Options));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // O histórico é acessório: nunca deve interromper o app.
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void Trim<T>(List<T> list)
    {
        if (list.Count > MaxEntries)
        {
            list.RemoveRange(0, list.Count - MaxEntries);
        }
    }
}
