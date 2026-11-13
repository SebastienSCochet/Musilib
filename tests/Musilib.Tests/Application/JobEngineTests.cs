using Microsoft.Extensions.Logging.Abstractions;
using Musilib.Application.Errors;
using Musilib.Application.Jobs;
using Musilib.Domain.Entities;
using Musilib.Domain.Enums;
using Musilib.Tests.Helpers;

namespace Musilib.Tests.Application;

public class JobEngineTests
{
    private sealed class Payload
    {
        public int Items { get; set; }
        public int DelayMs { get; set; }
        public string Mode { get; set; } = "ok";
    }

    /// <summary>Gestionnaire de test : traite N éléments, mémorise la concurrence maximale observée.</summary>
    private sealed class TestHandler : IJobHandler
    {
        private int _running;
        public int MaxObservedConcurrency;
        public List<string> Processed { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JobType Type => JobType.Match;

        public async Task<JobOutcome> ExecuteAsync(JobContext context, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _running);
            InterlockedMax(ref MaxObservedConcurrency, now);
            try
            {
                var payload = context.GetPayload<Payload>();
                Started.TrySetResult();
                if (payload.Mode == "quota") throw new QuotaExceededException("YouTube", "quota");
                if (payload.Mode == "auth") throw new AuthenticationRequiredException("Spotify", "login");
                if (payload.Mode == "crash") throw new InvalidOperationException("boom");
                await context.SetTotalAsync(payload.Items, ct);
                for (var i = 0; i < payload.Items; i++)
                {
                    var key = $"{context.Job.Id}:{i}";
                    if (context.IsItemDone(i.ToString())) continue;
                    await Task.Delay(payload.DelayMs, ct);
                    lock (Processed) Processed.Add(key);
                    await context.CompleteItemAsync(i.ToString(), JobItemStatus.Completed, null, CancellationToken.None);
                }
                return JobOutcome.Completed("done");
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
        }
    }

    private static JobEngine CreateEngine(TempDatabase db, TestHandler handler, int maxJobs = 2, bool autoResume = true)
    {
        var settings = new TestSettingsService();
        settings.Current.Processing.MaxConcurrentJobs = maxJobs;
        settings.Current.Processing.AutoResumeJobs = autoResume;
        return new JobEngine(db.Jobs, db.History, [handler], settings, NullLogger<JobEngine>.Instance);
    }

    private static async Task<Job> WaitForAsync(TempDatabase db, Guid id, Func<Job, bool> predicate, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var job = await db.Jobs.GetAsync(id);
            if (job is not null && predicate(job)) return job;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Condition non atteinte pour le traitement {id}");
    }

    [Fact]
    public async Task Runs_jobs_persists_progress_and_history()
    {
        await using var db = await TempDatabase.CreateAsync();
        var handler = new TestHandler();
        await using var engine = CreateEngine(db, handler);
        var updates = new List<Job>();
        engine.JobChanged += (_, j) => { lock (updates) updates.Add(j); };
        await engine.StartAsync();

        var job = await engine.EnqueueAsync(JobType.Match, null, "test", new Payload { Items = 5, DelayMs = 5 });
        var done = await WaitForAsync(db, job.Id, j => j.IsTerminal);

        Assert.Equal(JobStatus.Completed, done.Status);
        Assert.Equal(5, done.Processed);
        Assert.Equal(5, (await db.Jobs.GetItemsAsync(job.Id)).Count(i => i.Status == JobItemStatus.Completed));
        Assert.Contains(updates, u => u.Status == JobStatus.Running);
        Assert.Single(await db.History.GetRecentAsync(10, HistoryKind.Match));
    }

    [Fact]
    public async Task Respects_max_concurrent_jobs()
    {
        await using var db = await TempDatabase.CreateAsync();
        var handler = new TestHandler();
        await using var engine = CreateEngine(db, handler, maxJobs: 2);
        await engine.StartAsync();

        var jobs = new List<Job>();
        for (var i = 0; i < 5; i++)
            jobs.Add(await engine.EnqueueAsync(JobType.Match, Guid.NewGuid(), "job" + i, new Payload { Items = 4, DelayMs = 30 }));
        foreach (var j in jobs) await WaitForAsync(db, j.Id, x => x.IsTerminal, 20_000);

        Assert.True(handler.MaxObservedConcurrency <= 2, $"concurrence {handler.MaxObservedConcurrency}");
        Assert.True(handler.MaxObservedConcurrency >= 1);
    }

