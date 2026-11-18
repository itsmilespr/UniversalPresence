using System.Runtime.InteropServices;

namespace UniversalPresence.Infrastructure;

/// <summary>
/// The app is built as a GUI subsystem binary (no console window when launched from
/// Explorer). When it is started from a terminal in diagnostics mode we attach to the
/// parent console so Console.WriteLine works.
/// </summary>
internal static class ConsoleBootstrap
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    public static bool TryAttach()
    {
        if (GetConsoleWindow() != IntPtr.Zero)
        {
            return true;
        }

        if (!AttachConsole(AttachParentProcess))
        {
            return false;
        }

        try
        {
            var stdout = Console.OpenStandardOutput();
            Console.SetOut(new StreamWriter(stdout) { AutoFlush = true });

            var stderr = Console.OpenStandardError();
            Console.SetError(new StreamWriter(stderr) { AutoFlush = true });

            return true;
        }
        catch
        {
            return false;
        }
    }
}
