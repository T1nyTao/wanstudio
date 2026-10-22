using System;

namespace WanStudio.Services;

public sealed class GenerationJob
{
    public string TaskId { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string Model { get; init; } = "";
    public string Resolution { get; init; } = "480P";
    public int Duration { get; init; } = 5;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>In-memory handoff from Home → Tasks for the active generation.</summary>
public static class JobSession
{
    public static GenerationJob? Current { get; set; }
    public static string? LastError { get; set; }
    public static string? LastOutputPath { get; set; }
}
