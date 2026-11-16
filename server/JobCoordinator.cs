using System.Collections.Concurrent;

namespace paprika;

public sealed class JobCoordinator
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> jobs = new();

    public CancellationTokenSource Begin(string id, CancellationToken stopping)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        jobs[id] = source;
        return source;
    }

    public void Cancel(string id)
    {
        if (jobs.TryGetValue(id, out var source))
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException) { }
    }

    public void End(string id) => jobs.TryRemove(id, out _);
}
