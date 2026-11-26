# Universal Presence — documentación técnica

Detalles de implementación para quien quiera entender, modificar o compilar el proyecto.
Para instalar y usar la app, empieza por el [README](README.md).

- .NET 8 · WinForms · **sin dependencias NuGet**
- Windows 10 (1809+) y Windows 11, x64
- Protocolo IPC de Discord implementado a mano

---

## 1. Arquitectura

| Archivo | Responsabilidad |
| --- | --- |
| `Media/SmtcMonitor.cs` | `GlobalSystemMediaTransportControlsSessionManager.RequestAsync()`, `GetSessions()` y suscripción a `SessionsChanged`, `CurrentSessionChanged`, `MediaPropertiesChanged`, `PlaybackInfoChanged` y `TimelinePropertiesChanged`. **Ningún filtro por AUMID ni por ejecutable.** Elige la mejor sesión: `Playing` > `Paused` > `Stopped`, con bonus para la sesión actual y desempate por última actualización. |
| `Media/PlayerIdentityResolver.cs` | AUMID → nombre legible: override del usuario → `AppInfo.GetFromAppUserModelId` → proceso real en ejecución → AUMID embellecido. También extrae el icono del `.exe` a 256 px con `PrivateExtractIcons`. |
| `Media/ArtworkCache.cs` | Lee el `Thumbnail` de SMTC, lo normaliza a 512–1024 px y lo guarda en disco con hash SHA-256. Memoriza por pista para no releer la imagen en cada vuelta. |
| `Media/ArtworkLookupService.cs` | Busca una portada pública en **iTunes Search** y **Deezer** en paralelo, con caché de 6 h. |
| `Media/ArtworkUploadService.cs` | Sube la carátula extraída a **Litterbox** (72 h, sin clave) o **Imgur** (Client-ID) y guarda la URL. |
| `Media/ProgramIconAssetService.cs` | Sube el icono del reproductor una sola vez por AUMID, cachea la URL y la renueva con la misma política (revalidar a 24 h, renovar a 60 h). |
| `Core/PresenceEngine.Runtime.cs` | Si la memoria privada supera 80 MB (comprobado cada 5 s) fuerza un GC de gen2 en segundo plano: las referencias WinRT solo se liberan ahí y el heap gestionado es de ~1–7 MB. Mantiene el pico en ~90–100 MB en vez de dejar que crezca hasta 200 MB. También detecta una app a pantalla completa para el modo juego. |
| `Infrastructure/UpdateService.cs` | Consulta GitHub Releases (`/releases/latest` en estable, `/releases` filtrando pre-releases en beta), descarga el `.exe` verificando tamaño y SHA-256 y lo instala al cerrar mediante un script de PowerShell que espera al proceso y guarda el anterior como `.exe.bak`. `TryRestorePreviousVersion()` usa un segundo script para volver a ese `.bak`. |
| `Infrastructure/UserIdle.cs` | `GetLastInputInfo` para los segundos sin teclado/ratón y `OpenInputDesktop` para detectar bloqueo (un fallo de apertura es pantalla segura). Con `ClearWhenIdle` el motor limpia la presencia y la republica al volver. |
| `Infrastructure/ListeningHistory.cs` | Historial JSON Lines (`history.jsonl`) con título, artista, álbum, reproductor, fecha y duración. Agrega `Compute(días)` para el panel de estadísticas y exporta CSV. |
| `Localization/UiStrings.cs` | Diccionario por idioma para las cadenas nuevas (controles, estadísticas, ausencia, canal, portadas propias, Last.fm y asistente) en en/es/pt/fr/de/it/zh, con caída a inglés. |
| `Tray/StatsForm.cs` | Panel de estadísticas: tarjetas de totales, top artistas/canciones, últimos 7 días, filtro por periodo y exportación CSV. |
| `Tray/FirstRunForm.cs` | Asistente inicial: idioma, arranque con Windows, avisos, historial, overlay, estado de Discord y explicación de portadas/privacidad. |
| `Media/SmtcMonitor.cs` | Cachea sesiones, sesión actual, metadatos, playback y timeline (eventos + TTL de 10 s/15 s/2 s) para no crear objetos WinRT cada segundo. La posición se proyecta desde `LastUpdated`, así que la barra sigue exacta. |
| `Localization/PresenceText.cs` | Textos de la presencia en 18 idiomas (inglés por defecto): estado, álbum y botones. Se elige con `Language` en `config.json`. |
| `Discord/DiscordIpcClient.cs` | Named pipe `discord-ipc-0..9`, handshake `{v:1, client_id}`, frames de 8 bytes (op + longitud), `PING`→`PONG`, `SET_ACTIVITY`. |
| `Discord/DiscordPresenceService.cs` | Conexión perezosa, reconexión con backoff (1 s → 30 s), ventana de 5 actualizaciones / 20 s y limpieza de la actividad al salir. |
| `Core/PresenceEngine.cs` | Ciclo de vida del motor y estado compartido: campos, arranque/parada y el bucle principal (`LoopAsync`), que lee la sesión, aplica portada/artista/icono y encola la actividad. Es el archivo por el que conviene empezar. |
| `Core/PresenceEngine.Publishing.cs` | Cola de publicación (solo hay una actividad pendiente: la última gana), prioridades para no gastar el cupo de Discord, anclas de la barra y resincronización automática. |
| `Core/PresenceEngine.Artwork.cs` | Portadas e iconos: subida al host público, caché y revalidación, búsqueda en catálogo, portada propia del usuario y cola de reintentos. |
| `Core/PresenceEngine.TrackState.cs` | Identidad de pista, seeks, reanudación, contador «×N», hueco de metadatos al cambiar de canción y artistas invitados de Deezer. |
| `Core/PresenceEngine.History.cs` | Historial de escucha (`history.jsonl`) y Last.fm: cada pista se registra una sola vez y se scrobblea la anterior. |
| `Core/ActivityFactory.cs` | Construye el payload: título, artista, álbum, portada, icono, contador y timestamps. |
| `Tray/TrayApplicationContext.cs` | Icono de bandeja sin ventana principal, menú y globos de estado. |
| `Tray/WindowsAutostart.cs` | Entrada `UniversalPresence` en `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. |
| `Diagnostics/DiagnosticsRunner.cs` | Modos `--list` y `--test`. |

El motor corre en un `Task` aparte; la UI solo pinta el icono y recibe avisos con `BeginInvoke`.

## 2. Flujo de una actualización

```
SMTC (evento o sondeo de 1 s)
        │
        ▼
