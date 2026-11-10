using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Headroom.Core.Scanning;

namespace Headroom.App.Services;

/// <summary>
/// Estado compartilhado: os resultados do último scan (um por disco escaneado) e o resultado
/// exibido no momento, visto por todas as telas de resultado.
/// </summary>
public sealed partial class ScanSession : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private ScanResult? _result;

    public bool HasResult => Result is not null;

    /// <summary>Todos os resultados do último scan (vários quando mais de um disco foi marcado).</summary>
    public ObservableCollection<ScanResult> Results { get; } = new();

    public bool HasMultipleResults => Results.Count > 1;

    /// <summary>Disparado quando um novo resultado chega, quando outro disco é exibido ou quando itens são apagados.</summary>
    public event EventHandler? ResultChanged;

    /// <summary>Troca todos os resultados e exibe o primeiro.</summary>
    public void SetResults(IReadOnlyList<ScanResult> results)
    {
        Results.Clear();
        foreach (var result in results)
        {
            Results.Add(result);
        }

        OnPropertyChanged(nameof(HasMultipleResults));
        Result = results.Count > 0 ? results[0] : null;
    }

    /// <summary>Troca um resultado por uma análise nova do mesmo disco (os outros discos continuam).</summary>
    public void Replace(ScanResult old, ScanResult fresh)
    {
        int index = Results.IndexOf(old);
        if (index < 0)
        {
            SetResults(new[] { fresh });
            return;
        }

        Results[index] = fresh;
        Result = fresh;
    }

    partial void OnResultChanged(ScanResult? oldValue, ScanResult? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.Changed -= OnInnerChanged;
        }

        if (newValue is not null)
        {
            newValue.Changed += OnInnerChanged;
        }

        ResultChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnInnerChanged(object? sender, EventArgs e) => ResultChanged?.Invoke(this, EventArgs.Empty);
}
