namespace MoenotesAssets;
// Retain a completed payload until all consumers release their leases.
public sealed class SharedWork<T> where T : class
{
    private sealed class Entry
    {
        public readonly CancellationTokenSource Cancellation = new();
        public required Task<T> Task;
        public int Users;
        public readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Dictionary<string, Entry> entries = new();
    private readonly object gate = new();
    private readonly HashSet<Task> pending = new();
    private readonly Action<T>? cleanup;
    public SharedWork(Action<T>? cleanup = null) { this.cleanup = cleanup; }
    public async Task<Lease> Join(string key, Func<CancellationToken, Task<T>> create, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Entry entry;
        lock (gate)
        {
            if (!entries.TryGetValue(key, out entry!))
            {
                entry = new Entry { Task = null!, Users = 0 };
                var source = entry.Cancellation.Token;
                entry.Task = Task.Run(() => create(source), CancellationToken.None);
                entries.Add(key, entry); pending.Add(entry.Finished.Task);
            }
            entry.Users++;
        }
        try { return new Lease(await entry.Task.WaitAsync(token), () => Release(key, entry)); }
        catch { Release(key, entry); throw; }
    }
    private void Release(string key, Entry entry)
    {
        lock (gate)
        {
            if (--entry.Users != 0) return;
            entries.Remove(key);
            entry.Cancellation.Cancel();
        }
        _ = entry.Task.ContinueWith(t =>
        {
            try { if (t.IsCompletedSuccessfully) cleanup?.Invoke(t.Result); else _ = t.Exception; }
            finally { entry.Cancellation.Dispose(); lock (gate) pending.Remove(entry.Finished.Task); entry.Finished.SetResult(); }
        }, TaskScheduler.Default);
    }
    public async Task Drain() { while (true) { Task[] tasks; lock (gate) tasks = pending.ToArray(); if (tasks.Length == 0) return; await Task.WhenAll(tasks); } }
    public sealed class Lease(T value, Action release) : IDisposable
    {
        public T Value { get; } = value;
        private Action? release = release;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
public sealed class Budget(long maximum)
{
    private long used;
    public long Used => Interlocked.Read(ref used);
    public IDisposable Reserve(long size)
    {
        while (true)
        {
            var before = Used;
            Config.Require(size >= 0 && size <= maximum - before, "Temporary storage budget exhausted");
            if (Interlocked.CompareExchange(ref used, before + size, before) == before) return new Reservation(this, size);
        }
    }
    private sealed class Reservation(Budget budget, long size) : IDisposable
    {
        private long bytes = size;
        public void Dispose() => Interlocked.Add(ref budget.used, -Interlocked.Exchange(ref bytes, 0));
    }
}
