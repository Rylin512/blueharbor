namespace BlueHarbor.Services;

/// <summary>
/// Keeps one receiver process per user session. A second launch signals the
/// first one to show its window instead of starting a competing A2DP sink.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\BlueHarbor.Receiver.SingleInstance";
    private const string EventName = @"Local\BlueHarbor.Receiver.Show";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle _waitHandle;
    private bool _disposed;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsFirstInstance = createdNew;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        if (createdNew)
        {
            _waitHandle = ThreadPool.RegisterWaitForSingleObject(
                _showEvent,
                (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        else
        {
            _waitHandle = null!;
        }
    }

    public bool IsFirstInstance { get; }

    public event EventHandler? ShowRequested;

    public void SignalFirstInstance()
    {
        if (!IsFirstInstance)
        {
            try
            {
                _showEvent.Set();
            }
            catch
            {
                // The first instance may have exited between mutex check and signal.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _waitHandle?.Unregister(null);
        }
        catch
        {
            // Ignore.
        }
        try
        {
            if (IsFirstInstance)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch
        {
            // Ignore.
        }
        _mutex.Dispose();
        _showEvent.Dispose();
    }
}