MediaSnapshot  ── título, artista, álbum, posición, duración, estado, carátula
        │
        ▼
ApplyArtwork / ApplyProgramIcon   (si hay que subir o buscar, se hace en segundo plano)
        │
        ▼
ActivityFactory  ── DiscordActivity (details, state, assets, timestamps)
        │
        ▼
publicador (un solo hueco)  ── espera hueco de Discord, descarta lo viejo
        │
        ▼
DiscordIpcClient.SET_ACTIVITY
```

El bucle **nunca espera a la red** ni al límite de Discord: si hay que subir una portada, se
lanza una tarea y se republica al terminar.

## 3. Carátulas

Discord resuelve las imágenes desde su lado y solo acepta una **URL HTTPS pública** (no admite
base64, `data:` ni `http://127.0.0.1`; `large_image` tiene además un límite de 256 caracteres).

Orden de resolución (`Artwork.Mode = "Auto"`):

1. `ExternalUrlBase`, si está configurado (host propio).
2. Subida de la carátula SMTC a Litterbox/Imgur y uso de la URL devuelta.
3. Búsqueda en iTunes + Deezer con título/artista/álbum.
4. `FallbackAssetKey`.
5. Icono de la aplicación (lo pone Discord automáticamente).

Notas:

- La URL se envía **cruda**. Las versiones anteriores la convertían al proxy `mp:` mediante
  `external-assets`, que exige un token OAuth2 de usuario y no renderizaba de forma fiable; ese
  camino ya no se usa para la actividad.
- `UploadMode: "None"` desactiva la subida (útil por privacidad o si usas `ExternalUrlBase`).
- Cada carátula se sube una vez y la URL queda en `covers-cache.json` con su fecha real de
  subida. Litterbox borra el archivo a las 72 h, así que la URL se **revalida con un HEAD a
  las 24 h** y se **renueva a las 60 h**; nunca se publica una URL ya caducada. Si el host no
  devuelve una nueva, se conserva la anterior mientras siga viva.
- Los archivos viven en `%LOCALAPPDATA%\UniversalPresence\artwork`.

## 4. Tiempos y saltos

`timestamps.start` y `timestamps.end` son epoch **en milisegundos**. Discord dibuja la barra en
su cliente a partir de ese par absoluto; no hay porcentaje ni estado «pausado».

