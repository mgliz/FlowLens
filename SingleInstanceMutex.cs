namespace FlowLens;

internal sealed class SingleInstanceMutex : IDisposable
{
    private Mutex? _mutex;
    private bool _ownsMutex;

    private SingleInstanceMutex(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public bool OwnsMutex => _ownsMutex && _mutex is not null;

    public static SingleInstanceMutex Acquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        return new SingleInstanceMutex(mutex, createdNew);
    }

    public void Dispose()
    {
        var mutex = _mutex;
        if (mutex is null)
        {
            return;
        }

        _mutex = null;
        try
        {
            if (_ownsMutex)
            {
                _ownsMutex = false;
                mutex.ReleaseMutex();
            }
        }
        finally
        {
            mutex.Dispose();
        }
    }
}
