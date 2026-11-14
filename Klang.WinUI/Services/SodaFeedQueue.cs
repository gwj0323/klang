using Klang_WinUI.Models;
using Klang_WinUI.Services.Soda;

namespace Klang_WinUI.Services;

/// <summary>A channel owns its batch counter, played history and deduplication state.</summary>
internal sealed class SodaFeedQueue
{
    private readonly Func<SodaMode, IReadOnlyList<string>, int, CancellationToken, Task<List<TrackItem>>> _fetch;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<string> _played = new();
    private int _batches;
    private int _emptyBatches;
    public SodaMode Mode { get; }
    public List<TrackItem> Tracks { get; } = new();
    public bool HasMore => _emptyBatches < 2;

    public SodaFeedQueue(SodaMode mode,
        Func<SodaMode, IReadOnlyList<string>, int, CancellationToken, Task<List<TrackItem>>> fetch)
    { Mode = mode; _fetch = fetch; }

    public void MarkPlayed(TrackItem track)
    {
        if (track.SodaId.Length == 0) return;
        _played.Remove(track.SodaId);
        _played.Add(track.SodaId);
        if (_played.Count > 200) _played.RemoveAt(0);
    }

    public async Task<bool> LoadMoreAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!HasMore) return false;
            var batch = await _fetch(Mode, _played.ToArray(), _batches + 1, ct);
            ct.ThrowIfCancellationRequested();
            _batches++;
            int added = 0;
            foreach (var track in batch)
            {
                if (track.SodaId.Length == 0 || !_seen.Add(track.SodaId)) continue;
                Tracks.Add(track); added++;
            }
            _emptyBatches = added == 0 ? _emptyBatches + 1 : 0;
            return added > 0;
        }
        finally { _gate.Release(); }
    }
}
