namespace UniversalPresence.Cli;

/// <summary>
/// Command line switches.
///   (none)            tray mode, no window, only a notification-area icon
///   --test / --console  headless diagnostics loop, prints every detected session
///   --list              one-shot dump of every SMTC session, then exit
    ///   --settings          tray mode, opening the settings window on start
    ///   --dry-run           never talk to Discord
/// </summary>
public sealed class CliOptions
{
    public bool ConsoleMode { get; private set; }
    public bool ListSessions { get; private set; }
    public bool DryRun { get; private set; }
    public bool Verbose { get; private set; }
    public bool Paused { get; private set; }
    public bool Settings { get; private set; }
    public bool ShowHelp { get; private set; }
    public string? ClientId { get; private set; }
    public string? ConfigPath { get; private set; }
    public int Seconds { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var cli = new CliOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i].Trim();
            switch (arg.ToLowerInvariant())
            {
                case "--test":
                case "--console":
                case "-t":
                    cli.ConsoleMode = true;
                    break;
                case "--list":
                case "-l":
                    cli.ListSessions = true;
                    break;
                case "--dry-run":
                case "--no-discord":
                    cli.DryRun = true;
                    break;
                case "--verbose":
                case "-v":
                    cli.Verbose = true;
                    break;
                case "--paused":
                    cli.Paused = true;
                    break;
                case "--settings":
                    cli.Settings = true;
                    break;
                case "--client-id":
                    cli.ClientId = NextValue(args, ref i);
                    break;
                case "--config":
                    cli.ConfigPath = NextValue(args, ref i);
                    break;
                case "--seconds":
                    cli.Seconds = int.TryParse(NextValue(args, ref i), out var seconds) ? Math.Clamp(seconds, 1, 86_400) : 0;
                    break;
                case "--help":
                case "-h":
                case "/?":
                case "/help":
                    cli.ShowHelp = true;
                    break;
                default:
                    if (arg.Length > 0)
                    {
                        Console.Error.WriteLine($"Argumento desconocido: {arg}");
                    }

                    break;
            }
        }

        if (cli.ListSessions)
        {
            cli.ConsoleMode = true;
        }

        return cli;
    }

    private static string? NextValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            return null;
        }

        index++;
        return args[index];
    }

    public static void PrintHelp()
    {
        Console.WriteLine(
            """
            Universal Presence — Rich Presence universal para Windows (SMTC), sin listas blancas.

            USO
              UniversalPresence.exe                 Icono en la bandeja del sistema (sin ventana).
              UniversalPresence.exe --test          Modo de prueba: imprime todo lo que detecta SMTC.
              UniversalPresence.exe --list           Vuelca las sesiones SMTC y termina.
              UniversalPresence.exe --settings       Starts in the tray and opens the settings.
              UniversalPresence.exe --test --dry-run --seconds 20
                                                     Prueba sin Discord, terminando sola a los 20 s.

            OPCIONES
              --client-id <id>   Discord application id (overrides the configuration).
              --config <ruta>    Ruta alternativa a config.json.
              --paused           Arranca pausado (sin publicar en Discord).
              --verbose          Registro detallado.
              --seconds <n>      In --test/--console mode, exits after n seconds.
              -h, --help         Esta ayuda.

            The configuration file is created only at:
              %APPDATA%\UniversalPresence\config.json
            Logs and covers are stored at:
              %LOCALAPPDATA%\UniversalPresence\
            """);
    }
}
