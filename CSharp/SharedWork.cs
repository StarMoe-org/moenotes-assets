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
        catch { await Release(key, entry); throw; }
    }
    private Task Release(string key, Entry entry)
    {
        lock (gate)
        {
            if (--entry.Users != 0) return Task.CompletedTask;
            entries.Remove(key);
            entry.Cancellation.Cancel();
        }
        _ = entry.Task.ContinueWith(t =>
        {
            try { if (t.IsCompletedSuccessfully) cleanup?.Invoke(t.Result); else _ = t.Exception; }
            finally { entry.Cancellation.Dispose(); lock (gate) pending.Remove(entry.Finished.Task); entry.Finished.SetResult(); }
        }, TaskScheduler.Default);
        return entry.Finished.Task;
    }
    public async Task Drain() { while (true) { Task[] tasks; lock (gate) tasks = pending.ToArray(); if (tasks.Length == 0) return; await Task.WhenAll(tasks); } }
    public sealed class Lease(T value, Func<Task> release) : IDisposable, IAsyncDisposable
    {
        public T Value { get; } = value;
        private Func<Task>? release = release;
        public void Dispose() { _ = Interlocked.Exchange(ref release, null)?.Invoke(); }
        public async ValueTask DisposeAsync() { var task = Interlocked.Exchange(ref release, null)?.Invoke(); if (task != null) await task; }
    }
}
public sealed class Budget(long maximum)
{
    private long used;
    private readonly object gate = new();
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public long Used => Interlocked.Read(ref used);
    public IDisposable Reserve(long size)
    {
        lock (gate)
        {
            Config.Require(size >= 0 && size <= maximum - used, "Temporary storage budget exhausted");
            used += size; return new Reservation(this, size);
        }
    }
    public async ValueTask<IDisposable> ReserveAsync(long size, CancellationToken token)
    {
        Config.Require(size >= 0 && size <= maximum, "Task exceeds total temporary storage budget");
        while (true)
        {
            token.ThrowIfCancellationRequested(); Task waiting;
            lock (gate)
            {
                if (size <= maximum - used) { used += size; return new Reservation(this, size); }
                waiting = changed.Task;
            }
            await waiting.WaitAsync(token);
        }
    }
    private void Release(long size)
    {
        TaskCompletionSource signal;
        lock (gate) { used -= size; signal = changed; changed = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        signal.TrySetResult();
    }
    private sealed class Reservation(Budget budget, long size) : IDisposable
    {
        private long bytes = size;
        public void Dispose() => budget.Release(Interlocked.Exchange(ref bytes, 0));
    }
}
