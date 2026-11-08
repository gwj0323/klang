using System;

namespace Klang_WinUI.Models;

public class TrackItem
{
    public int Index { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "Unknown Title";
    public string Artist { get; set; } = "Unknown Artist";
    public string Album { get; set; } = "Unknown Album";
    public double Duration { get; set; } = 0.0;
    public string DurationStr => FormatDuration(Duration);
    public string FilePath { get; set; } = string.Empty;
    public string? CoverPath { get; set; }
    public string FormatBadge { get; set; } = "FLAC";
    public int SampleRate { get; set; } = 44100;
    public int BitDepth { get; set; } = 16;
    public int Bitrate { get; set; } = 0;
    public bool IsFavorite { get; set; } = false;

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0) return "00:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
    }
}
