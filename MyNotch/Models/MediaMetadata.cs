namespace MyNotch.Models;

/// <summary>Pure provider-specific metadata fixes before display, track identity and lyrics lookup.</summary>
public static class MediaMetadata
{
    public static bool IsAppleMusic(string appId) => appId.Contains("applemusic", StringComparison.OrdinalIgnoreCase);

    public static (string Artist, string Album) ArtistAlbum(string appId, string artist, string album)
    {
        if (!IsAppleMusic(appId)) return (artist, album);
        // Apple Music Windows reports Artist — Album — Station in the artist field.
        var parts = artist.Split(" \u2014 ", StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0])) return (artist, album);
        return (parts[0], string.IsNullOrWhiteSpace(album) ? parts[1] : album);
    }

    public static string ProviderName(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return string.Empty;
        if (IsAppleMusic(appId)) return "Apple Music";
        if (appId.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (appId.Contains("tidal", StringComparison.OrdinalIgnoreCase)) return "TIDAL";
        if (appId.Contains("itunes", StringComparison.OrdinalIgnoreCase)) return "iTunes";
        if (appId.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge";
        var id = appId;
        int bang = id.LastIndexOf('!');
        if (bang >= 0)
        {
            var entry = id[(bang + 1)..];
            var package = id[..bang].Split('_')[0];
            int dot = package.LastIndexOf('.');
            if (dot >= 0) package = package[(dot + 1)..];
            id = entry.Equals("App", StringComparison.OrdinalIgnoreCase) || entry.Length == 0 ? package : entry;
        }
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id[..^4];
        return id.Length == 0 ? string.Empty : char.ToUpperInvariant(id[0]) + id[1..];
    }
}
