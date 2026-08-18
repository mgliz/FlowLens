namespace FlowLens;

internal sealed class HistorySaveCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly TrafficHistoryStore _trafficHistory;
    private readonly NetworkTrafficHistoryStore _networkHistory;
    private Task _saveTask = Task.CompletedTask;
    private bool _saveRequested;
    private bool _workerRunning;
    private bool _disposed;
    private string _lastError = string.Empty;

    public HistorySaveCoordinator(
        TrafficHistoryStore trafficHistory,
        NetworkTrafficHistoryStore networkHistory)
    {
        _trafficHistory = trafficHistory;
        _networkHistory = networkHistory;
    }

    public string LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    public void RequestSave()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _saveRequested = true;
            if (!_workerRunning)
            {
                _workerRunning = true;
                _saveTask = Task.Run(SaveLoop);
            }
        }
    }

    public void Flush()
    {
        RequestSave();

        while (true)
        {
            Task task;
            lock (_gate)
            {
                task = _saveTask;
            }

            task.GetAwaiter().GetResult();

            lock (_gate)
            {
                if (!_saveRequested && !_workerRunning)
                {
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        Task task;
        lock (_gate)
        {
            _disposed = true;
            task = _saveTask;
        }

        task.GetAwaiter().GetResult();
    }

    private void SaveLoop()
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_saveRequested)
                {
                    _workerRunning = false;
                    return;
                }

                _saveRequested = false;
            }

            try
            {
                _trafficHistory.Save();
                _networkHistory.Save();
                SetLastError(string.Empty);
            }
            catch (Exception ex)
            {
                SetLastError(ex.Message);
            }
        }
    }

    private void SetLastError(string message)
    {
        lock (_gate)
        {
            _lastError = message;
        }
    }
}
