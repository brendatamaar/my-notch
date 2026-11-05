using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MyNotch.Interop;

/// <summary>
/// A system backdrop that paints nothing. WinUI 3 windows are opaque (black) wherever XAML draws nothing,
/// even with no backdrop set; handing the window a fully transparent brush as its system backdrop lets the
/// desktop show through, which the floating capsule, its shadow and its rounded ends depend on.
/// </summary>
/// <remarks>
/// The brush must come from the system compositor (Windows.UI.Composition), and that compositor can only be
/// created on a thread that has a Windows.System dispatcher queue. A WinUI thread only has the Microsoft.UI
/// one, so one is created for the UI thread first (the same step the official Mica-for-Win32 sample takes).
/// </remarks>
public sealed partial class TransparentBackdrop : SystemBackdrop
{
    private static Windows.System.DispatcherQueueController? s_queueController;

    private Windows.UI.Composition.Compositor? _compositor;
    private Windows.UI.Composition.CompositionColorBrush? _brush;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        try
        {
            EnsureSystemDispatcherQueue();
            _compositor ??= new Windows.UI.Composition.Compositor();
            _brush ??= _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            connectedTarget.SystemBackdrop = _brush;
        }
        catch (Exception ex)
        {
            // Falls back to an opaque black window: ugly around the capsule, but the app still works.
            System.Diagnostics.Debug.WriteLine($"[MyNotch] Transparent backdrop unavailable: {ex.Message}");
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        disconnectedTarget.SystemBackdrop = null;
    }


    private static void EnsureSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null || s_queueController is not null) return;

        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = DQTYPE_THREAD_CURRENT,
            apartmentType = DQTAT_COM_STA,
        };
        int hr = CreateDispatcherQueueController(options, out var controller);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        s_queueController = WinRT.MarshalInspectable<Windows.System.DispatcherQueueController>.FromAbi(controller);
    }

    private const int DQTYPE_THREAD_CURRENT = 2;
    private const int DQTAT_COM_STA = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [LibraryImport("CoreMessaging.dll")]
    private static partial int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
}

