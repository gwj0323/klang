using Klang_WinUI.Models;
using Klang_WinUI.Services.Netease;
using Klang_WinUI.Services.Soda;

namespace Klang_WinUI.Services;

/// <summary>Source-aware routing shared by play, lyrics, prefetch and crossfade.</summary>
internal static class OnlineMusicService
{
    public static bool IsLoggedIn(TrackItem track) => track.SodaId.Length > 0
        ? SodaService.Instance.IsLoggedIn : NeteaseService.Instance.IsLoggedIn;

    public static Task<bool> ResolveAsync(TrackItem track, CancellationToken ct) => track.SodaId.Length > 0
        ? SodaService.Instance.ResolveAsync(track, ct) : NeteaseService.Instance.ResolveAsync(track, ct);

    public static bool IsCached(TrackItem track) => track.SodaId.Length > 0
        ? SodaService.Instance.IsCached(track) : NeteaseService.Instance.IsCached(track);

    public static void Prefetch(TrackItem track)
    {
        if (track.SodaId.Length > 0) SodaService.Instance.Prefetch(track);
        else NeteaseService.Instance.Prefetch(track);
    }

    public static async Task<SodaLyrics> GetLyricsAsync(TrackItem track, CancellationToken ct)
    {
        // Lyrics run alongside audio resolution and may outlive a cancelled selection.
        // Optional lyric failures must neither stop playable audio nor leave an unobserved task.
        try
        {
            if (track.SodaId.Length > 0) return await SodaService.Instance.GetLyricsAsync(track.SodaId, ct);
            var bundle = await NeteaseService.Instance.GetLyricsAsync(track.NeteaseId, ct);
            return new SodaLyrics(bundle.Lrc, bundle.Trans, bundle.Roma);
        }
        catch { return new SodaLyrics(""); }
    }
}
