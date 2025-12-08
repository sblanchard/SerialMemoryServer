using System.Collections.Concurrent;
using System.Diagnostics;

namespace SerialMemory.Core.Performance;

/// <summary>
/// High-resolution latency measurement with histogram buckets.
/// </summary>
public sealed class LatencyHistogram
{
    private readonly long[] _buckets;
    private readonly double[] _bucketBoundaries;
    private long _count;
    private long _totalTicks;
    private long _minTicks = long.MaxValue;
    private long _maxTicks;
    private readonly object _lock = new();

    // Default boundaries in milliseconds: 1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000
    private static readonly double[] DefaultBoundaries = [1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000];

    public LatencyHistogram(double[]? boundariesMs = null)
    {
        _bucketBoundaries = boundariesMs ?? DefaultBoundaries;
        _buckets = new long[_bucketBoundaries.Length + 1];
    }

    public void Record(TimeSpan duration)
    {
        var ms = duration.TotalMilliseconds;
        var ticks = duration.Ticks;

        lock (_lock)
        {
            _count++;
            _totalTicks += ticks;

            if (ticks < _minTicks) _minTicks = ticks;
            if (ticks > _maxTicks) _maxTicks = ticks;

            // Find bucket
            var bucketIndex = _bucketBoundaries.Length;
            for (int i = 0; i < _bucketBoundaries.Length; i++)
            {
                if (ms <= _bucketBoundaries[i])
                {
                    bucketIndex = i;
                    break;
                }
            }
            _buckets[bucketIndex]++;
        }
    }

    public LatencySnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var bucketCounts = new Dictionary<string, long>();
            for (int i = 0; i < _bucketBoundaries.Length; i++)
            {
                var label = i == 0 ? $"≤{_bucketBoundaries[i]}ms" : $"≤{_bucketBoundaries[i]}ms";
                bucketCounts[label] = _buckets[i];
            }
            bucketCounts[$">{_bucketBoundaries[^1]}ms"] = _buckets[^1];

            return new LatencySnapshot
            {
                Count = _count,
                MinMs = _count > 0 ? TimeSpan.FromTicks(_minTicks).TotalMilliseconds : 0,
                MaxMs = _count > 0 ? TimeSpan.FromTicks(_maxTicks).TotalMilliseconds : 0,
                AvgMs = _count > 0 ? TimeSpan.FromTicks(_totalTicks / _count).TotalMilliseconds : 0,
                TotalMs = TimeSpan.FromTicks(_totalTicks).TotalMilliseconds,
                Buckets = bucketCounts,
                P50Ms = CalculatePercentile(50),
                P90Ms = CalculatePercentile(90),
                P95Ms = CalculatePercentile(95),
                P99Ms = CalculatePercentile(99)
            };
        }
    }

    private double CalculatePercentile(int percentile)
    {
        if (_count == 0) return 0;

        var targetCount = (long)Math.Ceiling(_count * percentile / 100.0);
        long cumulative = 0;

        for (int i = 0; i < _buckets.Length; i++)
        {
            cumulative += _buckets[i];
            if (cumulative >= targetCount)
            {
                return i < _bucketBoundaries.Length ? _bucketBoundaries[i] : _bucketBoundaries[^1] * 2;
            }
        }

        return _bucketBoundaries[^1] * 2;
    }

    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_buckets);
            _count = 0;
            _totalTicks = 0;
            _minTicks = long.MaxValue;
            _maxTicks = 0;
        }
    }
}

public sealed class LatencySnapshot
{
    public long Count { get; init; }
    public double MinMs { get; init; }
    public double MaxMs { get; init; }
    public double AvgMs { get; init; }
    public double TotalMs { get; init; }
    public double P50Ms { get; init; }
    public double P90Ms { get; init; }
    public double P95Ms { get; init; }
    public double P99Ms { get; init; }
    public Dictionary<string, long> Buckets { get; init; } = new();
}

/// <summary>
/// Tracks operation metrics by name.
/// </summary>
public sealed class OperationMetrics
{
    private readonly ConcurrentDictionary<string, LatencyHistogram> _histograms = new();
    private readonly ConcurrentDictionary<string, OperationCounter> _counters = new();

    public LatencyHistogram GetHistogram(string operationName)
    {
        return _histograms.GetOrAdd(operationName, _ => new LatencyHistogram());
    }

    public OperationCounter GetCounter(string operationName)
    {
        return _counters.GetOrAdd(operationName, _ => new OperationCounter());
    }

    public Dictionary<string, LatencySnapshot> GetAllLatencySnapshots()
    {
        return _histograms.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.GetSnapshot());
    }

    public Dictionary<string, CounterSnapshot> GetAllCounterSnapshots()
    {
        return _counters.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.GetSnapshot());
    }

    public void Reset()
    {
        foreach (var h in _histograms.Values) h.Reset();
        foreach (var c in _counters.Values) c.Reset();
    }
}

