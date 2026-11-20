using System.Text.Json;

namespace MyNotch.Models;

public enum UpdateStatusKind { Idle, Checking, UpToDate, Available, Downloading, Installing, Error }

public sealed record AppUpdateRelease(
    string Tag,
    Version Version,
    string InstallerUrl,
    string InstallerName,
    string? ChecksumsUrl,
    string ReleasePageUrl);

public sealed record UpdateState(
    UpdateStatusKind Kind,
    string CurrentVersion,
    AppUpdateRelease? Release = null,
    double? ProgressPercent = null,
    string? Message = null);

/// <summary>Pure parsing and validation helpers for GitHub Releases updates (AOT-safe).</summary>
public static class AppUpdateParser
{
    public static string NormalizeDisplayVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "dev";
        string cleaned = raw.Trim();
        int plus = cleaned.IndexOf('+');
        if (plus >= 0) cleaned = cleaned[..plus];
        if (cleaned.Length == 0) return "dev";
        if (char.IsDigit(cleaned[0])) return "v" + cleaned;
        return cleaned;
    }

    public static bool TryParseSemVer(string? raw, out Version version, out string? prerelease)
    {
        version = new Version(0, 0, 0);
        prerelease = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];

        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = s[(dash + 1)..];
            s = s[..dash];
            if (prerelease.Length == 0) prerelease = null;
        }

        string[] parts = s.Split('.');
        if (parts.Length is < 2 or > 4) return false;
        if (!int.TryParse(parts[0], out int major) || major < 0) return false;
        if (!int.TryParse(parts[1], out int minor) || minor < 0) return false;
        int patch = 0;
        if (parts.Length >= 3 && (!int.TryParse(parts[2], out patch) || patch < 0)) return false;

        version = new Version(major, minor, patch);
        return true;
    }

    public static bool IsNewer(string? candidateTag, string? currentVersion)
    {
        if (!TryParseSemVer(candidateTag, out var candVer, out var candPre)) return false;
        if (!TryParseSemVer(currentVersion, out var curVer, out var curPre)) return candVer > new Version(0, 0, 0);

        int cmp = candVer.CompareTo(curVer);
        if (cmp != 0) return cmp > 0;
        if (curPre is not null && candPre is null) return true;
        if (curPre is null && candPre is not null) return false;
        if (curPre is not null && candPre is not null)
            return string.Compare(candPre, curPre, StringComparison.OrdinalIgnoreCase) > 0;
        return false;
    }

    public static bool IsTrustedAssetUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        string host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    public static (AppUpdateRelease? Release, bool IsUpToDate, string? Error) ParseGitHubRelease(string json, string currentVersion)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return (null, false, "Unexpected release payload.");

        if (root.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True)
            return (null, true, null);

        string? tag = root.TryGetProperty("tag_name", out var tagProp) && tagProp.ValueKind == JsonValueKind.String ? tagProp.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(tag) || !TryParseSemVer(tag, out var version, out _))
            return (null, false, "Release tag could not be parsed.");

        if (!IsNewer(tag, currentVersion))
            return (null, true, null);

        string releasePage = root.TryGetProperty("html_url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String && IsTrustedAssetUrl(urlProp.GetString())
            ? urlProp.GetString()!
            : "https://github.com/brendatamaar/my-notch/releases";

        string? installerUrl = null;
        string? installerName = null;
        string? checksumsUrl = null;

        if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsProp.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) continue;
                string? name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                string? download = asset.TryGetProperty("browser_download_url", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || !IsTrustedAssetUrl(download)) continue;

                if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    checksumsUrl = download;
                }
                else if (name.StartsWith("MyNotch-Setup-", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    // Prefer explicit x64 installer when multiple architectures exist.
                    if (installerUrl is null || name.Contains("x64", StringComparison.OrdinalIgnoreCase))
                    {
                        installerUrl = download;
                        installerName = name;
                    }
                }
            }
        }

        if (installerUrl is null || installerName is null)
            return (null, false, $"Release {tag} has no Windows Setup installer.");

        return (new AppUpdateRelease(tag, version, installerUrl, installerName, checksumsUrl, releasePage), false, null);
    }

    public static string? ParseExpectedSha256(string checksumsText, string fileName)
    {
        if (string.IsNullOrWhiteSpace(checksumsText) || string.IsNullOrWhiteSpace(fileName)) return null;
        foreach (string rawLine in checksumsText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length < 66) continue;
            int sep = line.IndexOfAny([' ', '\t']);
            if (sep != 64) continue;
            string hash = line[..64];
            if (!IsHex64(hash)) continue;
            string candidateName = line[(sep + 1)..].Trim().TrimStart('*');
            if (candidateName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return hash.ToLowerInvariant();
        }
        return null;
    }

    private static bool IsHex64(string value)
    {
        if (value.Length != 64) return false;
        foreach (char c in value)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
        return true;
    }
}
