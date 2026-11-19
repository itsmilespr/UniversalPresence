using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalPresence.Cli;
using UniversalPresence.Infrastructure;

namespace UniversalPresence.Configuration;

/// <summary>
/// Runtime configuration. Stored as JSON in %APPDATA%\UniversalPresence\config.json.
/// Nothing here can enable a player whitelist: there is no such concept in this app.
/// </summary>
public sealed class AppOptions
{
    public const string AppFolderName = "UniversalPresence";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonSerializerOptions SerializerOptionsPublic => SerializerOptions;
    /// <summary>Discord application (client) id. Create an app at https://discord.com/developers/applications.</summary>
    public string DiscordClientId { get; set; } = "1558323929766953042";

    /// <summary>
    /// Token OAuth2 de usuario (scope identify). Lo usa <c>external-assets</c> para convertir
    /// las URLs de las carátulas en el proxy «mp:» de Discord.
    /// Un token de BOT no sirve para esto: Discord responde 403 «Bots cannot use this endpoint»
    /// y las carátulas salen como placeholder.
    /// Vacío = se envían las URLs externas directas (Discord las muestra como placeholder).
    /// </summary>
    [JsonIgnore]
    public string DiscordAccessToken { get; set; } = "";

    /// <summary>
    /// Refresh token OAuth2 (lo entrega la ventana «Conectar con Discord»). Permite renovar
    /// el access token sin volver a autorizar. Si la aplicación no está marcada como
    /// «Public Client» en el portal, la renovación necesita <see cref="DiscordClientSecret"/>.
    /// </summary>
    [JsonIgnore]
    public string DiscordRefreshToken { get; set; } = "";

    /// <summary>Client secret opcional, solo para renovar el token en apps no públicas.</summary>
    [JsonIgnore]
    public string DiscordClientSecret { get; set; } = "";

    /// <summary>
    /// Redirect URI registrado en la aplicación. Discord exige que coincida exacto, y para que
    /// la ventana de conexión capture la respuesta sin copiar y pegar tiene que apuntar a un
    /// puerto local (hay que añadirlo una vez en el portal → OAuth2 → Redirects).
    /// </summary>
    public string DiscordRedirectUri { get; set; } = "http://127.0.0.1:51234/callback";

    /// <summary>Momento (UTC) en que caduca <see cref="DiscordAccessToken"/>.</summary>
    public DateTimeOffset? DiscordTokenExpiresAt { get; set; }

    /// <summary>How often the media sessions are re-read as a safety net, in milliseconds.</summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Hueco mínimo entre dos actualizaciones que no cambian lo que se ve (latido y
    /// reanclaje de pausa). Los cambios de pista, de reproductor o la llegada de la
    /// portada se envían al instante, sin pasar de ~5 actualizaciones cada 20 s, que es
    /// el límite de Discord.
    /// </summary>
    public int MinUpdateIntervalMs { get; set; } = 4000;

    /// <summary>Re-publish the current activity periodically (keeps the IPC connection warm). 0 disables it.</summary>
    public int HeartbeatSeconds { get; set; } = 60;

    public bool StartPaused { get; set; }

    /// <summary>Send start/end timestamps so Discord renders a live progress bar.</summary>
    public bool ShowProgress { get; set; } = true;

    /// <summary>
    /// While paused, re-anchor the (frozen) timestamps this often so Discord's client-side
    /// clock keeps the bar at the paused position. Discord has no "paused" flag and animates
    /// timestamps locally, so static values alone drift. 0 = send once and never re-anchor.
    /// </summary>
    public int PauseRefreshSeconds { get; set; } = 10;

    /// <summary>
    /// Some third-party SMTC integrations report Stopped instead of Paused while the track is
    /// paused. Enable this to keep publishing those sessions as paused instead of clearing.
    /// </summary>
    public bool TreatStoppedAsPaused { get; set; }

    /// <summary>
    /// Keep the track visible in Discord while it is paused (Music Presence's
    /// "Show paused media in your status"). Disabled = the presence is cleared on pause.
    /// </summary>
    public bool ShowPausedMedia { get; set; } = true;

    /// <summary>
    /// While paused, send timestamps that freeze the progress bar at 0:00 (Music Presence's
    /// "Freeze the progress bar for paused media"). Disabled = the bar stays pinned at the
    /// paused position instead.
    /// </summary>
    public bool PauseResetProgress { get; set; } = true;

