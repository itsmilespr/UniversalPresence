using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UniversalPresence.Infrastructure;

namespace UniversalPresence.Core;

/// <summary>
/// Recursos del proceso: recolección de memoria (WinRT solo libera referencias en gen2) y
/// detección de una app a pantalla completa para el modo juego.
/// </summary>
public sealed partial class PresenceEngine : IAsyncDisposable, IDisposable
{
    private DateTimeOffset _nextMemoryCheck = DateTimeOffset.MinValue;

    /// <summary>
    /// Las referencias WinRT del bucle (sesiones, metadatos, miniaturas) solo se liberan en
    /// un GC de gen2, y con un heap gestionado de ~2 MB puede no llegar nunca: la memoria
    /// privada subía sin parar (480 MB tras unos minutos). Se comprueba cada 5 s y, si
    /// supera 80 MB, se fuerza una recolección completa en segundo plano. Así el pico se
    /// queda cerca de 90 MB en lugar de crecer hasta los 200 MB.
    /// </summary>
    private void MaybeTrimMemory()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextMemoryCheck)
        {
            return;
        }

        _nextMemoryCheck = now.AddSeconds(5);

        long privateBytes;
        try
        {
            using var process = Process.GetCurrentProcess();
            privateBytes = process.PrivateMemorySize64;
        }
        catch
        {
            return;
        }

        if (privateBytes < 80L * 1024 * 1024)
        {
            return;
        }

        var gc = GC.GetGCMemoryInfo();
        _log.Debug(
            $"Memoria privada {privateBytes / (1024 * 1024)} MB " +
            $"(GC comprometido {gc.TotalCommittedBytes / (1024 * 1024)} MB, " +
            $"heap vivo {GC.GetTotalMemory(false) / (1024 * 1024)} MB); liberando referencias WinRT.");

        _ = Task.Run(() =>
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
            }
            catch
            {
                // Nunca tumbar el bucle por una recolección.
            }
        });
    }
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private static bool IsForegroundFullscreen()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || IsIconic(hwnd))
            {
                return false;
            }

            // Never treat the desktop / shell / our own window as a game.
            var className = GetClassName(hwnd);
            if (className is "Progman" or "WorkerW" or "Shell_TrayWnd")
            {
                return false;
            }

            if (!GetWindowRect(hwnd, out var r))
            {
                return false;
            }

            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return false;
            }

            var m = info.rcMonitor;
            // Fullscreen means covering the monitor bounds (allowing a couple of pixels
            // for window borders / rounded corners) and not merely a maximized window.
            return r.Left <= m.Left + 2
                && r.Top <= m.Top + 2
                && r.Right >= m.Right - 2
                && r.Bottom >= m.Bottom - 2
                && (r.Right - r.Left) >= (m.Right - m.Left) - 4
                && (r.Bottom - r.Top) >= (m.Bottom - m.Top) - 4;
        }
        catch
        {
            return false;
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder lpClassName, int nMaxCount);

    private static string GetClassName(IntPtr hwnd)
    {
        try
        {
            var builder = new StringBuilder(256);
            return GetClassName(hwnd, builder, builder.Capacity) > 0 ? builder.ToString() : "";
        }
        catch
        {
            return "";
        }
    }
}
