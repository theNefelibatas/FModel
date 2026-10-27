using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace FModel.Automation;

internal sealed class OperationJobs : IDisposable
{
    internal sealed class Job(string operation)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Operation { get; } = operation;
        public CancellationTokenSource Cancellation { get; } = new();
        public volatile string State = "queued";
        public int Completed;
        public int Total;
        public string Error;
        public ConcurrentQueue<object> Results { get; } = new();
    }

    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    public bool IsBusy => _jobs.Values.Any(x => x.State is "queued" or "running");

    public object Start(string operation, Func<Job, Task> action)
    {
        if (_jobs.Count >= 64)
        {
            var oldest = _jobs.FirstOrDefault(x => x.Value.State is not ("queued" or "running"));
            if (oldest.Key == null) throw new InvalidOperationException("Too many active jobs.");
            // A status/cancellation request may still hold the evicted job; let its token be collected with it.
            _jobs.TryRemove(oldest.Key, out _);
        }
        var job = new Job(operation);
        _jobs[job.Id] = job;
        _ = Task.Run(async () =>
        {
            job.State = "running";
            try
            {
                await action(job);
                job.State = job.Cancellation.IsCancellationRequested ? "cancelled" : "completed";
            }
            catch (OperationCanceledException) { job.State = "cancelled"; }
            catch (Exception ex) { job.Error = ex.Message; job.State = "failed"; }
        });
        return new { jobId = job.Id, state = "queued" };
    }

    public object Status(JObject args)
    {
        var job = Find(args);
        return new { jobId = job.Id, operation = job.Operation, state = job.State, completed = job.Completed,
            total = job.Total, error = job.Error, results = QueryHelpers.Page(job.Results, args) };
    }

    public object Cancel(JObject args)
    {
        var job = Find(args);
        if (job.State is "queued" or "running") job.Cancellation.Cancel();
        return new { jobId = job.Id, state = job.State, cancellationRequested = job.Cancellation.IsCancellationRequested };
    }

    private Job Find(JObject args) => _jobs.TryGetValue(args.Value<string>("jobId") ?? "", out var job)
        ? job : throw new ArgumentException("Unknown or expired job ID.");

    public void Dispose()
    {
        foreach (var job in _jobs.Values) job.Cancellation.Cancel();
    }
}
