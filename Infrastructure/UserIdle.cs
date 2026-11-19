using System.Runtime.InteropServices;

namespace UniversalPresence.Infrastructure;

/// <summary>
/// Detecta si el usuario está ausente: tiempo desde la última entrada de teclado o ratón y
/// si la sesión está bloqueada (pantalla de bloqueo, cambio de usuario o UAC).
///
/// No hace falta una ventana: <c>OpenInputDesktop</c> solo funciona cuando el escritorio de
/// entrada está activo, así que un fallo es una señal fiable de bloqueo.
/// </summary>
public static class UserIdle
{
    private const uint DesktopSwitchDesktop = 0x0100;
    private const uint DesktopReadObjects = 0x0001;
    private const uint DesktopEnumerate = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    /// <summary>Tiempo desde la última pulsación de tecla o movimiento del ratón.</summary>
    public static TimeSpan IdleTime
    {
        get
        {
            try
            {
                var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
                if (!GetLastInputInfo(ref info))
                {
                    return TimeSpan.Zero;
                }

                // GetLastInputInfo usa GetTickCount (32 bits): la resta sin signo evita el
                // salto cada 49 días.
                var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
                return TimeSpan.FromMilliseconds(elapsed);
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }
    }

    /// <summary>True cuando la sesión está bloqueada o en la pantalla segura.</summary>
    public static bool IsSessionLocked()
    {
        var desktop = IntPtr.Zero;

        try
        {
            desktop = OpenInputDesktop(
                flags: 0,
                inherit: false,
                desiredAccess: DesktopSwitchDesktop | DesktopReadObjects | DesktopEnumerate);

            if (desktop == IntPtr.Zero)
            {
                // Acceso denegado = pantalla segura (bloqueo, UAC, cambio de usuario).
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (desktop != IntPtr.Zero)
            {
                try { CloseDesktop(desktop); } catch { }
            }
        }
    }

    /// <summary>
    /// True cuando conviene ocultar la presencia: sesión bloqueada o sin actividad durante
    /// <paramref name="idleSeconds"/> segundos (0 desactiva el criterio de inactividad).
    /// </summary>
    public static bool IsAway(int idleSeconds)
    {
        if (IsSessionLocked())
        {
            return true;
        }

        return idleSeconds > 0 && IdleTime >= TimeSpan.FromSeconds(idleSeconds);
    }
}
