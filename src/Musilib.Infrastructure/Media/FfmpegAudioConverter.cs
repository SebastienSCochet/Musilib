using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Musilib.Application.Abstractions;

namespace Musilib.Infrastructure.Media;

/// <summary>
/// Conversion audio via un exécutable FFmpeg installé par l'utilisateur (non redistribué).
/// Utilisée uniquement si l'utilisateur choisit un format cible différent du format d'origine.
/// </summary>
public sealed class FfmpegAudioConverter : IAudioConverter
{
    private readonly ISettingsService _settings;
    private readonly ILogger<FfmpegAudioConverter> _logger;

    public FfmpegAudioConverter(ISettingsService settings, ILogger<FfmpegAudioConverter> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsAvailable => ResolveExecutable() is not null;

    public string? ResolveExecutable()
    {
        var configured = _settings.Current.Export.FfmpegPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in paths)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public static IReadOnlyList<string> BuildArguments(string source, string target, string format, int bitrateKbps)
    {
        var br = bitrateKbps.ToString(CultureInfo.InvariantCulture) + "k";
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", source, "-map", "0:a:0", "-map_metadata", "0" };
        switch (format.Trim('.').ToLowerInvariant())
        {
            case "mp3": args.AddRange(["-c:a", "libmp3lame", "-b:a", br, "-id3v2_version", "3"]); break;
            case "m4a":
            case "aac": args.AddRange(["-c:a", "aac", "-b:a", br, "-movflags", "+faststart"]); break;
            case "flac": args.AddRange(["-c:a", "flac"]); break;
            case "ogg": args.AddRange(["-c:a", "libvorbis", "-q:a", "6"]); break;
            case "opus": args.AddRange(["-c:a", "libopus", "-b:a", br]); break;
            case "wav": args.AddRange(["-c:a", "pcm_s16le"]); break;
            default: throw new NotSupportedException($"Format de conversion non pris en charge : {format}");
        }
        args.Add(target);
        return args;
    }

    public async Task ConvertAsync(string sourcePath, string targetPath, string targetFormat, int bitrateKbps, CancellationToken ct = default)
    {
        var exe = ResolveExecutable() ?? throw new InvalidOperationException("FFmpeg est introuvable. Indiquez son chemin dans les paramètres.");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in BuildArguments(sourcePath, targetPath, targetFormat, bitrateKbps)) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr)
            {
                stderr.AppendLine(e.Data);
                if (stderr.Length > 8000) stderr.Remove(0, stderr.Length - 8000);
            }
        };
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        if (process.ExitCode != 0)
        {
            string tail;
            lock (stderr) tail = stderr.ToString().Trim().Split('\n').LastOrDefault() ?? string.Empty;
            _logger.LogWarning("FFmpeg a échoué (code {Code}) : {Error}", process.ExitCode, tail);
            throw new IOException($"La conversion a échoué : {tail}");
        }
    }
}