public sealed class OperationCounter
{
    private long _success;
    private long _failure;
    private long _total;

    public void RecordSuccess() { Interlocked.Increment(ref _success); Interlocked.Increment(ref _total); }
    public void RecordFailure() { Interlocked.Increment(ref _failure); Interlocked.Increment(ref _total); }

    public CounterSnapshot GetSnapshot() => new()
    {
        Success = Interlocked.Read(ref _success),
        Failure = Interlocked.Read(ref _failure),
        Total = Interlocked.Read(ref _total)
    };

    public void Reset()
    {
        Interlocked.Exchange(ref _success, 0);
        Interlocked.Exchange(ref _failure, 0);
        Interlocked.Exchange(ref _total, 0);
    }
}

public sealed class CounterSnapshot
{
    public long Success { get; init; }
    public long Failure { get; init; }
    public long Total { get; init; }
    public double SuccessRate => Total > 0 ? (double)Success / Total : 0;
}

/// <summary>
/// Detects and tracks slow operations.
/// </summary>
public sealed class SlowOperationDetector
{
    private readonly ConcurrentDictionary<string, TimeSpan> _thresholds = new();
    private readonly ConcurrentQueue<SlowOperationRecord> _recentSlowOps = new();
    private readonly int _maxRecords;
    private readonly TimeSpan _defaultThreshold;

    public SlowOperationDetector(TimeSpan? defaultThreshold = null, int maxRecords = 1000)
    {
        _defaultThreshold = defaultThreshold ?? TimeSpan.FromMilliseconds(100);
        _maxRecords = maxRecords;
    }

    public void SetThreshold(string operationName, TimeSpan threshold)
    {
        _thresholds[operationName] = threshold;
    }

    public TimeSpan GetThreshold(string operationName)
    {
        return _thresholds.TryGetValue(operationName, out var threshold) ? threshold : _defaultThreshold;
    }

    public bool RecordIfSlow(string operationName, TimeSpan duration, string? context = null)
    {
        var threshold = GetThreshold(operationName);
        if (duration <= threshold) return false;

        var record = new SlowOperationRecord
        {
            OperationName = operationName,
            Duration = duration,
            Threshold = threshold,
            Timestamp = DateTimeOffset.UtcNow,
            Context = context,
            SlowFactor = duration.TotalMilliseconds / threshold.TotalMilliseconds
        };

        _recentSlowOps.Enqueue(record);

        // Trim if needed
        while (_recentSlowOps.Count > _maxRecords)
        {
            _recentSlowOps.TryDequeue(out _);
        }

        return true;
    }

    public IReadOnlyList<SlowOperationRecord> GetRecentSlowOperations(int limit = 100)
    {
        return _recentSlowOps.Reverse().Take(limit).ToList();
    }

    public Dictionary<string, SlowOperationSummary> GetSlowOperationSummary()
    {
        return _recentSlowOps
            .GroupBy(r => r.OperationName)
            .ToDictionary(
                g => g.Key,
                g => new SlowOperationSummary
                {
                    Count = g.Count(),
                    AvgDurationMs = g.Average(r => r.Duration.TotalMilliseconds),
                    MaxDurationMs = g.Max(r => r.Duration.TotalMilliseconds),
                    AvgSlowFactor = g.Average(r => r.SlowFactor),
                    ThresholdMs = g.First().Threshold.TotalMilliseconds
                });
    }

    public void Clear()
    {
        while (_recentSlowOps.TryDequeue(out _)) { }
    }
}

public sealed class SlowOperationRecord
{
    public required string OperationName { get; init; }
    public required TimeSpan Duration { get; init; }
    public required TimeSpan Threshold { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? Context { get; init; }
    public double SlowFactor { get; init; }
}

public sealed class SlowOperationSummary
{
    public int Count { get; init; }
    public double AvgDurationMs { get; init; }
    public double MaxDurationMs { get; init; }
    public double AvgSlowFactor { get; init; }
    public double ThresholdMs { get; init; }
}

/// <summary>
/// Scoped measurement helper - records latency on dispose.
/// </summary>
public readonly struct OperationScope : IDisposable
{
    private readonly LatencyHistogram _histogram;
    private readonly OperationCounter _counter;
    private readonly SlowOperationDetector _slowDetector;
    private readonly string _operationName;
    private readonly long _startTicks;
    private readonly string? _context;

    public OperationScope(
        LatencyHistogram histogram,
        OperationCounter counter,
        SlowOperationDetector slowDetector,
        string operationName,
        string? context = null)
    {
        _histogram = histogram;
        _counter = counter;
        _slowDetector = slowDetector;
        _operationName = operationName;
        _context = context;
        _startTicks = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        var elapsed = Stopwatch.GetElapsedTime(_startTicks);
        _histogram.Record(elapsed);
        _counter.RecordSuccess();
        _slowDetector.RecordIfSlow(_operationName, elapsed, _context);
    }
}

/// <summary>
/// Central performance registry.
/// </summary>
public sealed class PerformanceRegistry
{
    public OperationMetrics Metrics { get; } = new();
    public SlowOperationDetector SlowDetector { get; }
    public CacheMetrics CacheMetrics { get; } = new();

