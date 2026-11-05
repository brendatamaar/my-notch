using Microsoft.UI.Xaml.Media;
using MyNotch.Models;
using Windows.UI;

namespace MyNotch.Controls;

/// <summary>Shared status colours (Oyenza tokens), cached so list rows don't allocate a brush per bind.</summary>
internal static class UiBrushes
{
    internal static readonly SolidColorBrush Accent = new(Color.FromArgb(255, 240, 137, 46));
    internal static readonly SolidColorBrush Neutral = new(Color.FromArgb(255, 142, 142, 147));
    private static readonly SolidColorBrush Blue = new(Color.FromArgb(255, 10, 132, 255));
    private static readonly SolidColorBrush Green = new(Color.FromArgb(255, 48, 209, 88));
    private static readonly SolidColorBrush Red = new(Color.FromArgb(255, 229, 72, 77));

    internal static Brush ForStatus(AgentStatus status) => status switch
    {
        AgentStatus.Running => Blue,
        AgentStatus.AwaitingApproval => Accent,
        AgentStatus.Done => Green,
        AgentStatus.Error => Red,
        _ => Neutral,
    };

    internal static string StatusLabel(AgentStatus status) => Services.GlanceResolver.StatusLabel(status);
}
