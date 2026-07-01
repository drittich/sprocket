using Sprocket.Core.Model;

namespace Sprocket.Export;

/// <summary>The lifecycle state of a queued export job.</summary>
public enum ExportJobStatus
{
    /// <summary>Waiting to run.</summary>
    Queued,

    /// <summary>Currently rendering/encoding.</summary>
    Running,

    /// <summary>Finished and the file was written.</summary>
    Succeeded,

    /// <summary>Stopped before finishing (per-job cancel or a queue-wide stop).</summary>
    Cancelled,

    /// <summary>Ended in an error (see <see cref="ExportJob.Error"/>); the queue moves on to the next job.</summary>
    Failed,
}

/// <summary>
/// One export in the queue (PLAN.md step 29). The immutable half is the <em>spec</em> — where to write, in what
/// format, which sequence, and an optional in-out range; the mutable half is the runtime <see cref="Status"/> /
/// <see cref="Progress"/> the <see cref="ExportQueue"/> drives as it runs (only the queue mutates them, on its
/// worker; the UI reads them and refreshes on <see cref="ExportQueue.Changed"/>).
/// </summary>
public sealed class ExportJob
{
    /// <summary>Creates a queued job. <paramref name="name"/> defaults to the output file name.</summary>
    /// <param name="outputPath">Absolute destination path (its extension should match <paramref name="options"/>'s container).</param>
    /// <param name="options">Container / codec / quality to deliver.</param>
    /// <param name="sequenceId">The sequence to render, or <see langword="null"/> for the project's active sequence at run time.</param>
    /// <param name="range">The half-open timeline slice to export, or <see langword="null"/> for the whole timeline.</param>
    /// <param name="name">A display label, or <see langword="null"/> to use the output file name.</param>
    public ExportJob(
        string outputPath,
        ExportOptions options,
        SequenceId? sequenceId = null,
        ExportRange? range = null,
        string? name = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        OutputPath = outputPath;
        Options = options;
        SequenceId = sequenceId;
        Range = range;
        Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(outputPath) : name;
    }

    /// <summary>Stable identity for the lifetime of the job (UI row tracking).</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>A short display label (defaults to the output file name).</summary>
    public string Name { get; }

    /// <summary>Absolute destination path.</summary>
    public string OutputPath { get; }

    /// <summary>The delivery format / codecs / quality.</summary>
    public ExportOptions Options { get; }

    /// <summary>The sequence to render (<see langword="null"/> = the active sequence at run time).</summary>
    public SequenceId? SequenceId { get; }

    /// <summary>The timeline slice to export (<see langword="null"/> = the whole timeline).</summary>
    public ExportRange? Range { get; }

    /// <summary>Current lifecycle state; mutated only by the owning <see cref="ExportQueue"/>.</summary>
    public ExportJobStatus Status { get; internal set; } = ExportJobStatus.Queued;

    /// <summary>Progress in [0, 1] while running; 1 once succeeded.</summary>
    public double Progress { get; internal set; }

    /// <summary>The failure message when <see cref="Status"/> is <see cref="ExportJobStatus.Failed"/>.</summary>
    public string? Error { get; internal set; }

    /// <summary>Whether the job has reached a final state (succeeded, cancelled, or failed).</summary>
    public bool IsTerminal =>
        Status is ExportJobStatus.Succeeded or ExportJobStatus.Cancelled or ExportJobStatus.Failed;
}

/// <summary>
/// Runs one export job: renders/encodes it to its output, reporting progress and observing the cancellation
/// token. Throws <see cref="OperationCanceledException"/> if cancelled and any other exception on failure — the
/// <see cref="ExportQueue"/> maps those onto the job's terminal state. The production runner wraps
/// <see cref="VideoExporter.Export(Project, string, ExportOptions, Core.Model.SequenceId?, ExportRange?, IProgress{double}?, CancellationToken)"/>;
/// tests inject a fake so the queue's orchestration is verified without encoding.
/// </summary>
public delegate void ExportJobRunner(ExportJob job, IProgress<double> progress, CancellationToken cancellationToken);

/// <summary>
/// A sequential export queue (PLAN.md step 29): jobs run one at a time on a background worker so a single
/// in-process libav* muxer is ever active (concurrent muxing crashes the native encoder — see
/// <see cref="VideoExporter"/> / the App's export quiesce). Each job reports its own progress and can be
/// cancelled individually; the whole queue can be stopped. Jobs added while a run is in flight are picked up.
/// </summary>
/// <remarks>
/// <para>The queue is decoupled from the actual encoder by an injected <see cref="ExportJobRunner"/>: it owns the
/// ordering, status transitions, and cancellation, not the rendering. This keeps it fully unit-testable without
/// FFmpeg and lets the composition root bind the runner to <see cref="VideoExporter"/> over the real project.</para>
/// <para><b>Threading:</b> the public surface is safe to call from the UI thread while a run proceeds on a worker.
/// Mutations to the job list and each job's state happen under a lock; <see cref="Changed"/> is raised outside the
/// lock and may fire on the worker thread, so a UI subscriber must marshal to its own thread.</para>
/// </remarks>
public sealed class ExportQueue
{
    private readonly ExportJobRunner _runner;
    private readonly object _gate = new();
    private readonly List<ExportJob> _jobs = new();
    private CancellationTokenSource? _currentJobCts; // the running job's linked CTS (for per-job / stop cancel)
    private ExportJob? _running;
    private Task? _runTask;