    /// <summary>
    /// While paused, show <see cref="PauseAssetKey"/> as the small image instead of the
    /// UniversalPresence icon (Discord cannot overlay an icon on the cover, so the badge
    /// slot is reused, like Music Presence's paused icon).
    /// </summary>
    public bool ShowPausedIcon { get; set; }

    /// <summary>Asset key of the pause badge uploaded in the Discord Developer Portal. Empty = no pause badge.</summary>
    public string PauseAssetKey { get; set; } = "";

    /// <summary>
    /// Asset key of the UniversalPresence icon shown as the small image next to the cover.
    /// Empty = keep the previous behaviour (<see cref="PlayerAssetKey"/> player badge or none).
    /// </summary>
    public string AppIconAssetKey { get; set; } = "";

    /// <summary>Show the album name in the activity tooltip.</summary>
    public bool ShowAlbum { get; set; } = true;

    /// <summary>Append "· PlayerName" to the state line (off by default: state is the artist).</summary>
    public bool AppendPlayerToState { get; set; }

    /// <summary>
    /// Muestra el nombre del reproductor en la línea superior de Discord ("Reproduciendo foobar2000")
    /// en lugar del nombre fijo de la aplicación. Como Music Presence.
    /// </summary>
    public bool UsePlayerNameAsActivityName { get; set; } = true;

    /// <summary>
    /// No publica presencia si la canción no tiene portada disponible. Útil para no mostrar
    /// actividades incompletas en Discord.
    /// </summary>
    public bool HideIfNoArtwork { get; set; }

    /// <summary>
    /// Inicia Universal Presence automáticamente con Windows (entrada en el registro Run de HKCU).
    /// </summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>
    /// Idioma del texto que se publica en Discord (ISO: en, es, pt, fr, de, it, nl, pl,
    /// ru, uk, tr, ja, ko, zh-CN, zh-TW, id, sv, cs). Por defecto inglés; un idioma
    /// desconocido cae al inglés.
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Busca actualizaciones en GitHub Releases al arrancar y cada 6 h, y las instala al
    /// cerrar. Se puede desactivar desde el menú del icono de bandeja.
    /// </summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Playing | Listening | Watching | Competing.</summary>
    public string ActivityType { get; set; } = "Listening";

    /// <summary>
    /// Plantilla del campo Details (línea 1). Marcadores: {title}, {artist}, {album}, {player}, {status}.
    /// Vacío = comportamiento por defecto (título).
    /// </summary>
    public string DetailsTemplate { get; set; } = "";

    /// <summary>
    /// Plantilla del campo State (línea 2). Marcadores: {title}, {artist}, {album}, {player}, {status}.
    /// Vacío = comportamiento por defecto (artista).
    /// </summary>
    public string StateTemplate { get; set; } = "";

    /// <summary>
    /// Plantilla del tooltip de la portada (large text). Marcadores: {title}, {artist}, {album}, {player}.
    /// Vacío = comportamiento por defecto (álbum).
    /// </summary>
    public string LargeTextTemplate { get; set; } = "";

    /// <summary>Muestra un toast de Windows al cambiar de pista.</summary>
    public bool ShowTrackChangeToast { get; set; } = true;

    /// <summary>Pausa la presencia automáticamente cuando hay un juego a pantalla completa en primer plano.</summary>
    public bool GamingMode { get; set; }

    /// <summary>Guarda un historial local de lo escuchado (JSON Lines).</summary>
    public bool ListeningHistory { get; set; } = true;

    /// <summary>Nombre del perfil activo (configuración guardada en profiles/).</summary>
    public string ActiveProfile { get; set; } = "default";

    /// <summary>Se ha completado el asistente de primera ejecución.</summary>
    public bool FirstRunCompleted { get; set; }

    /// <summary>API key de Last.fm para scrobbling (vacío = desactivado).</summary>
    [JsonIgnore]
    public string LastFmApiKey { get; set; } = "";

    /// <summary>Secreto compartido de Last.fm (necesario para firmar las peticiones).</summary>
    [JsonIgnore]
    public string LastFmSecret { get; set; } = "";

    /// <summary>Usuario de Last.fm para scrobbling (vacío = desactivado).</summary>
    public string LastFmUser { get; set; } = "";