- **Reproduciendo:** `start = ahora − posición`, `end = ahora + (duración − posición)`. Como el
  par es absoluto, esperar por el límite de Discord no retrasa la barra.
- **Pausado con `PauseResetProgress = true`:** `start = ahora + duración`,
  `end = start + duración`; Discord lo limita a `0:00` y la barra queda congelada. Se reancla
  cada `PauseRefreshSeconds` porque el cliente anima el reloj por su cuenta.
- **Pausado con `PauseResetProgress = false`:** el par se congela en la posición de pausa y se
  reancla igual.
- **Reanudar la misma pista:** se relee la sesión en ese instante (`ReadFreshAsync`, sin
  proyectar la posición) y se republica aunque el título no cambie. Si no, la barra se queda
  donde la dejó la pausa.
- **Saltar o reiniciar una canción:** se detecta el salto de posición (atrás > 1,5 s o adelante
  > 5 s) y se reenvía el par con la posición real.

## 5. Contador de repeticiones (`×N`)

`MediaSnapshot.PlayCount` empieza en 1 y solo sube cuando la misma pista **vuelve al principio**
después de haber sonado de verdad:

1. La lectura anterior tenía que estar en reproducción (o al final de la pista, si terminó) y
   haber avanzado al menos 10 s (o la mitad, en pistas cortas).
2. La nueva posición tiene que estar en los primeros 3 s.
3. Se confirma en la lectura siguiente: la pista sigue sonando y la posición avanzó al ritmo
   real. Así no cuentan un arrastre de la barra ni un dato momentáneo en `0:00`.

La identidad de pista compara AUMID, título, artista, álbum y duración (±2 s), para no mezclar
una versión de estudio con un directo. El contador es en memoria: se reinicia al cerrar la app.

## 6. Límite de Discord y publicador

Discord acepta unas **5 actualizaciones cada 20 s**.

- El bucle encola la actividad en un **publicador con un solo hueco**: lo nuevo sustituye a lo
  pendiente. Si Discord está en su límite, el bucle sigue leyendo SMTC y la bandeja responde.
- Una **ventana de asentamiento de 140 ms** agrupa ráfagas de saltos: solo se envía la última
  canción, no una por pista.
- Si llega una actividad nueva mientras otra espera hueco, la espera se cancela y no consume
  ninguna actualización. La escritura al pipe nunca se cancela a medias.
- `MinUpdateIntervalMs` solo espacia los envíos que **no cambian lo que se ve** (latido y
  reanclaje de pausa). Los cambios de pista, reproductor o portada son urgentes.

## 7. Configuración

Archivo: `%APPDATA%\UniversalPresence\config.json`. Se crea con los valores por defecto en el
primer arranque y se aplica en caliente al guardar desde la ventana de configuración.

