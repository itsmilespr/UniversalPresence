namespace UniversalPresence.Cli;

/// <summary>
/// Command line switches (see <see cref="PrintHelp"/> for the text shown to the user).
///   (none)              tray mode, no window, only a notification-area icon
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
    public bool Pause { get; private set; }
    public bool Resume { get; private set; }
    public bool Toggle { get; private set; }
    public bool Status { get; private set; }
    public bool OpenSettings { get; private set; }
    public bool Version { get; private set; }
    public bool SelfTest { get; private set; }
    public bool HistoryStats { get; private set; }
    public string? HistoryExport { get; private set; }
    public string? Profile { get; private set; }
    public string? SendCommand { get; private set; }
    public bool ShowHelp { get; private set; }
    public string? ClientId { get; private set; }
    public string? ConfigPath { get; internal set; }
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
                case "--send":
                    cli.SendCommand = NextValue(args, ref i);
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
                case "--pause":
                    cli.Pause = true;
                    break;
                case "--resume":
                    cli.Resume = true;
                    break;
                case "--toggle":
                    cli.Toggle = true;
                    break;
                case "--status":
                    cli.Status = true;
                    break;
                case "--open-settings":
                    cli.OpenSettings = true;
                    break;
                case "--version":
                    cli.Version = true;
                    break;
                case "--self-test":
                    cli.SelfTest = true;
                    break;
                case "--history-stats":
                    cli.HistoryStats = true;
                    break;
                case "--history-export":
                    cli.HistoryExport = NextValue(args, ref i);
                    break;
                case "--profile":
                    cli.Profile = NextValue(args, ref i);
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
                        Console.Error.WriteLine($"Unknown argument: {arg}");
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
            Universal Presence — Universal Rich Presence for Windows (SMTC), no whitelists.

            USAGE
              UniversalPresence.exe                 System tray icon only (no window).
              UniversalPresence.exe --test          Test mode: prints everything SMTC detects.
              UniversalPresence.exe --list           Dumps SMTC sessions and exits.
              UniversalPresence.exe --settings       Starts in the tray and opens the settings.
              UniversalPresence.exe --test --dry-run --seconds 20
                                                     Test without Discord, exits after 20 s.

            OPTIONS
              --client-id <id>   Discord application id (overrides the configuration).
              --config <path>    Alternative path to config.json.
              --paused           Start paused (does not publish to Discord).
              --verbose          Verbose logging.
              --seconds <n>      In --test/--console mode, exits after n seconds.
              -h, --help         This help.

            The configuration file is created only at:
              %APPDATA%\UniversalPresence\config.json
            Logs and covers are stored at:
              %LOCALAPPDATA%\UniversalPresence\
            """);
    }
}
