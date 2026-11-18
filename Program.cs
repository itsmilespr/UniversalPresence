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
