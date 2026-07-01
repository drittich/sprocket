using Xunit;

namespace Sprocket.Export.Tests;

/// <summary>
/// Deterministic tests for the export queue's orchestration (PLAN.md step 29): sequential ordering, per-job
/// status/progress transitions, per-job and queue-wide cancellation, mid-run enqueue, and list management. These
/// use a fake <see cref="ExportJobRunner"/> — no FFmpeg / encoding — so they exercise the queue mechanics alone
/// (the real encode path is covered by <see cref="ExportRangeTests"/>). Gated tests use bounded waits so a stuck
/// worker fails fast rather than hanging the suite.
/// </summary>
public sealed class ExportQueueTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static ExportJob Job(string name) =>
        new(Path.Combine(Path.GetTempPath(), $"queue-{name}.mp4"), default, name: name);

    [Fact]
    public async Task RunAsync_RunsAllJobs_InEnqueueOrder_MarkingSucceeded()
    {
        var order = new List<string>();
        var queue = new ExportQueue((job, _, _) => { lock (order) order.Add(job.Name); });
        queue.Enqueue(Job("a"));
        queue.Enqueue(Job("b"));
        queue.Enqueue(Job("c"));

        await queue.RunAsync().WaitAsync(Bound);

        Assert.Equal(["a", "b", "c"], order);
        Assert.All(queue.Jobs, j => Assert.Equal(ExportJobStatus.Succeeded, j.Status));
        Assert.All(queue.Jobs, j => Assert.Equal(1.0, j.Progress));
        Assert.False(queue.IsRunning);
    }

    [Fact]
    public async Task RunAsync_ForwardsProgress_AndCompletesToOne()
    {
        var queue = new ExportQueue((_, progress, _) =>
        {
            progress.Report(0.25);
            progress.Report(0.5);
        });
        ExportJob job = queue.Enqueue(Job("a"));

        var observed = new List<double>();
        queue.Changed += () => { lock (observed) observed.Add(job.Progress); };

        await queue.RunAsync().WaitAsync(Bound);

        Assert.Contains(0.5, observed);           // an intermediate report reached subscribers
        Assert.Equal(1.0, job.Progress);          // succeeding forces progress to 1
        Assert.Equal(ExportJobStatus.Succeeded, job.Status);
    }

    [Fact]
    public async Task FailingJob_IsMarkedFailed_WithMessage_AndTheQueueContinues()
    {
        var queue = new ExportQueue((job, _, _) =>
        {
            if (job.Name == "bad")
                throw new InvalidOperationException("boom");
        });
        ExportJob a = queue.Enqueue(Job("a"));
        ExportJob bad = queue.Enqueue(Job("bad"));
        ExportJob c = queue.Enqueue(Job("c"));

        await queue.RunAsync().WaitAsync(Bound);

        Assert.Equal(ExportJobStatus.Succeeded, a.Status);
        Assert.Equal(ExportJobStatus.Failed, bad.Status);
        Assert.Equal("boom", bad.Error);
        Assert.Equal(ExportJobStatus.Succeeded, c.Status); // a failure does not stop later jobs
    }

    [Fact]
    public async Task CancelJob_BeforeItRuns_SkipsIt()
    {
        var ran = new List<string>();
        var queue = new ExportQueue((job, _, _) => { lock (ran) ran.Add(job.Name); });
        ExportJob a = queue.Enqueue(Job("a"));
        ExportJob b = queue.Enqueue(Job("b"));

        queue.CancelJob(b);
        await queue.RunAsync().WaitAsync(Bound);

        Assert.Contains("a", ran);
        Assert.DoesNotContain("b", ran);           // the cancelled job never ran
        Assert.Equal(ExportJobStatus.Succeeded, a.Status);
        Assert.Equal(ExportJobStatus.Cancelled, b.Status);
    }

    [Fact]
    public async Task CancelJob_WhileRunning_MarksItCancelled_AndRunsTheNext()
    {
        using var started = new ManualResetEventSlim();
        var queue = new ExportQueue((job, _, ct) =>
        {
            if (job.Name == "long")
            {
                started.Set();
                ct.WaitHandle.WaitOne(Bound);      // block until cancelled
                ct.ThrowIfCancellationRequested();
            }
        });
        ExportJob longJob = queue.Enqueue(Job("long"));
        ExportJob next = queue.Enqueue(Job("next"));

        Task run = queue.RunAsync();
        Assert.True(started.Wait(Bound));
        queue.CancelJob(longJob);
        await run.WaitAsync(Bound);

        Assert.Equal(ExportJobStatus.Cancelled, longJob.Status);
        Assert.Equal(ExportJobStatus.Succeeded, next.Status);
    }

    [Fact]
    public async Task CancelAll_CancelsTheRunningJobAndAllQueuedJobs()
    {
        using var started = new ManualResetEventSlim();
        var queue = new ExportQueue((_, _, ct) =>
        {
            started.Set();
            ct.WaitHandle.WaitOne(Bound);
            ct.ThrowIfCancellationRequested();
        });
        ExportJob a = queue.Enqueue(Job("a"));
        ExportJob b = queue.Enqueue(Job("b"));
        ExportJob c = queue.Enqueue(Job("c"));

        Task run = queue.RunAsync();
        Assert.True(started.Wait(Bound));
        queue.CancelAll();
        await run.WaitAsync(Bound);

        Assert.Equal(ExportJobStatus.Cancelled, a.Status);
        Assert.Equal(ExportJobStatus.Cancelled, b.Status);
        Assert.Equal(ExportJobStatus.Cancelled, c.Status);
        Assert.False(queue.IsRunning);
    }

    [Fact]
    public async Task Remove_TakesOutAQueuedJob_ButNotTheRunningOne()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var queue = new ExportQueue((_, _, _) => { started.Set(); release.Wait(Bound); });
        ExportJob running = queue.Enqueue(Job("running"));
        ExportJob queued = queue.Enqueue(Job("queued"));

        Task run = queue.RunAsync();
        Assert.True(started.Wait(Bound));

        Assert.False(queue.Remove(running));                              // can't remove the in-flight job
        Assert.Contains(queue.Jobs, j => j.Id == running.Id);
        Assert.True(queue.Remove(queued));                               // a still-queued one is fine
        Assert.DoesNotContain(queue.Jobs, j => j.Id == queued.Id);

        release.Set();
        await run.WaitAsync(Bound);
        Assert.Equal(ExportJobStatus.Succeeded, running.Status);
    }

    [Fact]
    public async Task Enqueue_DuringARun_IsPickedUpByTheSameRun()
    {
        var ran = new List<string>();
        ExportQueue queue = null!;
        queue = new ExportQueue((job, _, _) =>
        {
            lock (ran) ran.Add(job.Name);
            if (job.Name == "first")
                queue.Enqueue(Job("added-mid-run"));
        });
        queue.Enqueue(Job("first"));

        await queue.RunAsync().WaitAsync(Bound);

        Assert.Equal(["first", "added-mid-run"], ran);
    }

    [Fact]
    public async Task ClearCompleted_RemovesFinishedJobs_KeepsQueuedOnes()
    {
        var queue = new ExportQueue((job, _, _) =>
        {
            if (job.Name == "bad")
                throw new InvalidOperationException("x");
        });
        queue.Enqueue(Job("ok"));   // will succeed
        queue.Enqueue(Job("bad"));  // will fail
        await queue.RunAsync().WaitAsync(Bound);

        ExportJob pending = queue.Enqueue(Job("pending")); // added after the run — still Queued

        int removed = queue.ClearCompleted();

        Assert.Equal(2, removed);
        Assert.Single(queue.Jobs);
        Assert.Equal(pending.Id, queue.Jobs[0].Id);
    }

    [Fact]
    public void Enqueue_RaisesChanged_AndAppendsInOrder()
    {
        int changes = 0;
        var queue = new ExportQueue((_, _, _) => { });
        queue.Changed += () => Interlocked.Increment(ref changes);

        ExportJob a = queue.Enqueue(Job("a"));
        ExportJob b = queue.Enqueue(Job("b"));

        Assert.True(changes >= 2);
        Assert.Equal([a.Id, b.Id], queue.Jobs.Select(j => j.Id));
        Assert.All(queue.Jobs, j => Assert.Equal(ExportJobStatus.Queued, j.Status));
    }

    [Fact]
    public void ExportJob_DefaultsNameToFileName_AndStartsQueued()
    {
        var job = new ExportJob(Path.Combine(Path.GetTempPath(), "clip.mp4"), default);
        Assert.Equal("clip.mp4", job.Name);
        Assert.Equal(ExportJobStatus.Queued, job.Status);
        Assert.False(job.IsTerminal);
    }
}
