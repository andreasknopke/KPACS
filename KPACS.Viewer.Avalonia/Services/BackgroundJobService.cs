using System.Globalization;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class BackgroundJobService
{
    private readonly Lock _syncRoot = new();
    private readonly List<BackgroundJobInfo> _jobs = [];
    private readonly string _jobsDirectory;

    public BackgroundJobService(string applicationDirectory)
    {
        _jobsDirectory = Path.Combine(applicationDirectory, "job-logs");
        Directory.CreateDirectory(_jobsDirectory);
    }

    public event Action? JobsChanged;

    public string JobsDirectory => _jobsDirectory;

    public BackgroundJobInfo CreateJob(BackgroundJobType jobType, string key, string title, string initialStatus)
    {
        string safeFileName = SanitizeFileName($"{DateTime.UtcNow:yyyyMMdd-HHmmss}_{jobType}_{title}");
        string logPath = Path.Combine(_jobsDirectory, safeFileName + ".log");
        var job = new BackgroundJobInfo
        {
            JobId = Guid.NewGuid(),
            JobType = jobType,
            Key = key,
            Title = title,
            StatusText = initialStatus,
            State = BackgroundJobState.Queued,
            LogPath = logPath,
        };

        lock (_syncRoot)
        {
            _jobs.Insert(0, job);
            if (_jobs.Count > 100)
            {
                _jobs.RemoveRange(100, _jobs.Count - 100);
            }
        }

        AppendLog(job.JobId, initialStatus);
        NotifyChanged();
        return job;
    }

    public IReadOnlyList<BackgroundJobInfo> GetJobsSnapshot()
    {
        lock (_syncRoot)
        {
            return _jobs
                .Select(Clone)
                .ToList();
        }
    }

    public BackgroundJobInfo? GetJob(Guid jobId)
    {
        lock (_syncRoot)
        {
            BackgroundJobInfo? job = _jobs.FirstOrDefault(candidate => candidate.JobId == jobId);
            return job is null ? null : Clone(job);
        }
    }

    public string ReadJobLog(Guid jobId)
    {
        BackgroundJobInfo? job = GetJob(jobId);
        if (job is null)
        {
            return "Selected job was not found.";
        }

        if (!File.Exists(job.LogPath))
        {
            return $"No log file exists yet for {job.Title}.";
        }

        return File.ReadAllText(job.LogPath);
    }

    public void MarkRunning(Guid jobId, string statusText, int totalUnits)
    {
        Update(jobId, job =>
        {
            job.State = BackgroundJobState.Running;
            job.StatusText = statusText;
            job.TotalUnits = Math.Max(totalUnits, 0);
            job.StartedAtUtc ??= DateTime.UtcNow;
        }, statusText);
    }

    public void ReportProgress(Guid jobId, string statusText, int completedUnits, int totalUnits)
    {
        Update(jobId, job =>
        {
            job.State = BackgroundJobState.Running;
            job.StatusText = statusText;
            job.CompletedUnits = Math.Max(completedUnits, 0);
            job.TotalUnits = Math.Max(totalUnits, 0);
            job.StartedAtUtc ??= DateTime.UtcNow;
        }, statusText, appendToLog: false);
    }

    public void MarkCompleted(Guid jobId, string statusText)
    {
        Update(jobId, job =>
        {
            job.State = BackgroundJobState.Completed;
            job.StatusText = statusText;
            job.CompletedUnits = Math.Max(job.CompletedUnits, job.TotalUnits);
            job.CompletedAtUtc = DateTime.UtcNow;
        }, statusText);
    }

    public void MarkFailed(Guid jobId, string statusText)
    {
        Update(jobId, job =>
        {
            job.State = BackgroundJobState.Failed;
            job.StatusText = statusText;
            job.CompletedAtUtc = DateTime.UtcNow;
        }, statusText);
    }

    public void AppendLog(Guid jobId, string message)
    {
        BackgroundJobInfo? job;
        lock (_syncRoot)
        {
            job = _jobs.FirstOrDefault(candidate => candidate.JobId == jobId);
        }

        if (job is null || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string line = $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}] {message}{Environment.NewLine}";
        File.AppendAllText(job.LogPath, line);
    }

    private void Update(Guid jobId, Action<BackgroundJobInfo> update, string? logMessage, bool appendToLog = true)
    {
        bool changed = false;
        lock (_syncRoot)
        {
            BackgroundJobInfo? job = _jobs.FirstOrDefault(candidate => candidate.JobId == jobId);
            if (job is null)
            {
                return;
            }

            update(job);
            changed = true;
        }

        if (appendToLog && !string.IsNullOrWhiteSpace(logMessage))
        {
            AppendLog(jobId, logMessage);
        }

        if (changed)
        {
            NotifyChanged();
        }
    }

    private void NotifyChanged() => JobsChanged?.Invoke();

    private static BackgroundJobInfo Clone(BackgroundJobInfo source)
    {
        return new BackgroundJobInfo
        {
            JobId = source.JobId,
            JobType = source.JobType,
            Key = source.Key,
            Title = source.Title,
            StatusText = source.StatusText,
            State = source.State,
            CompletedUnits = source.CompletedUnits,
            TotalUnits = source.TotalUnits,
            CreatedAtUtc = source.CreatedAtUtc,
            StartedAtUtc = source.StartedAtUtc,
            CompletedAtUtc = source.CompletedAtUtc,
            LogPath = source.LogPath,
        };
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
    }
}
