namespace FlowLens;

internal sealed class CaptureContinuityTracker
{
    private readonly object _gate = new();
    private long _epoch;
    private bool _active;
    private bool _endedNormally;
    private SampleBoundary? _previous;

    public void CaptureStarted()
    {
        lock (_gate)
        {
            _epoch++;
            _active = true;
            _endedNormally = false;
        }
    }

    public void CaptureStopped(bool expected)
    {
        lock (_gate)
        {
            _endedNormally = expected && _active;
            _active = false;
            if (!_endedNormally)
            {
                _epoch++;
            }
        }
    }

    public void ResetSampleBaseline()
    {
        lock (_gate)
        {
            _previous = null;
        }
    }

    public CaptureBoundary BeginSample()
    {
        lock (_gate)
        {
            return new CaptureBoundary(_epoch, _active);
        }
    }

    public bool CompleteSample(CaptureBoundary before, NetworkTrafficSnapshot network)
    {
        lock (_gate)
        {
            var captureValid = before.Epoch == _epoch && before.Active && _active;
            var currentValid = captureValid && network.IsAvailable && network.IsAttributionAvailable;
            var complete = currentValid
                && _previous is { Valid: true } previous
                && previous.CaptureEpoch == _epoch
                && previous.AdapterEpoch == network.AdapterEpoch
                && previous.InterfaceId.Equals(network.InterfaceId, StringComparison.OrdinalIgnoreCase);
            _previous = new SampleBoundary(_epoch, network.AdapterEpoch, network.InterfaceId, currentValid);
            return complete;
        }
    }

    public bool IsExpectedFinalBoundary(CaptureBoundary boundary)
    {
        lock (_gate)
        {
            return boundary.Active && boundary.Epoch == _epoch && !_active && _endedNormally;
        }
    }

    internal readonly record struct CaptureBoundary(long Epoch, bool Active);
    private sealed record SampleBoundary(long CaptureEpoch, long AdapterEpoch, string InterfaceId, bool Valid);
}