| Clave | Por defecto | Descripción |
| --- | --- | --- |
| `DiscordClientId` | `1558323929766953042` | Application ID incluido. Solo hace falta cambiarlo si usas tu propia aplicación. |
| `PollIntervalMs` | `1500` |
| `MinUpdateIntervalMs` | `4000` | Separación mínima entre actualizaciones que no cambian lo visible. |
| `HeartbeatSeconds` | `60` | Re-publicación periódica para mantener viva la conexión. `0` la desactiva. |
| `ShowProgress` | `true` | Envía `timestamps` para que Discord muestre la barra. |
| `ShowAlbum` | `true` | Muestra el álbum en el tooltip de la portada. |
| `UsePlayerNameAsActivityName` | `true` | La línea superior muestra el reproductor en vez del nombre de la app. |
| `AppendPlayerToState` | `false` | Añade «· Reproductor» a la segunda línea. |
| `HideIfNoArtwork` | `false` | No publica si la pista no tiene portada. |
| `StartWithWindows` | `true` | Inicio automático con Windows (HKCU, sin administrador). |
| `StartPaused` | `false` | Arranca sin publicar. |
| `ActivityType` | `"Listening"` | `Playing`, `Listening`, `Watching` o `Competing`. |
| `ShowPausedMedia` | `true` | Mantiene la canción visible al pausar. `false` limpia la presencia. |
| `PauseResetProgress` | `true` | Barra congelada en `0:00` al pausar; `false` la congela en la posición. |
| `PauseRefreshSeconds` | `10` | Cada cuánto se reancla el par congelado. `0` = una sola vez. |
| `ShowPausedIcon` | `false` | Al pausar, usa `PauseAssetKey` como icono pequeño. |
| `PauseAssetKey` / `AppIconAssetKey` / `PlayerAssetKey` | `""` | Assets subidos en el portal de Discord. |
| `Artwork.Mode` | `"Auto"` | `Auto`, `ExternalUrl`, `Lookup`, `DiscordAsset` o `None`. |
| `Artwork.UploadMode` | `"Catbox"` | `Catbox` (Litterbox, sin clave), `Imgur`, `SelfHosted` (host propio permanente) o `None`. |
| `Artwork.UploadApiKey` | `""` | Client-ID de Imgur o token del host propio. |
| `Artwork.UploadUrl` | `""` | URL base del host propio de carátulas (SelfHosted). |
| `Artwork.ExternalUrlBase` | `""` | Base HTTPS propia para servir las carátulas. |
| `Artwork.FallbackAssetKey` | `""` | Asset de respaldo sin portada. |
| `IgnoredPlayers` | navegadores | Lista **negra** por subcadena sobre AUMID, proceso y nombre. |
| `PlayerNameOverrides` | `{}` | `{"AUMID": "Nombre"}`. Solo cambia el nombre, nunca filtra. |
| `TreatStoppedAsPaused` | `false` | Para integraciones que reportan `Stopped` al pausar. |
| `ClearWhenIdle` | `false` | Oculta la presencia si el PC está inactivo o bloqueado. |
| `IdleSeconds` | `600` | Segundos sin actividad para considerar ausencia (30–86400). |
| `ResyncProgressDrift` | `true` | Red de seguridad: reenvía los timestamps si la posición real se aleja más de 2,5 s de la barra publicada (como mucho cada 5 s). Los seeks/arrastres de la barra se detectan por el evento de timeline de SMTC y se reenvían al instante. |
| `ResyncEverySeconds` | `5` | Cada cuántos segundos se compara la barra con la posición real y se reenvía si se desvió más de 400 ms. `0` desactiva la comprobación. |
| `UpdateChannel` | `"stable"` | `stable` o `beta` (incluye pre-releases). |
| `CoverOverrides` | `{}` | `{"Spotify.exe": "https://…"}` o ruta de archivo: portada propia por app. |
| `LastFmApiKey` / `LastFmSecret` | `""` | Credenciales de la API de Last.fm (cifradas en `secrets.dpapi.json`). |
| `LastFmUser` / `LastFmSessionKey` | `""` | Sesión obtenida por el botón «Conectar con Last.fm». |
| `LastFmNowPlaying` | `true` | Publica «escuchando ahora» además del scrobble. |
| `ListeningHistory` | `true` | Guarda el historial local de escucha. |
| `ListeningHistoryRetentionDays` / `ListeningHistoryMaxEntries` | `90` / `5000` | Retención del historial antes de recortar. |
| `LogLevel` | `"Info"` | `Debug`, `Info`, `Warn` o `Error`. |

Variables de entorno:

- `UNIVERSAL_PRESENCE_IPC_PIPE`: fuerza un único named pipe de Discord (diagnóstico).

## 8. Compilar y diagnosticar

```bash
dotnet build UniversalPresence.csproj -c Release

dotnet publish UniversalPresence.csproj -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:DebugType=None -o publish
```

Modos de consola:

```bash
./publish/UniversalPresence.exe --list      # volcado de sesiones SMTC y salir
./publish/UniversalPresence.exe --test      # imprime cada lectura y el JSON
./publish/UniversalPresence.exe --settings  # bandeja + configuración
./publish/UniversalPresence.exe --verbose   # nivel Debug
```

Registro: `%LOCALAPPDATA%\UniversalPresence\logs\presence.log` (rotación a 2 MB).

## 9. Limitaciones conocidas

- Una sola actividad a la vez: si suenan dos reproductores, se publica el que esté en
  reproducción (o el que Windows marque como sesión actual).
- Algunos reproductores no exponen carátula o duración por SMTC; se muestran los campos que sí
  ofrecen.
- Las carátulas subidas a Litterbox caducan a las 72 h; la URL se renueva al fallar.
- El ejecutable no está firmado: SmartScreen puede avisar la primera vez.
- El contador de repeticiones se guarda en memoria, no entre sesiones.

## 10. Publicación

- `.github/workflows/build.yml` compila en cada push a `main` y en cada pull request.
- `.github/workflows/release.yml` se dispara con una etiqueta `v*`, publica el `.exe`
  autocontenido y crea la release.
- La web (`docs/`) se publica con GitHub Pages desde la rama `main`, carpeta `/docs`.
