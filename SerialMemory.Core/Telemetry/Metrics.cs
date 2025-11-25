 
using System.Diagnostics.Metrics;

namespace SerialMemory.Core.Telemetry;

public static class Metrics
{
    public const string MeterName = "SerialMemory";
    public static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> RabbitPublished = Meter.CreateCounter<long>("rabbit.published");
    public static readonly Counter<long> RabbitConsumed  = Meter.CreateCounter<long>("rabbit.consumed");
    public static readonly Histogram<double> RedisLatencyMs = Meter.CreateHistogram<double>("redis.latency.ms",
        unit: "ms", description: "Redis op latency");
}