    /// <summary>Creates a queue that runs each job through <paramref name="runner"/>.</summary>
    public ExportQueue(ExportJobRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <summary>Raised whenever the queue or any job's state changes (a job added/removed, or a status/progress
    /// update). May fire on a worker thread — marshal to the UI thread in the handler.</summary>
    public event Action? Changed;

    /// <summary>A snapshot of the jobs in queue order (safe to enumerate).</summary>
    public IReadOnlyList<ExportJob> Jobs
    {
        get { lock (_gate) return _jobs.ToArray(); }
    }

    /// <summary>Whether a job is currently running.</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _running is not null; }
    }

    /// <summary>Whether any job is still waiting to run.</summary>
    public bool HasPending
    {
        get { lock (_gate) return _jobs.Any(j => j.Status == ExportJobStatus.Queued); }
    }

    /// <summary>Adds <paramref name="job"/> to the end of the queue and returns it.</summary>
    public ExportJob Enqueue(ExportJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_gate)
            _jobs.Add(job);
        RaiseChanged();
        return job;
    }

    /// <summary>Builds and enqueues a job (convenience over <see cref="Enqueue(ExportJob)"/>).</summary>
    public ExportJob Enqueue(
        string outputPath, ExportOptions options,
        SequenceId? sequenceId = null, ExportRange? range = null, string? name = null)
        => Enqueue(new ExportJob(outputPath, options, sequenceId, range, name));

    /// <summary>Removes a job that is not currently running. Returns whether it was removed (a running job stays —
    /// cancel it first).</summary>
    public bool Remove(ExportJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        bool removed;
        lock (_gate)
        {
            if (ReferenceEquals(_running, job))
                return false;
            removed = _jobs.Remove(job);
        }
        if (removed)
            RaiseChanged();
        return removed;
    }

    /// <summary>Removes all jobs that have finished (succeeded / cancelled / failed), leaving queued and running
    /// jobs. Returns how many were removed.</summary>
    public int ClearCompleted()
    {
        int removed;
        lock (_gate)
            removed = _jobs.RemoveAll(j => j.IsTerminal);
        if (removed > 0)
            RaiseChanged();
        return removed;
    }

    /// <summary>Cancels a single job: a running job's runner is signalled (it ends as
    /// <see cref="ExportJobStatus.Cancelled"/>); a still-queued job is marked cancelled so the worker skips it. A
    /// job that has already finished is left alone.</summary>
    public void CancelJob(ExportJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        bool changed = false;
        lock (_gate)
        {
            if (ReferenceEquals(_running, job))
            {
                _currentJobCts?.Cancel();
            }
            else if (job.Status == ExportJobStatus.Queued)
            {
                job.Status = ExportJobStatus.Cancelled;
                changed = true;
            }
        }
        if (changed)
            RaiseChanged();
    }

    /// <summary>Stops the whole queue: cancels the running job and marks every still-queued job cancelled, so the
    /// run drains to a stop.</summary>
    public void CancelAll()
    {
        lock (_gate)
        {
            _currentJobCts?.Cancel();
            foreach (ExportJob j in _jobs)
                if (j.Status == ExportJobStatus.Queued)
                    j.Status = ExportJobStatus.Cancelled;
        }
        RaiseChanged();
    }

    /// <summary>
    /// Runs queued jobs sequentially until none remain, returning a task that completes when the run drains. Jobs
    /// enqueued during the run are picked up. Calling this while a run is already in flight returns the in-flight
    /// task (there is never more than one worker). <paramref name="cancellationToken"/> stops the whole run (like
    /// <see cref="CancelAll"/>, but externally driven).
    /// </summary>
    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_runTask is { IsCompleted: false })
                return _runTask;
            _runTask = RunLoopAsync(cancellationToken);
            return _runTask;
        }
    }

    private async Task RunLoopAsync(CancellationToken queueToken)
    {
        while (true)
        {
            ExportJob job;
            CancellationTokenSource jobCts;
            lock (_gate)
            {
                ExportJob? next = _jobs.FirstOrDefault(j => j.Status == ExportJobStatus.Queued);
                if (next is null)
                {
                    _running = null;
                    break;
                }
                job = next;
                jobCts = CancellationTokenSource.CreateLinkedTokenSource(queueToken);
                _currentJobCts = jobCts;
                _running = job;
                job.Status = ExportJobStatus.Running;
                job.Progress = 0;
                job.Error = null;
            }
            RaiseChanged();

            var progress = new JobProgress(this, job);
            try
            {
                // Run the (CPU-bound) job on the thread pool so RunAsync stays non-blocking when awaited on the UI
                // thread. If the token is already cancelled, Task.Run yields a cancelled task → OperationCanceledException.
                await Task.Run(() => _runner(job, progress, jobCts.Token), jobCts.Token).ConfigureAwait(false);
                job.Status = ExportJobStatus.Succeeded;
                job.Progress = 1.0;
            }
            catch (OperationCanceledException)
            {
                job.Status = ExportJobStatus.Cancelled;
            }
            catch (Exception ex)
            {
                job.Status = ExportJobStatus.Failed;
                job.Error = ex.Message;
            }
            finally
            {
                lock (_gate)
                {
                    _running = null;
                    _currentJobCts = null;
                }
                jobCts.Dispose();
            }
            RaiseChanged();
        }
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    /// <summary>Updates a job's progress and notifies subscribers. Invoked synchronously by the runner on its
    /// worker thread so the queue's <see cref="Changed"/> event fires deterministically (no captured sync context).</summary>
    private sealed class JobProgress(ExportQueue queue, ExportJob job) : IProgress<double>
    {
        public void Report(double value)
        {
            job.Progress = Math.Clamp(value, 0.0, 1.0);
            queue.RaiseChanged();
        }
    }
}
