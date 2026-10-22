using System;
using System.Collections.Generic;
using System.Linq;

namespace WanStudio.Services;

public sealed record ActiveJob(
    string TaskId,
    string Prompt,
    string Model,
    string Resolution,
    int Duration,
    DateTimeOffset CreatedAt,
    string Status,
    string? VideoUrl,
    string? Message,
    string? LocalPath = null);

public static class ActiveJobStore
{
    private static readonly object Gate = new();
    private static readonly List<ActiveJob> Jobs = new();

    public static ActiveJob? Latest
    {
        get
        {
            lock (Gate)
            {
                return Jobs.LastOrDefault();
            }
        }
    }

    public static ActiveJob Add(ActiveJob job)
    {
        lock (Gate)
        {
            Jobs.Add(job);
            return job;
        }
    }

    public static void Upsert(ActiveJob job)
    {
        lock (Gate)
        {
            var idx = Jobs.FindIndex(j => j.TaskId == job.TaskId);
            if (idx >= 0)
            {
                Jobs[idx] = job;
            }
            else
            {
                Jobs.Add(job);
            }
        }
    }

    public static ActiveJob? Find(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        lock (Gate)
        {
            return Jobs.FirstOrDefault(j => j.TaskId == taskId);
        }
    }
}
