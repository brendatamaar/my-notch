using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MyNotch.Models;

namespace MyNotch.Services;

/// <summary>
/// Checks GitHub Releases for newer MyNotch builds, verifies the SHA-256 checksum,
/// and launches the Inno Setup installer silently to upgrade and relaunch the app.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/brendatamaar/my-notch/releases/latest";
    private const long MaxInstallerBytes = 250L * 1024 * 1024;
    private static readonly TimeSpan AutoCheckCooldown = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly string _currentVersionRaw;
    private readonly string _appDirectory;
    private readonly string _downloadRoot;
    private readonly object _gate = new();

    private DateTimeOffset _lastChecked = DateTimeOffset.MinValue;
    private bool _busy;
    private bool _disposed;

    public UpdateState State { get; private set; }
    public event EventHandler? Changed;

    public string CurrentVersionDisplay => AppUpdateParser.NormalizeDisplayVersion(_currentVersionRaw);
    public bool IsInstallerManaged => File.Exists(Path.Combine(_appDirectory, "unins000.exe"));

    public UpdateService(HttpMessageHandler? handler = null, string? currentVersion = null, string? appDirectory = null, string? downloadRoot = null)
    {
        _currentVersionRaw = currentVersion ?? ReadAssemblyVersion();
        _appDirectory = appDirectory ?? AppContext.BaseDirectory;
        _downloadRoot = downloadRoot ?? Path.Combine(Path.GetTempPath(), "MyNotch-Update");
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = TimeSpan.FromMinutes(5);
        string uaVer = CurrentVersionDisplay.TrimStart('v');
        if (!ProductInfoHeaderValue.TryParse($"MyNotch/{uaVer}", out var ua))
            ua = new ProductInfoHeaderValue("MyNotch", "1.0");
        _http.DefaultRequestHeaders.UserAgent.Add(ua);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        State = new UpdateState(UpdateStatusKind.Idle, CurrentVersionDisplay);
    }

    public async Task<UpdateState> CheckAsync(bool force = false, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed || _busy) return State;
            if (!force && DateTimeOffset.UtcNow - _lastChecked < AutoCheckCooldown) return State;
            _busy = true;
        }

        SetState(new UpdateState(UpdateStatusKind.Checking, CurrentVersionDisplay, State.Release, Message: "Checking for updates..."));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            using var response = await _http.GetAsync(LatestReleaseApiUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = (int)response.StatusCode == 403 ? "GitHub API rate limit reached. Try again later." : $"Update check failed (HTTP {(int)response.StatusCode}).";
                return SetState(new UpdateState(UpdateStatusKind.Error, CurrentVersionDisplay, State.Release, Message: detail));
            }

            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var (release, isUpToDate, parseError) = AppUpdateParser.ParseGitHubRelease(body, _currentVersionRaw);

            lock (_gate) _lastChecked = DateTimeOffset.UtcNow;

            if (release is not null)
                return SetState(new UpdateState(UpdateStatusKind.Available, CurrentVersionDisplay, release, Message: $"Update {release.Tag} is available."));

            if (isUpToDate)
                return SetState(new UpdateState(UpdateStatusKind.UpToDate, CurrentVersionDisplay, Message: $"You're up to date ({CurrentVersionDisplay})."));

            return SetState(new UpdateState(UpdateStatusKind.Error, CurrentVersionDisplay, Message: parseError ?? "Could not read release information."));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SetState(new UpdateState(UpdateStatusKind.Error, CurrentVersionDisplay, State.Release, Message: "Update check timed out."));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return SetState(new UpdateState(UpdateStatusKind.Error, CurrentVersionDisplay, State.Release, Message: "Couldn't reach GitHub Releases."));
        }
        finally
        {
            lock (_gate) _busy = false;
        }
    }

    public async Task<string?> DownloadAndVerifyAsync(AppUpdateRelease release, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed || _busy) return null;
            _busy = true;
        }

        string safeName = Path.GetFileName(release.InstallerName);
        if (string.IsNullOrWhiteSpace(safeName) || !safeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate) _busy = false;
            SetState(new UpdateState(UpdateStatusKind.Error, CurrentVersionDisplay, release, Message: "Invalid installer filename."));
            return null;
        }

        string targetPath = Path.Combine(_downloadRoot, safeName);
        try
        {
            Directory.CreateDirectory(_downloadRoot);
            CleanOldDownloads(targetPath);

            SetState(new UpdateState(UpdateStatusKind.Downloading, CurrentVersionDisplay, release, 0, $"Downloading {release.Tag}..."));

            string? expectedSha256 = null;
            if (release.ChecksumsUrl is { Length: > 0 } sumsUrl)
            {
                using var sumsResponse = await _http.GetAsync(sumsUrl, ct).ConfigureAwait(false);
                if (!sumsResponse.IsSuccessStatusCode)
                {
                    SetState(new UpdateState(UpdateStatusKind.Error, CurrentVersionDisplay, release, Message: "Couldn't download SHA256SUMS.txt to verify update."));
                    return null;
                }
                string sumsText = await sumsResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                expectedSha256 = AppUpdateParser.ParseExpectedSha256(sumsText, safeName);
                if (expectedSha256 is null)
                {
                    SetState(new UpdateState(UpdateStatusKind.Error, CurrentVersionDisplay, release, Message: "Installer checksum missing from SHA256SUMS.txt."));
                    return null;
                }
            }

            using var response = await _http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                SetState(new UpdateState(UpdateStatusKind.Error, CurrentVersionDisplay, release, Message: $"Download failed (HTTP {(int)response.StatusCode})."));
                return null;
            }

            long? totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes is > MaxInstallerBytes)
            {
                SetState(new UpdateState(UpdateStatusKind.Error, CurrentVersionDisplay, release, Message: "Installer exceeds expected size limit."));
                return null;
            }

            string tempPath = targetPath + ".part";
            long downloaded = 0;
            int lastPercent = -1;

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    downloaded += read;
                    if (downloaded > MaxInstallerBytes)
                        throw new InvalidDataException("Installer download exceeded size limit.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    if (totalBytes is > 0)
                    {
                        int pct = (int)Math.Clamp(downloaded * 100L / totalBytes.Value, 0, 100);
                        if (pct >= lastPercent + 2 || pct == 100)
                        {
                            lastPercent = pct;
                            SetState(new UpdateState(UpdateStatusKind.Downloading, CurrentVersionDisplay, release, pct, $"Downloading {release.Tag}... {pct}%"));
                        }
                    }
                }
            }

            if (expectedSha256 is not null)
            {
                string actualSha256;
                await using (var verifyStream = File.OpenRead(tempPath))
                {
                    byte[] hash = await SHA256.HashDataAsync(verifyStream, ct).ConfigureAwait(false);
                    actualSha256 = Convert.ToHexString(hash).ToLowerInvariant();
                }

                if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(tempPath);
                    SetState(new UpdateState(UpdateStatusKind.Error, CurrentVersionDisplay, release, Message: "Download verification failed (SHA-256 mismatch)."));
                    return null;
                }
            }

            File.Move(tempPath, targetPath, overwrite: true);
            return targetPath;
        }
        catch (OperationCanceledException)
        {
            TryDelete(targetPath + ".part");
            SetState(new UpdateState(UpdateStatusKind.Available, CurrentVersionDisplay, release, Message: $"Update {release.Tag} is available."));
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(targetPath + ".part");
            SetState(new UpdateState(UpdateStatusKind.Error, CurrentVersionDisplay, release, Message: "Download failed: " + ex.Message));
            return null;
        }
        finally
        {
            lock (_gate) _busy = false;
        }
    }

    public async Task<bool> InstallAndExitAsync(Action requestAppClose, CancellationToken ct = default)
    {
        if (State.Kind is UpdateStatusKind.Downloading or UpdateStatusKind.Installing) return false;
        var release = State.Release;
        if (release is null) return false;

        string? installerPath = await DownloadAndVerifyAsync(release, ct).ConfigureAwait(false);
        if (installerPath is null) return false;

        lock (_gate)
        {
            if (_disposed || _busy) return false;
            _busy = true;
        }
        try
        {
            SetState(new UpdateState(UpdateStatusKind.Installing, CurrentVersionDisplay, release, 100, $"Installing {release.Tag}..."));

            var psi = new ProcessStartInfo(installerPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            };
            psi.ArgumentList.Add(IsInstallerManaged ? "/VERYSILENT" : "/SILENT");
            psi.ArgumentList.Add("/SUPPRESSMSGBOXES");
            psi.ArgumentList.Add("/NORESTART");
            psi.ArgumentList.Add("/CLOSEAPPLICATIONS");
            psi.ArgumentList.Add("/LAUNCH=1");

            if (Process.Start(psi) is null)
            {
                SetState(new UpdateState(UpdateStatusKind.Error, CurrentVersionDisplay, release, Message: "Could not start the installer."));
                return false;
            }

            await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
            requestAppClose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            SetState(new UpdateState(UpdateStatusKind.Error, CurrentVersionDisplay, release, Message: "Failed to launch installer: " + ex.Message));
            return false;
        }
        finally
        {
            lock (_gate) _busy = false;
        }
    }

    private UpdateState SetState(UpdateState next)
    {
        State = next;
        Changed?.Invoke(this, EventArgs.Empty);
        return next;
    }

    private void CleanOldDownloads(string keepPath)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_downloadRoot))
            {
                if (!file.Equals(keepPath, StringComparison.OrdinalIgnoreCase))
                    TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string ReadAssemblyVersion()
    {
        var asm = typeof(UpdateService).Assembly;
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) return info;
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _http.Dispose();
    }
}
