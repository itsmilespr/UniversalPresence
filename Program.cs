using UniversalPresence.Cli;
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
