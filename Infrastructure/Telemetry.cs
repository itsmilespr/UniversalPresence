using System.Diagnostics;
using UniversalPresence.Configuration;

namespace UniversalPresence.Infrastructure;

/// <summary>
/// Lightweight, opt-in-ish telemetry written only to the local log. It helps diagnose
/// memory/CPU regressions without sending anything off the machine.
/// </summary>
public static class Telemetry
{
    private static DateTimeOffset _next = DateTimeOffset.MinValue;
    private static TimeSpan _lastCpu = TimeSpan.Zero;
    private static DateTimeOffset _lastCpuAt = DateTimeOffset.MinValue;

    public static void MaybeLog(Log log)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _next)
        {
            return;
        }

        _next = now.AddMinutes(5);

        try
        {
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var cpuPercent = 0.0;

            if (_lastCpuAt != DateTimeOffset.MinValue)
            {
                var elapsed = (now - _lastCpuAt).TotalMilliseconds;
                if (elapsed > 0)
                {
                    cpuPercent = (cpu - _lastCpu).TotalMilliseconds / elapsed * 100.0 / Environment.ProcessorCount;
                }
            }

            _lastCpu = cpu;
            _lastCpuAt = now;

            log.Info(
                $"Telemetry: private {process.PrivateMemorySize64 / 1048576} MB, " +
                $"working {process.WorkingSet64 / 1048576} MB, " +
                $"managed {GC.GetTotalMemory(false) / 1048576} MB, " +
                $"CPU {cpuPercent:0.0}%, " +
                $"threads {process.Threads.Count}, " +
                $"GCMemoryInfo committed {GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576} MB");
        }
        catch
        {
            // Telemetry must never affect playback.
        }
    }
}