    [Fact]
    public async Task Duplicate_enqueue_returns_existing_job()
    {
        await using var db = await TempDatabase.CreateAsync();
        var handler = new TestHandler();
        await using var engine = CreateEngine(db, handler);
        var playlist = Guid.NewGuid();
        var a = await engine.EnqueueAsync(JobType.Match, playlist, "a", new Payload { Items = 1 });
        var b = await engine.EnqueueAsync(JobType.Match, playlist, "b", new Payload { Items = 1 });
        Assert.Equal(a.Id, b.Id);
    }

    [Fact]
    public async Task Cancel_and_pause_stop_cleanly_and_resume_skips_completed_items()
    {
        await using var db = await TempDatabase.CreateAsync();
        var handler = new TestHandler();
        await using var engine = CreateEngine(db, handler);
        await engine.StartAsync();

        var job = await engine.EnqueueAsync(JobType.Match, null, "long", new Payload { Items = 40, DelayMs = 20 });
        await WaitForAsync(db, job.Id, j => j.Processed >= 3 || (j.Status == JobStatus.Running && j.Total > 0));
        await Task.Delay(120);
        await engine.PauseAsync(job.Id);
        var paused = await WaitForAsync(db, job.Id, j => j.Status == JobStatus.Paused);
        var doneBefore = (await db.Jobs.GetItemsAsync(job.Id)).Count;
        Assert.InRange(doneBefore, 1, 39);

        await engine.ResumeAsync(job.Id);
        var completed = await WaitForAsync(db, job.Id, j => j.IsTerminal, 20_000);
        Assert.Equal(JobStatus.Completed, completed.Status);
        // Chaque élément n'est traité qu'une seule fois malgré la pause.
        Assert.Equal(40, handler.Processed.Count);
        Assert.Equal(40, handler.Processed.Distinct().Count());

        var other = await engine.EnqueueAsync(JobType.Match, Guid.NewGuid(), "cancel", new Payload { Items = 50, DelayMs = 20 });
        await WaitForAsync(db, other.Id, j => j.Status == JobStatus.Running);
        await engine.CancelAsync(other.Id);
        var cancelled = await WaitForAsync(db, other.Id, j => j.IsTerminal);
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        Assert.NotNull(paused.Message);
    }

    [Theory]
    [InlineData("quota", JobStatus.Paused)]
    [InlineData("auth", JobStatus.Paused)]
    [InlineData("crash", JobStatus.Failed)]
    public async Task Errors_are_mapped_to_resumable_statuses(string mode, JobStatus expected)
    {
        await using var db = await TempDatabase.CreateAsync();
        var handler = new TestHandler();
        await using var engine = CreateEngine(db, handler);
        await engine.StartAsync();

        var job = await engine.EnqueueAsync(JobType.Match, null, mode, new Payload { Items = 1, Mode = mode });
        var result = await WaitForAsync(db, job.Id, j => j.Status == expected);
        Assert.False(string.IsNullOrEmpty(result.Message));
        Assert.True(result.CanResume);
    }

    [Fact]
    public async Task Interrupted_jobs_are_resumed_after_restart()
    {
        await using var db = await TempDatabase.CreateAsync();
        // Simule une fermeture brutale : un traitement est resté « Running » avec 2 éléments faits.
        var job = new Job { Type = JobType.Match, Status = JobStatus.Running, Title = "crashed", Payload = Musilib.Application.Common.JsonDefaults.Serialize(new Payload { Items = 5 }) };
        await db.Jobs.InsertAsync(job);
        await db.Jobs.UpsertItemAsync(new JobItem { JobId = job.Id, ItemKey = "0", Status = JobItemStatus.Completed });
        await db.Jobs.UpsertItemAsync(new JobItem { JobId = job.Id, ItemKey = "1", Status = JobItemStatus.Completed });

        var handler = new TestHandler();
        await using var engine = CreateEngine(db, handler);
        await engine.StartAsync();
        var done = await WaitForAsync(db, job.Id, j => j.IsTerminal);

        Assert.Equal(JobStatus.Completed, done.Status);
        Assert.Equal(3, handler.Processed.Count);
        Assert.Equal(5, done.Processed);
    }

    [Fact]
    public async Task Without_auto_resume_interrupted_jobs_are_paused()
    {
        await using var db = await TempDatabase.CreateAsync();
        var job = new Job { Type = JobType.Match, Status = JobStatus.Running, Title = "crashed", Payload = "{}" };
        await db.Jobs.InsertAsync(job);
        await using var engine = CreateEngine(db, new TestHandler(), autoResume: false);
        await engine.StartAsync();
        Assert.Equal(JobStatus.Paused, (await db.Jobs.GetAsync(job.Id))!.Status);
    }
}