    private readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;

    public PerformanceRegistry(TimeSpan? slowOperationThreshold = null)
    {
        SlowDetector = new SlowOperationDetector(slowOperationThreshold);
    }

    public OperationScope MeasureOperation(
        string operationName,
        string? context = null)
    {
        return new OperationScope(
            Metrics.GetHistogram(operationName),
            Metrics.GetCounter(operationName),
            SlowDetector,
            operationName,
            context);
    }

    public async Task<T> MeasureAsync<T>(
        string operationName,
        Func<Task<T>> operation,
        string? context = null)
    {
        var histogram = Metrics.GetHistogram(operationName);
        var counter = Metrics.GetCounter(operationName);
        var sw = Stopwatch.StartNew();

        try
        {
            var result = await operation();
            sw.Stop();
            histogram.Record(sw.Elapsed);
            counter.RecordSuccess();
            SlowDetector.RecordIfSlow(operationName, sw.Elapsed, context);
            return result;
        }
        catch
        {
            sw.Stop();
            histogram.Record(sw.Elapsed);
            counter.RecordFailure();
            SlowDetector.RecordIfSlow(operationName, sw.Elapsed, context);
            throw;
        }
    }

    public PerformanceSnapshot GetSnapshot()
    {
        return new PerformanceSnapshot
        {
            Timestamp = DateTimeOffset.UtcNow,
            UptimeSeconds = (DateTimeOffset.UtcNow - _startTime).TotalSeconds,
            Latencies = Metrics.GetAllLatencySnapshots(),
            Counters = Metrics.GetAllCounterSnapshots(),
            SlowOperations = SlowDetector.GetSlowOperationSummary(),
            RecentSlowOps = SlowDetector.GetRecentSlowOperations(50),
            Cache = CacheMetrics.GetSnapshot()
        };
    }

    public void Reset()
    {
        Metrics.Reset();
        SlowDetector.Clear();
        CacheMetrics.Reset();
    }
}

public sealed class PerformanceSnapshot
{
    public DateTimeOffset Timestamp { get; init; }
    public double UptimeSeconds { get; init; }
    public Dictionary<string, LatencySnapshot> Latencies { get; init; } = new();
    public Dictionary<string, CounterSnapshot> Counters { get; init; } = new();
    public Dictionary<string, SlowOperationSummary> SlowOperations { get; init; } = new();
    public IReadOnlyList<SlowOperationRecord> RecentSlowOps { get; init; } = [];
    public CacheSnapshot Cache { get; init; } = new();
}

/// <summary>
/// Cache metrics tracking.
/// </summary>
public sealed class CacheMetrics
{
    private readonly ConcurrentDictionary<string, CacheLayerMetrics> _layers = new();

    public CacheLayerMetrics GetLayer(string layerName)
    {
        return _layers.GetOrAdd(layerName, _ => new CacheLayerMetrics());
    }

    public CacheSnapshot GetSnapshot()
    {
        return new CacheSnapshot
        {
            Layers = _layers.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.GetSnapshot())
        };
    }

    public void Reset()
    {
        foreach (var layer in _layers.Values)
        {
            layer.Reset();
        }
    }
}

public sealed class CacheLayerMetrics
{
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _writes;
    private long _currentSize;

    public void RecordHit() => Interlocked.Increment(ref _hits);
    public void RecordMiss() => Interlocked.Increment(ref _misses);
    public void RecordEviction() => Interlocked.Increment(ref _evictions);
    public void RecordWrite() => Interlocked.Increment(ref _writes);
    public void SetSize(long size) => Interlocked.Exchange(ref _currentSize, size);

    public CacheLayerSnapshot GetSnapshot() => new()
    {
        Hits = Interlocked.Read(ref _hits),
        Misses = Interlocked.Read(ref _misses),
        Evictions = Interlocked.Read(ref _evictions),
        Writes = Interlocked.Read(ref _writes),
        CurrentSize = Interlocked.Read(ref _currentSize),
        HitRate = CalculateHitRate()
    };

    private double CalculateHitRate()
    {
        var hits = Interlocked.Read(ref _hits);
        var misses = Interlocked.Read(ref _misses);
        var total = hits + misses;
        return total > 0 ? (double)hits / total : 0;
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
        Interlocked.Exchange(ref _evictions, 0);
        Interlocked.Exchange(ref _writes, 0);
    }
}

public sealed class CacheLayerSnapshot
{
    public long Hits { get; init; }
    public long Misses { get; init; }
    public long Evictions { get; init; }
    public long Writes { get; init; }
    public long CurrentSize { get; init; }
    public double HitRate { get; init; }
}

public sealed class CacheSnapshot
{
    public Dictionary<string, CacheLayerSnapshot> Layers { get; init; } = new();
}
