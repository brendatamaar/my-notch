namespace MyNotch.Models;

/// <summary>One saved command for the Phase 5 palette (PRD §3.7).
/// Kind is a plain string ("terminal" | "ssh" | "script") to stay AOT-safe without converters.</summary>
public sealed record SavedCommand
{
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = "terminal";
    public string Target { get; init; } = string.Empty;
    public string Keywords { get; init; } = string.Empty;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Kind);

    public string DisplayText => $"{Name}  ·  {Kind}: {Target}";
}
