using System.Diagnostics;
using System.Text.RegularExpressions;
using UniversalPresence.Configuration;
using UniversalPresence.Infrastructure;
using UniversalPresence.Localization;

namespace UniversalPresence.Media;

public sealed record PlayerIdentity(string Name, string AppUserModelId, string? ExecutablePath, string Source)
{
    public string? FileName => string.IsNullOrWhiteSpace(ExecutablePath)
        ? null
        : Path.GetFileName(ExecutablePath);
}

/// <summary>
/// Turns an AppUserModelId into a human readable player name.
///
/// This is purely cosmetic: an unknown id never causes the session to be ignored.
/// Resolution order:
///   1. explicit user override (config)
///   2. Windows app registration (Software\Classes\AppUserModelId: nombre e IconUri)
///   3. Windows app registration through AppInfo (solo el nombre)
///   4. Firefox: el hash del acceso directo se rastrea hasta firefox.exe (TaskBarIDs)
///   5. a running process whose name matches the AUMID / executable
///   6. the AUMID itself, lightly prettified
/// </summary>
public sealed class PlayerIdentityResolver
{
    private static readonly TimeSpan AuthoritativeTtl = TimeSpan.FromMinutes(30);
    // Un AUMID que no está registrado no va a cambiar de nombre cada minuto: reintentar
    // cada 30 s solo gastaba objetos WinRT y enumeraba procesos sin necesidad.
    private static readonly TimeSpan GuessedTtl = TimeSpan.FromMinutes(5);
    private static readonly Regex PublisherHash = new("_[A-Za-z0-9]{13}$", RegexOptions.Compiled);
    private static readonly Regex TrailingInstanceId = new(@"[._][0-9A-Fa-f]{8}$", RegexOptions.Compiled);

    private readonly AppOptions _options;
    private readonly Log _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public PlayerIdentityResolver(AppOptions options, Log log)
    {
        _options = options;
        _log = log;
    }

    private readonly record struct CacheEntry(PlayerIdentity Identity, DateTimeOffset ExpiresAt);

    /// <summary>
    /// Extrae el icono del programa asociado al AUMID y lo guarda en disco.
    /// Primero intenta por ejecutable; si no hay, intenta por ventana del proceso.
    /// Devuelve la ruta del archivo .png o null si no se pudo obtener.
    /// </summary>
    public static string? GetProgramIconPath(string? appUserModelId, string? executablePath)
    {
        // 1. Por ejecutable.
        var path = ExtractIconFromExecutable(executablePath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        // 2. Por ventana del proceso (útil para Gatefold/foobar2000, que no exponen ejecutable).
        return ExtractIconFromProcessWindow(appUserModelId);
    }

    /// <summary>
    /// Compatibilidad con la firma antigua (solo ejecutable).
    /// </summary>
    public static string? GetProgramIconPath(string? executablePath) =>
        GetProgramIconPath(null, executablePath);

    private static string? ExtractIconFromExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return null;

        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UniversalPresence",
            "icons");
        Directory.CreateDirectory(cacheDir);

        var cacheFile = Path.Combine(cacheDir, $"{StableFileName(executablePath)}_v3.png");

        if (File.Exists(cacheFile))
            return cacheFile;

        try
        {
            using var bitmap = LoadBestIcon(executablePath);
            if (bitmap is not null)
            {
                using var normalized = NormalizeIcon(bitmap);
                normalized.Save(cacheFile, System.Drawing.Imaging.ImageFormat.Png);
                return cacheFile;
            }
        }
        catch
        {
            // Ignorar errores de extracción.
        }

