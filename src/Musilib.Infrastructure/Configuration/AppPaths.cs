using Musilib.Application.Abstractions;

namespace Musilib.Infrastructure.Configuration;

/// <summary>Emplacements des données : %LOCALAPPDATA%\Musilib (base, cache, logs) et %APPDATA%\Musilib (paramètres).</summary>
public sealed class AppPaths : IAppPaths
{
    public AppPaths(string? rootOverride = null)
    {
        var local = rootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Musilib");
        var roaming = rootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Musilib");
        DataDirectory = local;
        CacheDirectory = Path.Combine(local, "cache");
        LogDirectory = Path.Combine(local, "logs");
        SecretsDirectory = Path.Combine(local, "secrets");
        SettingsDirectory = roaming;
        foreach (var dir in new[] { DataDirectory, CacheDirectory, LogDirectory, SecretsDirectory, SettingsDirectory })
            Directory.CreateDirectory(dir);
    }

    public string DataDirectory { get; }
    public string CacheDirectory { get; }
    public string LogDirectory { get; }
    public string SecretsDirectory { get; }
    public string SettingsDirectory { get; }

    public string DatabasePath(bool demo) => Path.Combine(DataDirectory, demo ? "musilib-demo.db" : "musilib.db");
}
