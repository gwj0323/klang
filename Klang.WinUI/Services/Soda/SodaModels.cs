namespace Klang_WinUI.Services.Soda;

public sealed record SodaDeviceInfo(string DeviceId, string InstallId, string VersionCode);

/// <summary>IDs for scene modes come from the server; preference modes use their string key.</summary>
public sealed record SodaMode(string Key, string Name, string Preference = "", int? SceneId = null);

public sealed record SodaTrackPage(List<Models.TrackItem> Tracks, string Cursor, bool HasMore);
public sealed record SodaLyrics(string Lrc, string Trans = "", string Roma = "");