    /// <summary>Token de sesión de Last.fm (se obtiene autorizando una vez).</summary>
    [JsonIgnore]
    public string LastFmSessionKey { get; set; } = "";

    /// <summary>Muestra la ventana flotante "now playing".</summary>
    public bool ShowOverlay { get; set; }

    /// <summary>Atajos de teclado globales activos.</summary>
    public bool GlobalHotkeys { get; set; } = true;

    /// <summary>Tema oscuro para la ventana de configuración y el overlay.</summary>
    public bool DarkMode { get; set; }

    /// <summary>Rollback automático si la app no arranca correctamente.</summary>
    public bool AutoRollback { get; set; } = true;

    /// <summary>Load third-party plugins from %LOCALAPPDATA%\UniversalPresence\plugins.</summary>
    public bool PluginsEnabled { get; set; }

    /// <summary>Asset key for the small player badge (upload it in the Discord Developer Portal). Empty = no badge.</summary>
    public string PlayerAssetKey { get; set; } = "";

    /// <summary>Schema version for config migration.</summary>
    public int ConfigVersion { get; set; } = 2;

    public ArtworkOptions Artwork { get; set; } = new();

    /// <summary>Optional display-name overrides, keyed by AppUserModelId. Display only, never a filter.</summary>
    public Dictionary<string, string> PlayerNameOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sessions whose AppUserModelId, process name or resolved player name contains any of
    /// these values are discarded entirely (no activity, no artwork read). Browsers are
    /// ignored by default; this is a blacklist, not a whitelist: every other player is still
    /// detected with no further configuration.
    /// </summary>
    public List<string> IgnoredPlayers { get; set; } = new()
    {
        "brave",
        "chrome",
        "msedge",
        "firefox",
        "opera",
        "vivaldi",
    };

    /// <summary>Debug | Info | Warn | Error.</summary>
    public string LogLevel { get; set; } = "Info";

    [JsonIgnore]
    public string ConfigPath { get; internal set; } = DefaultConfigPath;

    [JsonIgnore]
    public string SecretsPath => Path.Combine(Path.GetDirectoryName(ConfigPath) ?? "", "secrets.dpapi.json");

