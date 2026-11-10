using System.Windows;
using System.Windows.Media;
using Headroom.Core.Settings;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Headroom.App.Services;

/// <summary>Tema claro/escuro (ou seguindo o Windows) e fundo Mica quando disponível.</summary>
public sealed class ThemeService
{
    /// <summary>
    /// Cor de destaque própria do app. A cor de destaque do Windows pode ser muito escura (ex.: vinho)
    /// e deixar botões primários sem contraste; com uma cor fixa o visual fica legível em qualquer PC.
    /// </summary>
    private static readonly Color BrandAccent = Color.FromRgb(0x0F, 0x6C, 0xBD);

    private Window? _window;
    private bool _watching;
    private bool _applyingAccent;

    public ThemeService()
    {
        ApplicationThemeManager.Changed += (theme, _) =>
        {
            // Quando o Windows troca de tema (modo "Sistema"), o WPF-UI reaplica o destaque do sistema.
            ApplyBrandAccent(theme);
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? ThemeChanged;

    public bool IsDark => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    /// <summary>Mica só existe no Windows 11 (build 22000+); no Windows 10 o fundo é sólido.</summary>
    public static WindowBackdropType Backdrop =>
        Environment.OSVersion.Version.Build >= 22000 ? WindowBackdropType.Mica : WindowBackdropType.None;

    public void Attach(Window window) => _window = window;

    public void Apply(AppTheme theme)
    {
        ApplicationTheme target;
        if (theme == AppTheme.System)
        {
            target = SystemIsDark() ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(target, Backdrop, false);
            if (_window is not null && !_watching)
            {
                SystemThemeWatcher.Watch(_window, Backdrop, false);
                _watching = true;
            }
        }
        else
        {
            if (_window is not null && _watching)
            {
                SystemThemeWatcher.UnWatch(_window);
                _watching = false;
            }

            target = theme == AppTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(target, Backdrop, false);
        }

        ApplyBrandAccent(target);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyBrandAccent(ApplicationTheme theme)
    {
        if (_applyingAccent)
        {
            return;
        }

        try
        {
            _applyingAccent = true;
            ApplicationAccentColorManager.Apply(BrandAccent, theme, false);
        }
        finally
        {
            _applyingAccent = false;
        }
    }

    private static bool SystemIsDark() => ApplicationThemeManager.GetSystemTheme() switch
    {
        SystemTheme.Dark or SystemTheme.HCBlack or SystemTheme.Glow or SystemTheme.CapturedMotion => true,
        _ => false,
    };
}
