using System.Text;

namespace Musilib.Infrastructure.Demo;

/// <summary>Génère de courts fichiers WAV synthétiques (mélodies sinusoïdales) pour le mode démonstration.</summary>
public static class DemoAudio
{
    private static readonly double[][] Melodies =
    [
        [261.63, 329.63, 392.00, 523.25, 392.00, 329.63],
        [293.66, 349.23, 440.00, 349.23, 293.66, 220.00],
        [329.63, 392.00, 493.88, 587.33, 493.88, 392.00],
        [220.00, 261.63, 329.63, 261.63, 196.00, 246.94],
        [349.23, 440.00, 523.25, 440.00, 349.23, 392.00],
        [196.00, 246.94, 293.66, 392.00, 293.66, 246.94]
    ];

    public const int SampleRate = 22050;

    /// <summary>Écrit un WAV PCM 16 bits mono.</summary>
    public static void WriteWav(string path, double seconds, int seed)
    {
        var melody = Melodies[Math.Abs(seed) % Melodies.Length];
        var samples = (int)(SampleRate * seconds);
        var noteLength = Math.Max(1, samples / (melody.Length * 2));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(stream, Encoding.ASCII);
        var dataSize = samples * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataSize);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(SampleRate);
        w.Write(SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataSize);
        for (var i = 0; i < samples; i++)
        {
            var note = melody[(i / noteLength) % melody.Length];
            var posInNote = (i % noteLength) / (double)noteLength;
            var envelope = Math.Min(1, posInNote * 20) * Math.Pow(1 - posInNote, 1.5);
            var t = i / (double)SampleRate;
            var value = (Math.Sin(2 * Math.PI * note * t) * 0.6 + Math.Sin(2 * Math.PI * note * 2 * t) * 0.15) * envelope;
            var fade = Math.Min(1, Math.Min(i, samples - i) / (SampleRate * 0.05));
            w.Write((short)(value * fade * short.MaxValue * 0.5));
        }
    }

    /// <summary>Renvoie (et crée si besoin) un extrait de démonstration.</summary>
    public static string EnsurePreview(string cacheDirectory, int index)
    {
        var path = Path.Combine(cacheDirectory, "demo", $"preview-{Math.Abs(index) % Melodies.Length}.wav");
        if (!File.Exists(path)) WriteWav(path, 8, index);
        return path;
    }
}