    [JsonIgnore]
    public static string DefaultConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppFolderName,
        "config.json");

    public static AppOptions Load(CliOptions cli)
    {
        // Modo portable: si existe config.json junto al .exe, se usa (sin tocar %APPDATA%).
        var portablePath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var path = cli.ConfigPath ?? (File.Exists(portablePath) ? portablePath : DefaultConfigPath);
        AppOptions options;

        // Secretos legacy que pudieran estar aún en el JSON (versiones anteriores).
        string legacyDiscordAccess = "";
        string legacyDiscordRefresh = "";
        string legacyDiscordSecret = "";
        string legacyLastFmApi = "";
        string legacyLastFmSecret = "";
        string legacyLastFmSession = "";
        int legacyVersion = 0;

        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    legacyDiscordAccess = GetString(root, "discordAccessToken");
                    legacyDiscordRefresh = GetString(root, "discordRefreshToken");
                    legacyDiscordSecret = GetString(root, "discordClientSecret");
                    legacyLastFmApi = GetString(root, "lastFmApiKey");
                    legacyLastFmSecret = GetString(root, "lastFmSecret");
                    legacyLastFmSession = GetString(root, "lastFmSessionKey");
                    if (root.TryGetProperty("configVersion", out var versionElement)
                        && versionElement.ValueKind == JsonValueKind.Number)
                    {
                        legacyVersion = versionElement.GetInt32();
                    }
                }
                catch
                {
                    // El JSON puede estar dañado; se intenta la deserialización normal.
                }

                options = JsonSerializer.Deserialize<AppOptions>(json, SerializerOptions) ?? new AppOptions();
            }
            else
            {
                options = new AppOptions();
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(path, JsonSerializer.Serialize(options, SerializerOptions));
            }
        }
        catch (Exception ex)
        {
            // Último recurso: intentar el backup atómico anterior.
            try
            {
                var backup = path + ".bak";
                if (File.Exists(backup))
                {
                    var backupJson = File.ReadAllText(backup);
                    options = JsonSerializer.Deserialize<AppOptions>(backupJson, SerializerOptions) ?? new AppOptions();
                    Console.Error.WriteLine($"Configuration restored from backup ({backup}).");
                }
                else
                {
                    Console.Error.WriteLine($"Could not read the configuration ({path}): {ex.Message}. Using defaults.");
                    options = new AppOptions();
                }
            }
            catch
            {
                Console.Error.WriteLine($"Could not read the configuration ({path}): {ex.Message}. Using defaults.");
                options = new AppOptions();
            }
        }

        options.ConfigPath = path;

        // Perfil activo: si no es el predeterminado, cargar su archivo.
        if (cli.ConfigPath is null
            && !string.Equals(options.ActiveProfile, "default", StringComparison.OrdinalIgnoreCase))
        {
            var profilePath = ProfileService.PathFor(options.ActiveProfile);
            if (File.Exists(profilePath))
            {
                try
                {
                    var profileJson = File.ReadAllText(profilePath);
                    var profileOptions = JsonSerializer.Deserialize<AppOptions>(profileJson, SerializerOptions) ?? new AppOptions();
                    profileOptions.ConfigPath = profilePath;
                    profileOptions.ActiveProfile = options.ActiveProfile;
                    profileOptions.Normalize();
                    options = profileOptions;
                }
                catch
                {
                    options.ActiveProfile = "default";
                    options.ConfigPath = AppOptions.DefaultConfigPath;
                }
            }
            else
            {
                options.ActiveProfile = "default";
            }
        }

        // Migración de secretos legacy (plaintext en config.json) al almacén protegido.
        if (string.IsNullOrWhiteSpace(options.DiscordAccessToken)) options.DiscordAccessToken = legacyDiscordAccess;
        if (string.IsNullOrWhiteSpace(options.DiscordRefreshToken)) options.DiscordRefreshToken = legacyDiscordRefresh;
        if (string.IsNullOrWhiteSpace(options.DiscordClientSecret)) options.DiscordClientSecret = legacyDiscordSecret;
        if (string.IsNullOrWhiteSpace(options.LastFmApiKey)) options.LastFmApiKey = legacyLastFmApi;
        if (string.IsNullOrWhiteSpace(options.LastFmSecret)) options.LastFmSecret = legacyLastFmSecret;
        if (string.IsNullOrWhiteSpace(options.LastFmSessionKey)) options.LastFmSessionKey = legacyLastFmSession;

        // Migraciones por versión.
        if (legacyVersion < 2)
        {
            options.ConfigVersion = 2;
        }

        LoadSecrets(options);

        if (cli.ConfigPath is null
            && !File.Exists(Path.Combine(AppContext.BaseDirectory, "config.json"))
            && (legacyDiscordAccess.Length > 0 || legacyDiscordRefresh.Length > 0
                || legacyDiscordSecret.Length > 0 || legacyLastFmApi.Length > 0
                || legacyLastFmSecret.Length > 0 || legacyLastFmSession.Length > 0))
        {
            // El config.json antiguo contenía secretos en claro: se reescribe sin ellos.
            try { options.Save(); } catch { }
        }

        if (!string.IsNullOrWhiteSpace(cli.ClientId))
        {
            options.DiscordClientId = cli.ClientId.Trim();
        }

        if (cli.Paused)
        {
            options.StartPaused = true;
        }

        if (cli.Verbose)
        {
            options.LogLevel = nameof(Infrastructure.LogLevel.Debug);
        }

        options.Normalize();
        return options;
    }

    public void Normalize()
    {
        PollIntervalMs = Math.Clamp(PollIntervalMs, 250, 60_000);
        MinUpdateIntervalMs = Math.Clamp(MinUpdateIntervalMs, 1_000, 60_000);
        HeartbeatSeconds = Math.Clamp(HeartbeatSeconds, 0, 3_600);
        PauseRefreshSeconds = Math.Clamp(PauseRefreshSeconds, 0, 120);
        DiscordClientId = DiscordClientId?.Trim() ?? "";
        DiscordAccessToken = DiscordAccessToken?.Trim() ?? "";
        DiscordRefreshToken = DiscordRefreshToken?.Trim() ?? "";
        DiscordClientSecret = DiscordClientSecret?.Trim() ?? "";
        DiscordRedirectUri = string.IsNullOrWhiteSpace(DiscordRedirectUri)
            ? "http://127.0.0.1:51234/callback"
            : DiscordRedirectUri.Trim();
        PlayerAssetKey = PlayerAssetKey?.Trim() ?? "";
        PauseAssetKey = PauseAssetKey?.Trim() ?? "";
        AppIconAssetKey = AppIconAssetKey?.Trim() ?? "";
        Artwork ??= new ArtworkOptions();
        Artwork.Normalize();
        Language = string.IsNullOrWhiteSpace(Language) ? "en" : Language.Trim();

        IgnoredPlayers = (IgnoredPlayers ?? new List<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in PlayerNameOverrides ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                normalized[pair.Key.Trim()] = pair.Value.Trim();
            }
        }

        PlayerNameOverrides = normalized;

        if (!Enum.TryParse<LogLevel>(LogLevel, ignoreCase: true, out _))
        {
            LogLevel = nameof(Infrastructure.LogLevel.Info);
        }
    }

    // Common naming differences between an AUMID and the browser's own name.
    private static readonly Dictionary<string, string[]> PlayerAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge"] = new[] { "microsoft edge", "microsoftedge" },
    };

    /// <summary>
    /// True when any ignored value is contained in the AUMID, process name, executable or
    /// resolved player name (case-insensitive substring, as configured by the user).
    /// </summary>
    public bool IsIgnoredPlayer(params string?[] candidates)
    {
        if (IgnoredPlayers.Count == 0)
        {
            return false;
        }

        var haystack = string.Join(
                ' ',
                candidates.Where(value => !string.IsNullOrWhiteSpace(value)))
            .ToLowerInvariant();

        if (haystack.Length == 0)
        {
            return false;
        }

        foreach (var entry in IgnoredPlayers)
        {
            var token = entry.Trim().ToLowerInvariant();
            if (token.Length == 0)
            {
                continue;
            }

            if (haystack.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }

            if (PlayerAliases.TryGetValue(token, out var aliases)
                && aliases.Any(alias => haystack.Contains(alias, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    public string? ResolvePlayerOverride(string appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return null;
        }

        return PlayerNameOverrides.TryGetValue(appUserModelId, out var name) ? name : null;
    }

    /// <summary>Persists the current values; throws so the UI can report a failure.</summary>
    public string Save()
    {
        SaveSecrets();

        var directory = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = ConfigPath + ".tmp";
        var backup = ConfigPath + ".bak";
        var json = JsonSerializer.Serialize(this, SerializerOptions);
        File.WriteAllText(temp, json);

        try
        {
            if (File.Exists(ConfigPath))
            {
                File.Replace(temp, ConfigPath, backup, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, ConfigPath);
            }
        }
        catch
        {
            if (File.Exists(temp))
            {
                File.Move(temp, ConfigPath, overwrite: true);
            }
        }

        return ConfigPath;
    }

    private static string GetString(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        return element.GetString() ?? "";
    }

    private void SaveSecrets()
    {
        try
        {
            var directory = Path.GetDirectoryName(SecretsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["discordAccessToken"] = SecretProtector.Protect(DiscordAccessToken),
                ["discordRefreshToken"] = SecretProtector.Protect(DiscordRefreshToken),
                ["discordClientSecret"] = SecretProtector.Protect(DiscordClientSecret),
                ["lastFmApiKey"] = SecretProtector.Protect(LastFmApiKey),
                ["lastFmSecret"] = SecretProtector.Protect(LastFmSecret),
                ["lastFmSessionKey"] = SecretProtector.Protect(LastFmSessionKey),
            };

            File.WriteAllText(
                SecretsPath,
                JsonSerializer.Serialize(payload, SerializerOptions));
        }
        catch
        {
            // Never let secret storage take the app down; the main config still saves.
        }
    }

    private static void LoadSecrets(AppOptions options)
    {
        try
        {
            if (!File.Exists(options.SecretsPath))
            {
                return;
            }

            var json = File.ReadAllText(options.SecretsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            options.DiscordAccessToken = SecretProtector.Unprotect(GetString(root, "discordAccessToken"));
            options.DiscordRefreshToken = SecretProtector.Unprotect(GetString(root, "discordRefreshToken"));
            options.DiscordClientSecret = SecretProtector.Unprotect(GetString(root, "discordClientSecret"));
            options.LastFmApiKey = SecretProtector.Unprotect(GetString(root, "lastFmApiKey"));
            options.LastFmSecret = SecretProtector.Unprotect(GetString(root, "lastFmSecret"));
            options.LastFmSessionKey = SecretProtector.Unprotect(GetString(root, "lastFmSessionKey"));
        }
        catch
        {
            // Corrupt secret store: leave the values empty instead of crashing.
        }
    }

    public static string LocalDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);
}

public sealed class ArtworkOptions
{
    /// <summary>Read the SMTC thumbnail and expose it to Discord.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Auto (default) | ExternalUrl | Lookup | DiscordAsset | None.
    /// Auto: serve the extracted SMTC cover from <see cref="ExternalUrlBase"/> when it is set,
    /// otherwise resolve a public cover URL from the track metadata.
    /// </summary>
    public string Mode { get; set; } = "Auto";

    /// <summary>
    /// Asset key uploaded to the Discord application, used when there is no cover art.
    /// Leave empty to let Discord fall back to the application icon automatically.
    /// </summary>
    public string FallbackAssetKey { get; set; } = "";

    /// <summary>
    /// Public HTTPS base for extracted covers, e.g. https://cdn.example.com/presence.
    /// The app writes &lt;hash&gt;.jpg/png files to <see cref="CacheDirectory"/>; Discord must be able to fetch them.
    /// </summary>
    public string ExternalUrlBase { get; set; } = "";

    /// <summary>
    /// None | Imgur | Catbox (default). Uploads the extracted SMTC cover to a public image host
    /// and uses the returned https URL, because Discord cannot fetch a local file.
    /// Catbox needs no API key and works out of the box.
    /// </summary>
    public string UploadMode { get; set; } = "Catbox";

    /// <summary>Imgur Client-ID used for anonymous uploads. Ignored by Catbox.</summary>
    public string UploadApiKey { get; set; } = "";

    /// <summary>Where thumbnails are cached. Defaults to %LOCALAPPDATA%\UniversalPresence\artwork.</summary>
    public string? CacheDirectory { get; set; }

    [JsonIgnore]
    public string ResolvedCacheDirectory => string.IsNullOrWhiteSpace(CacheDirectory)
        ? Path.Combine(AppOptions.LocalDataDirectory, "artwork")
        : CacheDirectory!;

    private bool HasPublicBase =>
        Uri.TryCreate(ExternalUrlBase, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;

    [JsonIgnore]
    public bool IsDisabled => !Enabled || string.Equals(Mode, "None", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool UsesExternalUrl =>
        !IsDisabled
        && (string.Equals(Mode, "ExternalUrl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Mode, "Auto", StringComparison.OrdinalIgnoreCase))
        && HasPublicBase;

    /// <summary>Upload the extracted SMTC cover to the configured public image host.</summary>
    [JsonIgnore]
    public bool UsesUpload =>
        !IsDisabled
        && !string.Equals(UploadMode, "None", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the cover may need a network step (upload or catalogue lookup).</summary>
    [JsonIgnore]
    public bool ResolvesArtwork => UsesUpload || UsesLookup;

    /// <summary>Resolve a publicly reachable cover URL from the track metadata.</summary>
    [JsonIgnore]
    public bool UsesLookup =>
        !IsDisabled
        && !UsesExternalUrl
        && (string.Equals(Mode, "Lookup", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Mode, "Auto", StringComparison.OrdinalIgnoreCase));

    public void Normalize()
    {
        Mode = string.IsNullOrWhiteSpace(Mode) ? "Auto" : Mode.Trim();

        var known = new[] { "Auto", "ExternalUrl", "Lookup", "DiscordAsset", "None" };
        if (!known.Contains(Mode, StringComparer.OrdinalIgnoreCase))
        {
            Mode = "Auto";
        }

        FallbackAssetKey = FallbackAssetKey?.Trim() ?? "";
        ExternalUrlBase = ExternalUrlBase?.Trim().TrimEnd('/') ?? "";

        // "None" es una opción válida: desactiva la subida a un host público (por privacidad
        // o para usar solo la búsqueda de portada). Solo se corrige un valor desconocido.
        UploadMode = string.IsNullOrWhiteSpace(UploadMode) ? "Catbox" : UploadMode.Trim();

        if (!new[] { "None", "Imgur", "Catbox" }.Any(x => string.Equals(x, UploadMode, StringComparison.OrdinalIgnoreCase)))
        {
            UploadMode = "Catbox";
        }

        UploadApiKey = UploadApiKey?.Trim() ?? "";
    }
}
