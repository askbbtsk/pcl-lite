using System;
using System.Collections.Generic;
using System.Linq;

namespace PCL.Core.Utils;

/// <summary>Progress follows completed work, never elapsed time or repeated child-task resets.</summary>
public sealed class StagedOperationProgress
{
    private readonly object _gate = new();
    private readonly double[] _weights;
    private readonly double[] _fractions;
    private readonly double _total;
    private int _current = -1;

    public StagedOperationProgress(params double[] weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Length == 0 || weights.Any(value => !double.IsFinite(value) || value <= 0))
            throw new ArgumentException("Stage weights must be finite and positive.", nameof(weights));
        _weights = weights.ToArray();
        _fractions = new double[weights.Length];
        _total = _weights.Sum();
        if (!double.IsFinite(_total)) throw new ArgumentException("Stage weights are too large.", nameof(weights));
    }

    public void Begin(int stage)
    {
        lock (_gate)
        {
            if (stage < 0 || stage >= _weights.Length || stage < _current || stage > _current + 1)
                throw new ArgumentOutOfRangeException(nameof(stage));
            if (stage > _current && _current >= 0 && _fractions[_current] < 1)
                throw new InvalidOperationException("Complete the current stage before beginning another.");
            _current = stage;
        }
    }

    public void Report(int stage, double fraction)
    {
        if (!double.IsFinite(fraction)) throw new ArgumentOutOfRangeException(nameof(fraction));
        if (stage < 0 || stage >= _weights.Length) throw new ArgumentOutOfRangeException(nameof(stage));
        lock (_gate)
        {
            if (stage != _current) return; // A delayed child callback cannot reset a later stage.
            _fractions[stage] = Math.Max(_fractions[stage], Math.Clamp(fraction, 0, 1));
        }
    }

    public void Complete(int stage) => Report(stage, 1);

    public double Overall
    {
        get
        {
            lock (_gate)
                return _weights.Select((weight, index) => weight * _fractions[index]).Sum() / _total;
        }
    }
}
