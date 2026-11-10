using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace Headroom.App.Services;

/// <summary>Resultado de uma pergunta com até três botões.</summary>
public enum DialogChoice
{
    Cancel,
    Primary,
    Secondary,
}

/// <summary>Diálogos do app (confirmações, avisos e seletores de arquivo/pasta).</summary>
public sealed class DialogService
{
    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } w ? w : null;

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "Sim",
        string cancelText = "Cancelar", bool danger = false)
    {
        var choice = await ChooseAsync(title, message, confirmText, null, cancelText, danger);
        return choice == DialogChoice.Primary;
    }

    public async Task<DialogChoice> ChooseAsync(string title, string message, string primaryText,
        string? secondaryText, string closeText = "Cancelar", bool danger = false)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 520,
            },
            PrimaryButtonText = primaryText,
            PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary,
            CloseButtonText = closeText,
            Owner = Owner,
            WindowStartupLocation = Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };

        if (!string.IsNullOrEmpty(secondaryText))
        {
            box.SecondaryButtonText = secondaryText;
            box.IsSecondaryButtonEnabled = true;
        }
        else
        {
            box.IsSecondaryButtonEnabled = false;
        }

        var result = await box.ShowDialogAsync();
        return result switch
        {
            MessageBoxResult.Primary => DialogChoice.Primary,
            MessageBoxResult.Secondary => DialogChoice.Secondary,
            _ => DialogChoice.Cancel,
        };
    }

    public async Task InfoAsync(string title, string message)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            CloseButtonText = "OK",
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = false,
            Owner = Owner,
            WindowStartupLocation = Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        await box.ShowDialogAsync();
    }

    /// <summary>Mostra um erro com mensagem amigável em português.</summary>
    public Task ErrorAsync(string title, Exception ex) => InfoAsync(title, FriendlyMessage(ex));

    public static string FriendlyMessage(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Sem permissão para acessar este item. Tente reiniciar o Headroom como administrador.",
        PathTooLongException => "O caminho é longo demais para esta operação.",
        DirectoryNotFoundException => ex.Message.Contains("não existe", StringComparison.Ordinal)
            ? ex.Message
            : "A pasta não foi encontrada. Ela pode ter sido movida ou apagada.",
        FileNotFoundException => "O arquivo não foi encontrado. Ele pode ter sido movido ou apagado.",
        IOException io when io.HResult == unchecked((int)0x80070020) =>
            "O arquivo está em uso por outro programa. Feche o programa e tente de novo.",
        IOException => "Erro de leitura/gravação no disco: " + ex.Message,
        OperationCanceledException => "A operação foi cancelada.",
        InvalidOperationException => ex.Message,
        _ => "Ocorreu um erro inesperado: " + ex.Message,
    };

    public string? PickFolder(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public string? SaveFile(string title, string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName,
            AddExtension = true,
            OverwritePrompt = true,
        };

        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }
}
