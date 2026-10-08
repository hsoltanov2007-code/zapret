using System.Globalization;
using Northpass.Models;
namespace Northpass.Engine.Native;

public static class NativeMetrics
{
    public static EnginePerformance Parse(string line)
    {
        if (line.Length > 2048 || !line.StartsWith("NORTHPASS_METRICS ", StringComparison.Ordinal)) throw new InvalidDataException("Invalid native metrics frame.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in line["NORTHPASS_METRICS ".Length..].Split(' '))
        {
            var split = field.Split('=');
            if (split.Length != 2 || !values.TryAdd(split[0], split[1])) throw new InvalidDataException("Invalid/duplicate native metrics field.");
        }
        if (values.Count != 18 || !values.TryGetValue("protocol", out var protocol) || protocol != "2" ||
            !values.TryGetValue("kernel_loss_unknown", out var unknown) || unknown != "1") throw new InvalidDataException("Unsupported native metrics schema.");
        ulong Number(string key, ulong maximum = ulong.MaxValue) => values.TryGetValue(key, out var value) &&
            ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result <= maximum
                ? result : throw new InvalidDataException("Invalid native counter: " + key);
        double Measure(string key, double maximum = 1e15) => values.TryGetValue(key, out var value) &&
            double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var result) &&
            double.IsFinite(result) && result >= 0 && result <= maximum ? result : throw new InvalidDataException("Invalid native measurement: " + key);
        var capacity = Number("queue_capacity", 64); if (capacity == 0) throw new InvalidDataException("Invalid queue capacity.");
        return new(Measure("cpu_percent", 100), Number("memory_bytes"), Measure("packets_per_second"), Measure("latency_us"), Measure("max_latency_us"),
            Number("queue_size", capacity), capacity, Number("queue_peak", capacity), Number("captured"), Number("forwarded"), Number("dropped_known"),
            true, Number("backpressure"), Number("active_flows", 4096), Number("recoverable_errors"), Number("fatal_errors"), Number("auth_failures"));
    }
}
