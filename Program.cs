using UniversalPresence.Cli;
using System.Diagnostics;
using UniversalPresence.Configuration;
using UniversalPresence.Diagnostics;
using UniversalPresence.Infrastructure;
using UniversalPresence.Tray;

namespace UniversalPresence;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var cli = CliOptions.Parse(args);

        // Rollback automático: si la versión anterior no arrancó bien, lanzar el .bak.
        if (!cli.ConsoleMode && !cli.ShowHelp)
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe))
                {
                    var bak = exe + ".bak";
                    var dir = AppOptions.LocalDataDirectory;
                    var flag = Path.Combine(dir, "started.flag");
                    if (File.Exists(bak) && !File.Exists(flag))
                    {
                        Process.Start(new ProcessStartInfo(bak) { UseShellExecute = true });
                        return 0;
                    }
                }
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(cli.SendCommand))
        {
            try
            {
                var dir = AppOptions.LocalDataDirectory;
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "command.txt"), cli.SendCommand.Trim().ToLowerInvariant());
                Console.WriteLine($"Command sent: {cli.SendCommand}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not send the command: {ex.Message}");
                return 1;
            }

            return 0;
        }

        if (cli.ConsoleMode || cli.ShowHelp)
        {
            ConsoleBootstrap.TryAttach();
        }

        if (cli.ShowHelp)
        {
            CliOptions.PrintHelp();
            return 0;
        }

        if (cli.Version)
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version);
            return 0;
        }

        if (cli.SelfTest)
        {
            return SelfTestRunner.Run();
        }

        if (cli.HistoryStats)
        {
            var history = new UniversalPresence.Infrastructure.ListeningHistoryService();
            Console.WriteLine(history.Summary());
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(cli.HistoryExport))
        {
            var history = new UniversalPresence.Infrastructure.ListeningHistoryService();
            Console.WriteLine(history.ExportCsv(cli.HistoryExport!));
            return 0;
        }

        if (cli.Pause || cli.Resume || cli.Toggle || cli.OpenSettings)
        {
            try
            {
                var dir = AppOptions.LocalDataDirectory;
                Directory.CreateDirectory(dir);
                var command = cli.Pause ? "pause" : cli.Resume ? "resume" : cli.Toggle ? "toggle" : "settings";
                File.WriteAllText(Path.Combine(dir, "command.txt"), command);
                Console.WriteLine($"Command sent: {command}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not send the command: {ex.Message}");
                return 1;
            }

            return 0;
        }

        if (cli.Status)
        {
            var statusPath = Path.Combine(AppOptions.LocalDataDirectory, "status.txt");
            if (File.Exists(statusPath))
            {
                Console.WriteLine(File.ReadAllText(statusPath));
            }
            else
            {
                Console.WriteLine("Universal Presence is not running.");
            }
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(cli.Profile))
        {
            try
            {
                var dir = AppOptions.LocalDataDirectory;
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "command.txt"), $"profile:{cli.Profile}");
                Console.WriteLine($"Profile switch requested: {cli.Profile}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not request profile switch: {ex.Message}");
                return 1;
            }

            return 0;
        }

        var options = AppOptions.Load(cli);

        if (cli.DryRun)
        {
            options.DiscordClientId = "";
        }

        using var log = Log.Create(options, cli.ConsoleMode);

        try
        {
            log.Info($"Universal Presence {typeof(Program).Assembly.GetName().Version} · configuration: {options.ConfigPath}");

            if (cli.ConsoleMode)
            {
                return DiagnosticsRunner.RunAsync(options, log, cli).GetAwaiter().GetResult();
            }

            return RunTray(options, log, cli);
        }
        catch (Exception ex)
        {
            log.Error("Error fatal.", ex);
            Console.Error.WriteLine(ex);
            try { CrashDiagnostics.Write(ex, log, options); } catch { }
            return 1;
        }
    }

    private static int RunTray(AppOptions options, Log log, CliOptions cli)
    {
        try
        {
            var dir = AppOptions.LocalDataDirectory;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "started.flag"), DateTime.UtcNow.ToString("O"));
        }
        catch { }

        using var instance = new SingleInstance(@"Local\UniversalPresence.Tray");

        if (!instance.IsFirst)
        {
            log.Info("Another instance of Universal Presence is already running; exiting.");
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // A background app must not vanish on an unexpected UI exception: log it and keep going.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) =>
            log.Error("Unhandled exception in the UI.", eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            log.Error("Unhandled exception.", eventArgs.ExceptionObject as Exception);

        using var context = new TrayApplicationContext(options, log, cli.Settings);
        Application.Run(context);

        log.Info("Application finished.");
        return 0;
    }
}
