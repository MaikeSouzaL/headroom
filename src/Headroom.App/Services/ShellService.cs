using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace Headroom.App.Services;

/// <summary>Integração com o Explorer: abrir, copiar caminho, propriedades, reiniciar elevado.</summary>
public sealed class ShellService
{
    /// <summary>Abre o Explorer com o item selecionado (arquivo) ou dentro da pasta.</summary>
    public void OpenInExplorer(string path)
    {
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        else if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        else
        {
            string? parent = Path.GetDirectoryName(path);
            if (parent is not null && Directory.Exists(parent))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{parent}\"") { UseShellExecute = true });
            }
        }
    }

    public void CopyPath(string path)
    {
        // A área de transferência pode estar ocupada por outro processo: tenta algumas vezes.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(path);
                return;
            }
            catch (COMException)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Abre a janela "Propriedades" do Windows para o item.</summary>
    public void ShowProperties(string path)
    {
        if (!SHObjectProperties(IntPtr.Zero, ShopFilePath, path, null))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void OpenUrlOrFile(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    /// <summary>
    /// Reinicia o app pedindo elevação (UAC). Retorna false se o usuário recusou.
    /// </summary>
    public bool RestartAsAdministrator(string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Core.Settings.AppPaths.ExecutablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments ?? string.Empty,
            });
            Application.Current.Shutdown();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: o usuário clicou em "Não" no UAC.
            return false;
        }
    }

    private const uint ShopFilePath = 0x2;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint shopObjectType, string pszObjectName,
        string? pszPropertyPage);
}
