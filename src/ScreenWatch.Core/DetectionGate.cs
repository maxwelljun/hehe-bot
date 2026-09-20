namespace ScreenWatch.Core;

public enum DetectionState { Waiting, Confirming, CoolingDown, Latched }

public readonly record struct DetectionResult(bool ShouldNotify, DetectionState State, int ConsecutiveMatches);

/// <summary>One notification per appearance; rearm requires consecutive misses.</summary>
public sealed class DetectionGate
{
    private readonly int _confirmFrames;
    private readonly int _rearmFrames;
    private readonly TimeSpan _cooldown;
    private int _matches;
    private int _misses;
    private bool _latched;
    private TimeSpan? _lastNotification;
    private TimeSpan _lastObservation;

    public DetectionGate(MonitorSettings settings)
    {
        settings.Validate();
        _confirmFrames = settings.ConfirmFrames;
        _rearmFrames = settings.RearmFrames;
        _cooldown = TimeSpan.FromSeconds(settings.CooldownSeconds);
    }

    public DetectionResult Observe(bool isMatch, TimeSpan elapsed)
    {
        if (elapsed < _lastObservation || elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed), "必须使用单调递增的时间。");
        _lastObservation = elapsed;

        if (!isMatch)
        {
            _matches = 0;
            _misses = Math.Min(_misses + 1, _rearmFrames);
            if (_misses >= _rearmFrames) _latched = false;
            return new(false, _latched ? DetectionState.Latched : DetectionState.Waiting, 0);
        }

        _misses = 0;
        _matches = Math.Min(_matches + 1, _confirmFrames);
        if (_latched) return new(false, DetectionState.Latched, _matches);
        if (_matches < _confirmFrames) return new(false, DetectionState.Confirming, _matches);
        if (_lastNotification is { } last && elapsed - last < _cooldown)
            return new(false, DetectionState.CoolingDown, _matches);

        _latched = true;
        _lastNotification = elapsed;
        return new(true, DetectionState.Latched, _matches);
    }
}
