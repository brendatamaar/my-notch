using System.Runtime.InteropServices;
using MyNotch.Models;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace MyNotch.Services;

/// <summary>Uses only the consent-based public listener; never reads the notification database.</summary>
public sealed class WindowsNotificationService : IDisposable
{
    private UserNotificationListener? _listener;
    private readonly NotificationHub _hub;
    private bool _disposed, _subscribed;
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private readonly HashSet<string> _seen = new();
    private readonly object _gate = new();
    public string Status { get; private set; } = "Click Enable to allow app notifications.";
    public WindowsNotificationService(NotificationHub hub) => _hub = hub;
    public async Task StartAsync(bool requestAccess = false)
    {
        try
        {
            _listener ??= UserNotificationListener.Current;
            var access = requestAccess ? await _listener.RequestAccessAsync() : _listener.GetAccessStatus();
            if (_disposed) return;
            Status = access switch
            {
                UserNotificationListenerAccessStatus.Allowed => "Windows app notifications enabled.",
                UserNotificationListenerAccessStatus.Denied => "Access denied. Allow notification access in Windows Settings.",
                _ => "Click Enable and allow access in the Windows dialog.",
            };
            if (access != UserNotificationListenerAccessStatus.Allowed) return;
            if (!_subscribed) { _listener.NotificationChanged += NotificationChanged; _subscribed = true; }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        { Status = "Windows access requires the identity-enabled build. Click Set up, then restart MyNotch."; }
    }
    private void NotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (_disposed || args.ChangeKind != UserNotificationChangedKind.Added || !_hub.Enabled(NotificationKind.WindowsApp)) return;
        try
        {
            if (sender.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed) return;
            var item = sender.GetNotification(args.UserNotificationId);
            if (item is null || item.CreationTime < _started) return;
            string key = $"windows:{item.Id}:{item.CreationTime.UtcTicks}";
            lock (_gate) { if (!_seen.Add(key)) return; if (_seen.Count > 512) { _seen.Clear(); _seen.Add(key); } }
            var text = item.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements();
            if (text is null || text.Count == 0) return;
            string message = string.Join(" - ", text.Select(t => t.Text)).Replace('\r', ' ').Replace('\n', ' ');
            if (message.Length > 500) message = message[..500];
            _hub.Post(new(key, NotificationKind.WindowsApp, item.AppInfo.DisplayInfo.DisplayName, message, item.CreationTime));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException) { }
    }
    public void Dispose() { _disposed = true; if (_subscribed && _listener is not null) _listener.NotificationChanged -= NotificationChanged; }
}