        return null;
    }

    private static string? ExtractIconFromProcessWindow(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return null;
        }

        var candidates = BuildCandidates(appUserModelId);
        // Alias para Gatefold (componente de foobar2000): el proceso real es foobar2000.
        if (appUserModelId.Contains("gatefold", StringComparison.OrdinalIgnoreCase)
            || appUserModelId.Contains("foobar", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("foobar2000");
        }
        if (candidates.Count == 0)
        {
            return null;
        }

        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UniversalPresence",
            "icons");
        Directory.CreateDirectory(cacheDir);

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return null;
        }

        try
        {
            foreach (var process in processes)
            {
                string? processName = null;
                try { processName = process.ProcessName; }
                catch { continue; }

                bool match = candidates.Contains(processName, StringComparer.OrdinalIgnoreCase);
                IntPtr hwnd = process.MainWindowHandle;

                if (!match && hwnd != IntPtr.Zero)
                {
                    // Intentar por título de ventana.
                    string? title = null;
                    try { title = process.MainWindowTitle; }
                    catch { title = null; }
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        foreach (var c in candidates)
                        {
                            if (title.Contains(c, StringComparison.OrdinalIgnoreCase))
                            {
                                match = true;
                                break;
                            }
                        }
                    }
                }

                if (!match)
                {
                    continue;
                }

                // Intentar primero por ejecutable (más fiable).
                string? execPath = null;
                try { execPath = process.MainModule?.FileName; }
                catch { execPath = null; }
                if (!string.IsNullOrWhiteSpace(execPath) && File.Exists(execPath))
                {
                    var fromExec = ExtractIconFromExecutable(execPath);
                    if (!string.IsNullOrWhiteSpace(fromExec))
                    {
                        return fromExec;
                    }
                }

                if (hwnd == IntPtr.Zero)
                {
                    continue;
                }

                // Obtener el icono de la ventana via Win32.
                IntPtr hIcon = SendMessage(hwnd, WM_GETICON, new IntPtr(ICON_BIG), IntPtr.Zero);
                if (hIcon == IntPtr.Zero)
                {
                    hIcon = SendMessage(hwnd, WM_GETICON, new IntPtr(ICON_SMALL2), IntPtr.Zero);
                }
                if (hIcon == IntPtr.Zero)
                {
                    hIcon = GetClassLongPtr(hwnd, GCL_HICON);
                }
                if (hIcon == IntPtr.Zero)
                {
                    continue;
                }

                var cacheFile = Path.Combine(cacheDir, $"{StableFileName(appUserModelId + "|" + processName)}_v3.png");
                if (File.Exists(cacheFile))
                {
                    return cacheFile;
                }

                try
                {
                    using var icon = System.Drawing.Icon.FromHandle(hIcon);
                    using var bitmap = icon.ToBitmap();
                    using var normalized = NormalizeIcon(bitmap);
                    normalized.Save(cacheFile, System.Drawing.Imaging.ImageFormat.Png);
                    return cacheFile;
                }
                catch
                {
                    // Ignorar.
                }
            }
        }
        finally
        {
            foreach (var p in processes)
            {
                try { p.Dispose(); } catch { }
            }
        }

        return null;
    }

    /// <summary>
    /// Icono del .exe en la resolución más alta que traiga el propio archivo.
    ///
    /// <c>Icon.ExtractAssociatedIcon</c> devuelve siempre 32×32 aunque el .exe tenga
    /// iconos de 256×256; ampliar ese bitmap (como se hacía antes, con vecino más
    /// cercano) es lo que dejaba el icono del reproductor borroso en Discord.
    /// </summary>
    private static System.Drawing.Bitmap? LoadBestIcon(string path)
    {
        var handle = ExtractIconAtSize(path, 128);
        if (handle != IntPtr.Zero)
        {
            try
            {
                using var icon = System.Drawing.Icon.FromHandle(handle);
                return icon.ToBitmap();
            }
            catch
            {
                // Icono corrupto: se prueba el asociado.
            }
            finally
            {
                DestroyIcon(handle);
            }
        }

        try
        {
            using var associated = System.Drawing.Icon.ExtractAssociatedIcon(path);
            return associated?.ToBitmap();
        }
        catch
        {
            return null;
        }
    }

    private static IntPtr ExtractIconAtSize(string path, int size)
    {
        try
        {
            var handles = new IntPtr[1];
            var ids = new uint[1];
            var count = PrivateExtractIcons(path, 0, size, size, handles, ids, 1, 0);
            return count > 0 && handles[0] != IntPtr.Zero ? handles[0] : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Deja el icono en al menos 256×256 con interpolación de calidad.</summary>
    private static System.Drawing.Bitmap NormalizeIcon(System.Drawing.Bitmap original)
    {
        const int minDimension = 128;

        if (original.Width >= minDimension && original.Height >= minDimension)
        {
            return new System.Drawing.Bitmap(original);
        }

        var scale = Math.Max(
            (double)minDimension / original.Width,
            (double)minDimension / original.Height);
        var newWidth = (int)Math.Ceiling(original.Width * scale);
        var newHeight = (int)Math.Ceiling(original.Height * scale);

        var resized = new System.Drawing.Bitmap(
            newWidth,
            newHeight,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(resized))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.DrawImage(original, 0, 0, newWidth, newHeight);
        }
        return resized;
    }

    /// <summary>Nombre de archivo estable: GetHashCode cambia en cada ejecución en .NET.</summary>
    private static string StableFileName(string value) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..12];

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(
        string szFileName,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        IntPtr[] phicon,
        uint[] piconid,
        uint nIcons,
        uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const int WM_GETICON = 0x007F;
    private const int ICON_BIG = 1;
    private const int ICON_SMALL2 = 2;
    private const int GCL_HICON = -14;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHLoadIndirectString(
        string source,
        System.Text.StringBuilder buffer,
        int bufferLength,
        IntPtr reserved);

    public PlayerIdentity Resolve(string? appUserModelId)
    {
        var aumid = (appUserModelId ?? string.Empty).Trim();

        lock (_gate)
        {
            if (_cache.TryGetValue(aumid, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return entry.Identity;
            }
        }

        var identity = ResolveCore(aumid);

        lock (_gate)
        {
            var ttl = identity.Source is "appinfo" or "override" or "registry" ? AuthoritativeTtl : GuessedTtl;
            _cache[aumid] = new CacheEntry(identity, DateTimeOffset.UtcNow + ttl);
        }

        _log.Debug($"Identidad '{identity.Name}' para AUMID '{aumid}' (origen: {identity.Source}).");
        return identity;
    }

    private PlayerIdentity ResolveCore(string aumid)
    {
        var text = PresenceText.For(_options.Language);

        if (aumid.Length == 0)
        {
            return new PlayerIdentity(text.UnknownPlayer, aumid, null, "empty");
        }

        var overridden = _options.ResolvePlayerOverride(aumid);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return new PlayerIdentity(overridden!, aumid, null, "override");
        }

        if (TryGetRegisteredAppIdentity(aumid, out var registeredIdentity))
        {
            return registeredIdentity;
        }

        if (TryResolveFirefoxExecutable(aumid, out var firefoxPath))
        {
            return new PlayerIdentity(
                FileDescription(firefoxPath) ?? "Firefox",
                aumid,
                firefoxPath,
                "firefox");
        }

        if (TryFindProcess(aumid, out var processName, out var executablePath) && !string.IsNullOrWhiteSpace(processName))
        {
            return new PlayerIdentity(processName!, aumid, executablePath, "process");
        }

        return new PlayerIdentity(Prettify(aumid, text.Player), aumid, null, "aumid");
    }

    /// <summary>
    /// Identidad que Windows tiene registrada para el AUMID.
    ///
    /// Algunos reproductores publican un identificador opaco (el hash del acceso directo,
    /// como Firefox con «308046B0AF4A39CB»): no hay forma de deducir de ahí ni el nombre ni
    /// el icono. Windows sí los guarda en HKCU\Software\Classes\AppUserModelId\&lt;id&gt;,
    /// así que se leen de ahí antes de intentar nada más.
    /// </summary>
    private static bool TryGetRegisteredAppIdentity(string aumid, out PlayerIdentity identity)
    {
        identity = default!;

        using var key = OpenAppUserModelIdKey(aumid);
        if (key is null)
        {
            return false;
        }

        var name = ResolveIndirectString(key.GetValue("DisplayName") as string);
        var executablePath = ParseIconUri(key.GetValue("IconUri") as string);

        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = FileDescription(executablePath);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        identity = new PlayerIdentity(name!, aumid, executablePath, "registry");
        return true;
    }

    private static Microsoft.Win32.RegistryKey? OpenAppUserModelIdKey(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return null;
        }

        foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                var key = root.OpenSubKey($@"Software\Classes\AppUserModelId\{aumid}");
                if (key is not null)
                {
                    return key;
                }
            }
            catch
            {
                // Un AUMID con caracteres raros o sin permisos: se sigue con el siguiente.
            }
        }

        return null;
    }

    /// <summary>
    /// El registro guarda el nombre y el icono como cadenas indirectas («@{...}»).
    /// SHLoadIndirectString las convierte en el texto y la ruta reales.
    /// </summary>
    private static string? ResolveIndirectString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();

        if (!value.StartsWith("@", StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            if (SHLoadIndirectString(value, buffer, buffer.Capacity, IntPtr.Zero) == 0)
            {
                var resolved = buffer.ToString().Trim();
                return resolved.Length == 0 ? null : resolved;
            }
        }
        catch
        {
            // Sin shlwapi no queda nada legible: se ignora esta vía.
        }

        return null;
    }

    /// <summary>
    /// IconUri puede ser una ruta («C:\...\firefox.exe») o una referencia con índice
    /// («C:\...\app.exe,-123»). Devuelve la ruta del ejecutable si existe en disco.
    /// </summary>
    private static string? ParseIconUri(string? value)
    {
        var resolved = ResolveIndirectString(value);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return null;
        }

        var comma = resolved.LastIndexOf(',');
        if (comma > 2 && int.TryParse(resolved[(comma + 1)..].Trim(), out _))
        {
            resolved = resolved[..comma].Trim();
        }

        resolved = resolved.Trim('"', ' ');
        return File.Exists(resolved) ? resolved : null;
    }

    /// <summary>
    /// Firefox y sus derivados no registran el AUMID: el acceso directo del instalador les da
    /// un hash que Windows asigna a la ventana oculta de SMTC. El propio instalador apunta la
    /// ruta de instalación a ese hash en Software\Mozilla\*\TaskBarIDs, así que de ahí sale el
    /// ejecutable real (y con él el nombre y el icono).
    /// </summary>
    private static bool TryResolveFirefoxExecutable(string aumid, out string? executablePath)
    {
        executablePath = null;

        if (string.IsNullOrWhiteSpace(aumid))
        {
            return false;
        }

        foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                using var mozilla = root.OpenSubKey(@"Software\Mozilla");
                if (mozilla is null)
                {
                    continue;
                }

                foreach (var vendor in mozilla.GetSubKeyNames())
                {
                    using var taskBarIds = mozilla.OpenSubKey($@"{vendor}\TaskBarIDs");
                    if (taskBarIds is null)
                    {
                        continue;
                    }

                    foreach (var installPath in taskBarIds.GetValueNames())
                    {
                        if (!string.Equals(taskBarIds.GetValue(installPath) as string, aumid,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var candidate = Path.Combine(installPath, "firefox.exe");
                        if (File.Exists(candidate))
                        {
                            executablePath = candidate;
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // Clave ausente o sin permisos: se prueba el siguiente árbol.
            }
        }

        return false;
    }

    private static bool TryFindProcess(string aumid, out string? displayName, out string? executablePath)
    {
        displayName = null;
        executablePath = null;

        var candidates = BuildCandidates(aumid);
        if (candidates.Count == 0)
        {
            return false;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return false;
        }

        try
        {
            // Primero intentar coincidencia exacta del nombre del proceso.
            foreach (var process in processes)
            {
                string processName;
                try
                {
                    processName = process.ProcessName;
                }
                catch
                {
                    continue;
                }

                if (!candidates.Contains(processName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    // Access denied for elevated/protected processes: the name is still useful.
                }

                executablePath = path;
                displayName = FileDescription(path) ?? processName;
                return true;
            }

            // Segundo intento: buscar por título de ventana (para players como foobar2000
            // cuyo proceso no coincide con el AUMID).
            foreach (var process in processes)
            {
                string? windowTitle = null;
                try
                {
                    windowTitle = process.MainWindowTitle;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(windowTitle))
                {
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    if (windowTitle.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        string? path = null;
                        try
                        {
                            path = process.MainModule?.FileName;
                        }
                        catch
                        {
                            // Access denied for elevated/protected processes.
                        }

                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            executablePath = path;
                            displayName = FileDescription(path) ?? process.ProcessName;
                            return true;
                        }
                    }
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return false;
    }

    private static string? FileDescription(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
            {
                return info.FileDescription.Trim();
            }

            if (!string.IsNullOrWhiteSpace(info.ProductName))
            {
                return info.ProductName.Trim();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary>Process names that could belong to this AUMID. Deliberately conservative.</summary>
    private static List<string> BuildCandidates(string aumid)
    {
        var candidates = new List<string>();

        if (aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(Path.GetFileNameWithoutExtension(aumid));
            return candidates.Where(value => value.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        var separator = aumid.IndexOf('!');
        if (separator > 0)
        {
            // Packaged app: Family_PublisherId!AppId
            var family = aumid[..separator];
            var appId = aumid[(separator + 1)..];
            candidates.Add(appId);

            var familyWithoutHash = PublisherHash.Replace(family, "");
            candidates.Add(familyWithoutHash);

            var lastSegment = familyWithoutHash.Split('.').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(lastSegment))
            {
                candidates.Add(lastSegment);
            }
        }
        else
        {
            candidates.Add(aumid);

            // AUMIDs con puntos («com.zarz.mornye», «Microsoft.ZuneMusic»): el proceso
            // casi siempre se llama como el último tramo, no como el AUMID completo.
            // Se recorre de atrás hacia delante para probar primero lo más probable.
            var parts = aumid.Split('.', StringSplitOptions.RemoveEmptyEntries);
            for (var index = parts.Length - 1; index >= 1; index--)
            {
                candidates.Add(parts[index]);
            }
        }

        return candidates
            .Where(value => value.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Prettify(string aumid, string fallback)
    {
        var value = aumid.Trim();

        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(value);
        }

        var separator = value.IndexOf('!');
        if (separator > 0)
        {
            value = value[..separator];
        }

        value = PublisherHash.Replace(value, "");

        // Components such as Gatefold.Player append a per-instance id:
        // "Gatefold.Player.23ABCE33" -> "Gatefold.Player".
        var withoutInstance = TrailingInstanceId.Replace(value, "");
        if (!string.IsNullOrWhiteSpace(withoutInstance))
        {
            value = withoutInstance;
        }

        if (value.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) && value.Length > "Microsoft.".Length)
        {
            value = value["Microsoft.".Length..];
        }

        // Un hash opaco (el AUMID de un acceso directo, como el de Firefox) no dice nada al
        // usuario: antes de eso se prefiere el texto genérico.
        if (OpaqueAppId.IsMatch(value))
        {
            return fallback;
        }

        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static readonly Regex OpaqueAppId = new("^[0-9A-Fa-f]{16}$", RegexOptions.Compiled);
}
