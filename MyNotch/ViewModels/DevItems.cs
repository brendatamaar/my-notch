using Microsoft.UI.Xaml.Media;

namespace MyNotch.ViewModels;

/// <summary>One running Docker container with its published ports. Display-only projection.</summary>
public sealed record DockerContainerItem(string Id, string Name, string Image, string PortsText, string OpenUrl, string State)
{
    public bool HasOpen => OpenUrl.Length > 0;
    public string Subtitle => PortsText.Length > 0 ? $"{Image} · {PortsText}" : Image;
}

/// <summary>One local git repository: branch, sync state, working-tree changes and latest CI run.</summary>
public sealed record RepoItem(string Name, string Path, string Branch, string SyncText, string ChangesText, string CiText, string CiUrl, Brush CiBrush)
{
    public bool HasCi => CiText.Length > 0;
    public string Subtitle => string.Join(" · ", new[] { Branch, SyncText, ChangesText }.Where(s => s.Length > 0));
}
