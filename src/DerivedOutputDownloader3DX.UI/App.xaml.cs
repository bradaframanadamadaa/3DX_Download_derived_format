using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using DerivedOutputDownloader3DX.Configuration;
using DerivedOutputDownloader3DX.UI.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace DerivedOutputDownloader3DX.UI;

public partial class App : Application
{
    public static ThreeExperienceOptions Options { get; private set; } = new();
    public static ILoggerFactory LogFactory { get; private set; } = null!;

    /// <summary>Chemin du fichier log courant (accessible depuis les fenêtres).</summary>
    public static string LogFilePath { get; private set; } = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Gestionnaires d'exceptions globaux ──────────────────────────────
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "[CRASH] DispatcherUnhandledException");
            Log.CloseAndFlush();
            MessageBox.Show(
                $"Erreur inattendue :\n{args.Exception.Message}\n\nVoir le log : {LogFilePath}",
                "Erreur critique", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true; // empêche la fermeture silencieuse
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Log.Fatal(ex, "[CRASH] UnhandledException (terminaison={Terminating})", args.IsTerminating);
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "[TASK] UnobservedTaskException");
            args.SetObserved(); // évite le crash sur les Tasks oubliées
        };

        LoadConfiguration();
        ConfigureLogging();

        Log.Information("=== Application démarrée (version Release) ===");
        Log.Information("appsettings.json : PlatformUrl={Url}", Options.PlatformUrl);

        var loginWindow = new LoginWindow(Options, LogFactory);
        loginWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("=== Application fermée ===");
        Log.CloseAndFlush();
        LogFactory?.Dispose();
        base.OnExit(e);
    }

    // ── Configuration Serilog + ILoggerFactory ──────────────────────────────
    private static void ConfigureLogging()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DerivedOutputDownloader3DX", "logs");
        Directory.CreateDirectory(logDir);

        LogFilePath = Path.Combine(logDir, "app-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                LogFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        LogFactory = new SerilogLoggerFactory(Log.Logger, dispose: false);
    }

    // ── Chargement appsettings.json ─────────────────────────────────────────
    private static void LoadConfiguration()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json")
        };

        var settingsPath = candidates.FirstOrDefault(File.Exists);
        if (settingsPath == null)
        {
            MessageBox.Show(
                "appsettings.json introuvable.\n\n" +
                "Assurez-vous que le fichier est présent à côté de l'exécutable.",
                "Configuration manquante",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var config = new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(settingsPath)!)
            .AddJsonFile(Path.GetFileName(settingsPath), optional: false, reloadOnChange: false)
            .Build();

        Options = config.GetSection(ThreeExperienceOptions.SectionName)
                        .Get<ThreeExperienceOptions>() ?? new ThreeExperienceOptions();
    }
}

// ── Convertisseurs ──────────────────────────────────────────────────────────

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is Visibility.Visible;
}

public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}
