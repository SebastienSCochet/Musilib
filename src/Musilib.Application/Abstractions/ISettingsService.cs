using Musilib.Application.Settings;

namespace Musilib.Application.Abstractions;

public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler<AppSettings>? Changed;
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
    string SettingsPath { get; }
}

/// <summary>Chemins applicatifs (données, cache, logs).</summary>
public interface IAppPaths
{
    string DataDirectory { get; }
    string CacheDirectory { get; }
    string LogDirectory { get; }
}
