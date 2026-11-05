using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyNotch.Models;
using MyNotch.Services;
using MyNotch.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace MyNotch;

/// <summary>Dev tab extras: open/restart servers, Docker containers, repositories with CI status, clipboard history.</summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan DevExtrasMinGap = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CiPollEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CiWatchLimit = TimeSpan.FromMinutes(45);
    private const int MaxRepos = 4;

    private readonly GitStatusService _git = new();
    private DispatcherQueueTimer _ciTimer = null!;
    private readonly Dictionary<string, (CiRun Run, DateTimeOffset Since)> _watchedRuns = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _devExtrasAt = DateTimeOffset.MinValue;
    private bool _devExtrasLoading;

    private static readonly SolidColorBrush CiRunning = new(Windows.UI.Color.FromArgb(255, 255, 202, 22));
    private static readonly SolidColorBrush CiPassed = new(Windows.UI.Color.FromArgb(255, 48, 209, 88));
    private static readonly SolidColorBrush CiFailed = new(Windows.UI.Color.FromArgb(255, 229, 72, 77));
    private static readonly SolidColorBrush CiOther = new(Windows.UI.Color.FromArgb(255, 142, 142, 147));

    private void InitializeDevExtras()
    {
        _ciTimer = _dispatcher.CreateTimer();
        _ciTimer.IsRepeating = false;
        _ciTimer.Tick += (_, _) => _ = PollWatchedRunsAsync();
    }

    // ---------------------------------------------------------------- clipboard history

    private void RecordClip(string text)
    {
        if (_settings.Current.ClipboardHistory) DevTools.AddClip(text);
    }

    private void CopyClip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string text }) return;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[MyNotch] Clip copy failed: {ex.Message}");
        }
    }

    private void ClearClips_Click(object sender, RoutedEventArgs e) { DevTools.ClearClips(); UpdateBadges(); }

    // ---------------------------------------------------------------- open / restart / terminal

    private async void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private async void RestartServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string token } button) return;
        var selected = DevTools.Ports.FirstOrDefault(p => p.StopToken == token);
        if (selected is null) return;
        button.IsEnabled = false;
        var (ok, error) = await Task.Run(() => (_ports.TryRestart(selected, out var err), err));
        if (ok) DevTools.RemovePid(selected.Pid);
        else DevTools.SetPortsError($"Restart {selected.Pid} failed: {error}");
        button.IsEnabled = true;
        // The relaunched server takes a moment to bind its port again.
        if (ok) _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ => _dispatcher.TryEnqueue(() => _ = RefreshPortsAsync()), TaskScheduler.Default);
    }

    private void OpenRepoTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } || !Directory.Exists(path)) return;
        var command = new SavedCommand { Name = Path.GetFileName(path), Kind = "terminal", Target = path };
        if (!_commands.TryExecute(command, out var error)) DevTools.SetPortsError(error);
    }

    // ---------------------------------------------------------------- containers + repositories

    /// <summary>Docker and git reads spawn processes, so they run only while the Dev tab is visible and at most every 20 s.</summary>
    private async Task RefreshDevExtrasAsync(bool force = false)
    {
        if (_devExtrasLoading || (!force && DateTimeOffset.Now - _devExtrasAt < DevExtrasMinGap)) return;
        _devExtrasLoading = true;
        _devExtrasAt = DateTimeOffset.Now;
        try
        {
            var roots = RepoRoots();
            var dockerTask = DockerService.ListAsync();
            var repoTasks = roots.Select(root => _git.ReadAsync(root, force)).ToArray();
            var docker = await dockerTask;
            var repos = await Task.WhenAll(repoTasks);

            DevTools.SetContainers(docker.Installed, docker.Containers.Select(c => new DockerContainerItem(
                c.Id, c.Name, c.Image, c.PortsText, c.HostPorts.Count > 0 ? $"http://localhost:{c.HostPorts[0]}/" : "", c.State)).ToArray(), docker.Status);
            DevTools.SetRepos(repos.Where(r => r.Git is not null).Select(ToItem).ToArray());
            foreach (var repo in repos) WatchRun(repo);
        }
        finally { _devExtrasLoading = false; }
    }

    /// <summary>Repositories behind the detected dev servers and the agent sessions, newest activity first.</summary>
    private List<string> RepoRoots()
    {
        var candidates = _sessions.Select(s => s.Latest.Cwd).Concat(DevTools.Ports.Select(p => p.ProjectPath));
        var roots = new List<string>();
        foreach (var path in candidates)
        {
            if (GitStatusService.FindRoot(path) is { } root && !roots.Contains(root, StringComparer.OrdinalIgnoreCase)) roots.Add(root);
            if (roots.Count == MaxRepos) break;
        }
        return roots;
    }

    private static RepoItem ToItem(RepoSnapshot repo)
    {
        var git = repo.Git!;
        string sync = (git.Ahead, git.Behind) switch
        {
            (0, 0) => git.Upstream.Length > 0 ? "up to date" : "no upstream",
            var (a, b) => string.Join(" ", new[] { a > 0 ? $"↑{a}" : "", b > 0 ? $"↓{b}" : "" }.Where(s => s.Length > 0)),
        };
        string changes = git.Changes == 0 ? "clean" : $"{git.Changes} changed";
        var (text, brush) = repo.Ci switch
        {
            null => ("", CiOther),
            { InProgress: true } => ("CI running", CiRunning),
            { Passed: true } => ("CI passed", CiPassed),
            { Failed: true } => ("CI failed", CiFailed),
            var run => ("CI " + (run.Conclusion.Length > 0 ? run.Conclusion : run.Status), CiOther),
        };
        return new RepoItem(Path.GetFileName(repo.Root), repo.Root, git.Branch, sync, changes, text, repo.Ci?.Url ?? "", brush);
    }

    private async void StopContainer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } button) return;
        button.IsEnabled = false;
        var (ok, error) = await DockerService.StopAsync(id);
        if (ok) DevTools.RemoveContainer(id);
        else DevTools.SetContainers(true, DevTools.Containers.ToArray(), error);
        button.IsEnabled = true;
    }

    // ---------------------------------------------------------------- CI completion notices

    /// <summary>Remembers an in-progress run; a one-shot timer re-checks it every minute until it completes (max 45 min).</summary>
    private void WatchRun(RepoSnapshot repo)
    {
        if (repo.Ci is not { InProgress: true } run || !_settings.Current.WatchCi) return;
        if (!_watchedRuns.ContainsKey(repo.Root)) _watchedRuns[repo.Root] = (run, DateTimeOffset.Now);
        if (!_ciTimer.IsRunning) { _ciTimer.Interval = CiPollEvery; _ciTimer.Start(); }
    }

    private async Task PollWatchedRunsAsync()
    {
        if (!_settings.Current.WatchCi) { _watchedRuns.Clear(); return; }
        foreach (var (root, watched) in _watchedRuns.ToArray())
        {
            if (DateTimeOffset.Now - watched.Since > CiWatchLimit) { _watchedRuns.Remove(root); continue; }
            var repo = await _git.ReadAsync(root, forceCi: true);
            if (repo.Ci is not { } run) { _watchedRuns.Remove(root); continue; }
            if (run.InProgress) continue;
            _watchedRuns.Remove(root);
            string name = Path.GetFileName(root);
            _notifications.Post(new("ci:" + root, NotificationKind.Ci,
                run.Passed ? $"CI passed · {name}" : run.Failed ? $"CI failed · {name}" : $"CI {run.Conclusion} · {name}",
                string.IsNullOrWhiteSpace(run.Title) ? run.Workflow : $"{run.Workflow}: {run.Title}", DateTimeOffset.Now, VisibleFor: TimeSpan.FromSeconds(12)));
            if (ViewModel.IsExpanded && ViewModel.SelectedTab == NotchTab.Dev) _ = RefreshDevExtrasAsync(force: true);
        }
        if (_watchedRuns.Count > 0) { _ciTimer.Interval = CiPollEvery; _ciTimer.Start(); }
    }
}
