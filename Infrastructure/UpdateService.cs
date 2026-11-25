using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using UniversalPresence.Configuration;

namespace UniversalPresence.Infrastructure;

/// <summary>Una release más nueva publicada en GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, long Size, string? Sha256);

/// <summary>
/// Busca, descarga y aplica actualizaciones desde GitHub Releases.
///
/// No usa credenciales: la API pública de GitHub permite 60 consultas por hora y aquí se
/// consulta al arrancar y cada 6 h como mucho. La descarga se verifica con el SHA-256 que
/// publica la release (si está disponible) y con el tamaño del asset.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string Owner = "itsmilespr";
    private const string Repo = "UniversalPresence";
    private const string AssetName = "UniversalPresence.exe";
    private const long MinExpectedBytes = 20 * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private readonly Log _log;
    private readonly string _directory;
    private readonly CancellationTokenSource _cts = new();
    private UpdateInfo? _available;
    private string? _downloadedPath;

    public UpdateService(Log log)
    {
        _log = log;
        _directory = Path.Combine(AppOptions.LocalDataDirectory, "updates");
        Directory.CreateDirectory(_directory);
    }

    public Version CurrentVersion { get; } =
        Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0));

    public UpdateInfo? Available => _available;

    public bool HasDownloadedUpdate => _downloadedPath is not null && File.Exists(_downloadedPath);

    /// <summary>
    /// Busca la release más nueva. Con <paramref name="includePreReleases"/> también tiene
    /// en cuenta las publicadas como beta, que GitHub excluye de <c>/releases/latest</c>.
    /// Devuelve null si ya se está al día.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(bool includePreReleases = false)
    {
        var url = includePreReleases
            ? $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=20"
            : $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await Http.SendAsync(request, _cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _log.Debug($"GitHub responded {(int)response.StatusCode} while checking for updates.");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: _cts.Token)
            .ConfigureAwait(false);

        if (!includePreReleases)
        {
            return Remember(ParseRelease(json.RootElement));
        }

        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        UpdateInfo? best = null;
        foreach (var release in json.RootElement.EnumerateArray())
        {
            var candidate = ParseRelease(release);
            if (candidate is null)
            {
                continue;
            }

            if (best is null || candidate.Version > best.Version)
            {
                best = candidate;
            }
        }

        return Remember(best);
    }

    private UpdateInfo? Remember(UpdateInfo? info)
    {
        if (info is null)
        {
            return null;
        }

        _available = info;
        _log.Info($"Update available: {info.Tag} ({info.Size / 1048576.0:0.0} MB).");
        return info;
    }

    private UpdateInfo? ParseRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!root.TryGetProperty("tag_name", out var tagElement)
            || tagElement.GetString() is not { Length: > 1 } tag)
        {
            return null;
        }

        tag = tag.Trim();

        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
        {
            _log.Debug($"Release '{tag}' has no recognizable version.");
            return null;
        }

        version = Normalize(version);

        if (version.CompareTo(CurrentVersion) <= 0)
        {
            _log.Debug($"No updates: {tag} is not newer than {CurrentVersion}.");
            return null;
        }

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name)
                || !string.Equals(name.GetString(), AssetName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!asset.TryGetProperty("browser_download_url", out var urlElement)
                || urlElement.GetString() is not { Length: > 0 } downloadUrl)
            {
                continue;
            }

            var size = asset.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;

            string? sha256 = null;
            if (asset.TryGetProperty("digest", out var digestElement)
                && digestElement.GetString() is { Length: > 0 } digest)
            {
                digest = digest.Trim();
                const string prefix = "sha256:";
                if (digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    sha256 = digest[prefix.Length..].ToLowerInvariant();
                }
            }

            return new UpdateInfo(version, tag, downloadUrl, size, sha256);
        }

        _log.Debug($"La release {tag} no incluye {AssetName}.");
        return null;
    }

    /// <summary>True cuando hay un .exe.bak de la versión anterior guardado por la última actualización.</summary>
    public bool HasPreviousVersion
    {
        get
        {
            try
            {
                var exe = Environment.ProcessPath;
                return exe is not null && File.Exists(exe + ".bak");
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Devuelve la versión anterior: un script espera a que la app cierre, restaura el
    /// .bak sobre el .exe y arranca. Es la salida de emergencia si una actualización falla.
    /// </summary>
    public bool TryRestorePreviousVersion()
    {
        var target = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target + ".bak"))
        {
            return false;
        }

        if (!LaunchRestore(target, _log))
        {
            return false;
        }

        // Si se restaurara la versión anterior y quedara una actualización descargada,
        // Dispose() también lanzaría apply-update.ps1 al cerrar: los dos scripts esperarían
        // al mismo proceso y competirían por el .exe. Se descarta la descarga pendiente.
        DeleteOlderDownloads(string.Empty);
        _downloadedPath = null;
        return true;
    }

    /// <summary>
    /// Marca de actualización a medio aplicar. Contiene el .exe que se va a sustituir y
    /// cuántas veces se ha arrancado la versión nueva sin confirmar.
    /// </summary>
    private static string PendingUpdatePath =>
        Path.Combine(AppOptions.LocalDataDirectory, "update-pending.json");

    private sealed class PendingUpdate
    {
        public string Target { get; set; } = "";
        public int Attempts { get; set; }
        public DateTimeOffset At { get; set; }
    }

    /// <summary>
    /// Se llama justo antes de reemplazar el .exe. Si la versión nueva no llega a arrancar
    /// (una release con una dependencia rota, un antivirus que bloquea el binario), el
    /// siguiente arranque restaura el .bak automáticamente en lugar de dejar la app muerta.
    /// </summary>
    public static void MarkUpdatePending(string target)
    {
        try
        {
            Directory.CreateDirectory(AppOptions.LocalDataDirectory);
            File.WriteAllText(PendingUpdatePath, JsonSerializer.Serialize(new PendingUpdate
            {
                Target = target,
                Attempts = 0,
                At = DateTimeOffset.Now,
            }));
        }
        catch
        {
            // Sin marca simplemente no hay rollback automático; la actualización sigue.
        }
    }

    /// <summary>
    /// Comprueba la marca al arrancar. Devuelve true cuando hay que salir porque se acaba de
    /// lanzar la versión anterior.
    /// </summary>
    public static bool CheckStartupGuard(bool autoRollback, Log log)
    {
        PendingUpdate? pending = null;

        try
        {
            if (!File.Exists(PendingUpdatePath))
            {
                return false;
            }

            pending = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(PendingUpdatePath));
        }
        catch
        {
            ClearPending();
            return false;
        }

        if (pending is null || string.IsNullOrWhiteSpace(pending.Target))
        {
            ClearPending();
            return false;
        }

        // Primer arranque tras la actualización: se anota y se deja trabajar a la versión nueva.
        if (pending.Attempts == 0)
        {
            pending.Attempts = 1;

            try
            {
                File.WriteAllText(PendingUpdatePath, JsonSerializer.Serialize(pending));
            }
            catch
            {
                // sin marca persistida no habrá rollback, pero no se bloquea el arranque
            }

            return false;
        }

        // Segundo arranque sin confirmación: la versión nueva no llegó a arrancar.
        if (autoRollback && File.Exists(pending.Target + ".bak"))
        {
            log.Warn("La versión actualizada no arrancó; se restaura la versión anterior.");

            if (LaunchRestore(pending.Target, log))
            {
                ClearPending();
                return true;
            }
        }

        ClearPending();
        return false;
    }

    /// <summary>Confirma que la versión nueva arrancó bien (borra la marca de rollback).</summary>
    public static void ConfirmStartup(Log log)
    {
        if (!File.Exists(PendingUpdatePath))
        {
            return;
        }

        ClearPending();
        log.Info("Actualización confirmada: la versión nueva arrancó correctamente.");
    }

    private static void ClearPending()
    {
        try
        {
            if (File.Exists(PendingUpdatePath))
            {
                File.Delete(PendingUpdatePath);
            }
        }
        catch
        {
            // se limpia en el siguiente arranque
        }
    }

    /// <summary>
    /// Lanza el script que espera al cierre, copia el .bak sobre el .exe y arranca.
    /// </summary>
    private static bool LaunchRestore(string target, Log log)
    {
        try
        {
            var directory = Path.Combine(AppOptions.LocalDataDirectory, "updates");
            Directory.CreateDirectory(directory);
            var script = Path.Combine(directory, "restore-previous.ps1");
            File.WriteAllText(script, RestoreScript);

            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("-ProcessId");
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-Target");
            start.ArgumentList.Add(target);

            Process.Start(start);
            log.Info("Restauración de la versión anterior programada para el cierre.");
            return true;
        }
        catch (Exception ex)
        {
            log.Error("No se pudo restaurar la versión anterior.", ex);
            return false;
        }
    }

    /// <summary>Descarga el .exe de la release; null si la descarga no es válida.</summary>
    public async Task<string?> DownloadAsync(UpdateInfo info)
    {
        var target = Path.Combine(_directory, $"UniversalPresence-{info.Tag}.exe");
        var temp = target + ".part";

        try
        {
            using var response = await Http
                .GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, _cts.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var file = new FileStream(
                temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await response.Content.CopyToAsync(file, _cts.Token).ConfigureAwait(false);
            }

            var length = new FileInfo(temp).Length;

            if (length < MinExpectedBytes || (info.Size > 0 && length != info.Size))
            {
                _log.Warn($"Download size mismatch ({length} bytes of {info.Size}).");
                File.Delete(temp);
                return null;
            }

            if (info.Sha256 is not { Length: 64 } expected)
            {
                _log.Warn("La release no publica SHA-256; la actualización se descarta por seguridad.");
                File.Delete(temp);
                return null;
            }

            var actual = await HashAsync(temp).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                _log.Warn("La descarga no coincide con el SHA-256 de la release; se descarta.");
                File.Delete(temp);
                return null;
            }

            _log.Info("Update SHA-256 verified.");

            File.Move(temp, target, true);
            _downloadedPath = target;
            DeleteOlderDownloads(target);
            _log.Info($"Update downloaded: {target}");
            return target;
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            return null;
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not download the update: {ex.Message}");
            TryDelete(temp);
            return null;
        }
    }

    /// <summary>
    /// Lanza el reemplazo del .exe y el reinicio cuando este proceso termine. Windows no
    /// deja sobrescribir un ejecutable en uso, así que un script espera a que el proceso
    /// desaparezca, copia el nuevo y lo arranca.
    /// </summary>
    public bool TryApplyAndRestart()
    {
        var source = _downloadedPath;
        var target = Environment.ProcessPath;

        if (source is null || !File.Exists(source))
        {
            _log.Warn("No hay una actualización descargada que aplicar.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(target)
            || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(target))
        {
            _log.Warn("No se pudo determinar el ejecutable que hay que reemplazar.");
            return false;
        }

        try
        {
            var script = Path.Combine(_directory, "apply-update.ps1");
            File.WriteAllText(script, ApplyScript);

            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("-ProcessId");
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add("-Source");
            start.ArgumentList.Add(source);
            start.ArgumentList.Add("-Target");
            start.ArgumentList.Add(target);

            MarkUpdatePending(target);
            Process.Start(start);
            _log.Info($"Actualización programada: se instalará al cerrar y se reiniciará desde {target}.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("No se pudo lanzar el instalador de la actualización.", ex);
            return false;
        }
    }

    private void DeleteOlderDownloads(string keep)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "UniversalPresence-*.exe"))
            {
                if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"No se pudieron limpiar descargas anteriores: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // se limpia en la siguiente ejecución
        }
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UniversalPresence");
        return client;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _cts.Dispose(); } catch (ObjectDisposedException) { }
    }

    private const string RestoreScript = """
        param(
          [int]$ProcessId,
          [string]$Target
        )

        $deadline = (Get-Date).AddSeconds(90)
        while ((Get-Date) -lt $deadline) {
          if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { break }
          Start-Sleep -Milliseconds 500
        }

        Start-Sleep -Milliseconds 800

        for ($i = 0; $i -lt 30; $i++) {
          try {
            Copy-Item -LiteralPath "$Target.bak" -Destination $Target -Force -ErrorAction Stop
            break
          } catch {
            Start-Sleep -Milliseconds 500
          }
        }

        Start-Process -FilePath $Target
        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;

    private const string ApplyScript = """
        param(
          [int]$ProcessId,
          [string]$Source,
          [string]$Target
        )

        # Espera a que la app cierre (máximo 90 s) para poder reemplazar el .exe.
        $deadline = (Get-Date).AddSeconds(90)
        while ((Get-Date) -lt $deadline) {
          if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { break }
          Start-Sleep -Milliseconds 500
        }

        Start-Sleep -Milliseconds 800

        $installed = $false
        for ($i = 0; $i -lt 30; $i++) {
          try {
            Copy-Item -LiteralPath $Target -Destination "$Target.bak" -Force -ErrorAction SilentlyContinue
            Copy-Item -LiteralPath $Source -Destination $Target -Force -ErrorAction Stop
            $installed = $true
            break
          } catch {
            Start-Sleep -Milliseconds 500
          }
        }

        if ($installed) {
          Remove-Item -LiteralPath $Source -Force -ErrorAction SilentlyContinue
        }

        Start-Process -FilePath $Target
        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;
}
