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

        if (!string.IsNullOrWhiteSpace(cli.SendCommand))
        {
            try
            {
                WriteCommand(cli.SendCommand.Trim().ToLowerInvariant());
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
                var command = cli.Pause ? "pause" : cli.Resume ? "resume" : cli.Toggle ? "toggle" : "settings";
                WriteCommand(command);
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
                WriteCommand($"profile:{cli.Profile}");
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
            Directory.CreateDirectory(AppOptions.LocalDataDirectory);
        }
        catch { }

        using var instance = new SingleInstance(@"Local\UniversalPresence.Tray");

        if (!instance.IsFirst)
        {
            log.Info("Another instance of Universal Presence is already running; exiting.");
            return 0;
        }

        // Rollback automático: si la versión recién instalada no llegó a arrancar la vez
        // anterior, se lanza la versión guardada en .bak en lugar de dejar la app muerta.
        if (UpdateService.CheckStartupGuard(options.AutoRollback, log))
        {
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // A background app must not vanish on an unexpected UI exception: log it and keep going.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) =>
            ReportCrash(eventArgs.Exception, log, options);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            ReportCrash(eventArgs.ExceptionObject as Exception, log, options);

        using var context = new TrayApplicationContext(options, log, cli.Settings);

        // La ventana y el motor ya se construyeron: si la actualización tocaba este arranque,
        // queda confirmada y se deja de vigilar el rollback.
        UpdateService.ConfirmStartup(log);

        Application.Run(context);

        log.Info("Application finished.");
        return 0;
    }

    /// <summary>
    /// Deja un comando para la instancia en marcha. Se escribe aparte y se renombra porque
    /// la app lo lee y lo borra en cuanto aparece: escribiendo directo, una lectura en medio
    /// podía ver el archivo vacío y perder el comando.
    /// </summary>
    private static void WriteCommand(string command)
    {
        var dir = AppOptions.LocalDataDirectory;
        Directory.CreateDirectory(dir);

        var target = Path.Combine(dir, "command.txt");
        var temp = target + ".tmp";

        File.WriteAllText(temp, command);
        File.Move(temp, target, overwrite: true);
    }

    private static DateTimeOffset _lastCrashBundleAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Registra un fallo no controlado y, como mucho una vez cada diez minutos, deja un
    /// paquete de diagnóstico listo para enviar. Nada se manda fuera del equipo: el zip se
    /// queda en la carpeta de crashes hasta que el usuario decida compartirlo.
    /// </summary>
    private static void ReportCrash(Exception? exception, Log log, AppOptions options)
    {
        log.Error("Unhandled exception.", exception);

        var now = DateTimeOffset.UtcNow;
        if (now - _lastCrashBundleAt < TimeSpan.FromMinutes(10))
        {
            return;
        }

        _lastCrashBundleAt = now;

        try
        {
            var path = CrashDiagnostics.Write(exception, log, options);
            log.Info($"Paquete de diagnóstico guardado en {path}");
        }
        catch
        {
            // El diagnóstico nunca debe empeorar el fallo.
        }
    }
}
