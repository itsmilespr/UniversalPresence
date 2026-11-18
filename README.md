# Universal Presence

**Rich Presence para Discord hecho para Windows.** Muestra lo que estás escuchando —en
cualquier reproductor— con carátula, barra de progreso e icono del programa, leyendo las
sesiones de medios del sistema (SMTC). Sin listas blancas, sin cuenta y sin configurar nada.

[![Última versión](https://img.shields.io/github/v/release/itsmilespr/UniversalPresence?label=descargar&color=6d5efc)](https://github.com/itsmilespr/UniversalPresence/releases/latest)
[![Licencia MIT](https://img.shields.io/badge/licencia-MIT-6ee7b7)](LICENSE)
[![Windows 10 y 11](https://img.shields.io/badge/Windows-10%20%7C%2011-4f8cff)](#requisitos)

**[Descargar la última versión](https://github.com/itsmilespr/UniversalPresence/releases/latest)** ·
[Página web](https://itsmilespr.github.io/UniversalPresence/) ·
[Documentación técnica](TECNICO.md)

---

## Qué hace

Abre el ejecutable, reproduce música y tu perfil de Discord muestra la canción con su portada,
el artista, el álbum, una barra de progreso sincronizada y el icono del reproductor. Si repites
la misma canción, añade un contador `×2`, `×3`…

No hay que crear una aplicación de Discord, ni pegar tokens, ni elegir tu reproductor en una
lista: si Windows expone la sesión, se publica.

## Características

- **Sin lista blanca.** Spotify, foobar2000 (Gatefold), VLC, Apple Music, MusicBee, TIDAL,
  Windows Media Player… cualquier reproductor que use SMTC.
- **Carátula real.** Se toma del propio reproductor; si no la expone, se busca en iTunes y
  Deezer en paralelo, con caché en disco para no repetir peticiones.
- **Barra sincronizada.** Timestamps absolutos, reanclaje al pausar y posición real al
  reanudar: la barra acompaña al audio aunque saltes canciones.
- **Cambios al instante.** La app escucha los eventos de Windows y publica en un hilo aparte,
  con un publicador que solo conserva la última actividad: el límite de Discord (5
  actualizaciones cada 20 s) nunca bloquea la detección ni los saltos.
- **Contador de repeticiones.** Detecta que has vuelto a poner la misma canción y muestra
  `×N` en el estado, sin contar arrastres de la barra ni fallos del reproductor.
- **Icono nítido del reproductor.** Extraído a 256 px del propio `.exe` y cacheado.
- **Pausa cuidado.** La carátula nunca desaparece, la barra se congela y se puede mostrar un
  badge de pausa.
- **Todo local.** Sin cuenta, sin telemetría y sin servidores propios: solo el IPC local de
  Discord.
- **Un solo `.exe`.** Autocontenido, sin instalar .NET. Inicio con Windows opcional (activado
  por defecto) desde la configuración.

## Requisitos

- Windows 10 (1809 o superior) o Windows 11, **64 bits**.
- Discord abierto para que se muestre la actividad.
- Para usar el ejecutable publicado: **nada más** (incluye el runtime de .NET 8).
- Para compilar: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

## Instalación

1. Descarga `UniversalPresence.exe` desde
   [Releases](https://github.com/itsmilespr/UniversalPresence/releases/latest).
2. Ejecútalo. Aparece en la bandeja del sistema, sin ventana principal.
3. Reproduce música y abre Discord.

No hace falta instalar nada ni ejecutar como administrador. Windows puede mostrar el aviso de
SmartScreen porque el binario no está firmado: «Más información» → «Ejecutar de todas formas».

## Uso

El menú del icono de la bandeja tiene lo esencial:

| Opción | Qué hace |
| --- | --- |
| Estado | Canción actual, reproductor y estado de la conexión con Discord. |
| Pausar / Reanudar | Deja de publicar (o limpia la presencia) sin cerrar la app. |
| Configuración… | Ventana con las opciones: inicio con Windows, reproducciones ignoradas, barra, pausa y carátulas. |
| Abrir carpeta de registros | `%LOCALAPPDATA%\UniversalPresence\logs`. |
| Salir | Cierra la app y limpia la actividad de Discord. |

La configuración se guarda en `%APPDATA%\UniversalPresence\config.json` y se aplica en caliente
al guardar. Todas las claves están documentadas en [TECNICO.md](TECNICO.md#configuración).

## Compilar desde el código

```bash
# Compilar
dotnet build UniversalPresence.csproj -c Release

# Ejecutable autocontenido de un solo archivo
dotnet publish UniversalPresence.csproj -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:DebugType=None -o publish
```

El proyecto usa .NET 8 (WinForms) y **no tiene dependencias NuGet**: el protocolo IPC de
Discord está implementado a mano. En cada push se compila con GitHub Actions y al empujar una
etiqueta `v*` se publica la release con el `.exe` adjunto.

## Privacidad

Universal Presence no tiene servidores ni cuentas. Solo escribe en tu equipo y habla con Discord
por su canal local. Para que Discord pueda mostrar la carátula extraída, la sube a Litterbox
(catbox.moe); se puede desactivar con `"Artwork": { "UploadMode": "None" }`. Para buscar una
portada cuando el reproductor no la ofrece, envía título/artista/álbum a las API públicas de
iTunes y Deezer; se puede desactivar con `"Artwork": { "Mode": "None" }`.

## Limitaciones conocidas

- Una sola actividad a la vez: si suenan dos reproductores, se publica el que esté en
  reproducción (o el que Windows marque como sesión actual).
- Los navegadores están excluidos por defecto para que una pestaña de YouTube no aparezca como
  música; se puede cambiar en «Reproductores ignorados».
- Algunos reproductores no exponen carátula o duración por SMTC: se muestran los campos que sí
  ofrecen.

## Licencia

[MIT](LICENSE). Puedes usarlo, modificarlo y redistribuirlo conservando el aviso de copyright.
