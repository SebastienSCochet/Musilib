using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Musilib.Application.Abstractions;
using Musilib.Desktop.Services;
using Musilib.Desktop.ViewModels;
using Musilib.Desktop.Views;

namespace Musilib.Desktop;

public partial class App : Avalonia.Application
{
    private static bool _restartRequested;

    public static IServiceProvider? Services { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Services = AppHost.Build(desktop.Args ?? Array.Empty<string>(), out var demo);
            ApplyTheme(Services.GetRequiredService<ISettingsService>().Current.General.Theme);

            var vm = Services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm };
            if (demo) window.Title += " — Mode démonstration";
            Services.GetRequiredService<DialogService>().Owner = window;
            desktop.MainWindow = window;

            var closed = false;
            window.Closing += async (_, e) =>
            {
                if (closed) return;
                // Arrêt propre : les traitements en cours sont sauvegardés pour reprise au prochain démarrage.
                e.Cancel = true;
                closed = true;
                Services.GetRequiredService<PlayerViewModel>().Dispose();
                await vm.ShutdownAsync();
                if (_restartRequested) MainWindowViewModel.Restart();
                window.Close();
            };

            window.Opened += async (_, _) => await vm.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(string? theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase) ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    public static void RequestRestart()
    {
        _restartRequested = true;
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow?.Close();
    }
}
