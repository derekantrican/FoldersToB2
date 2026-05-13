using FoldersToB2.Config;
using FoldersToB2.Tray;
using Serilog;

namespace FoldersToB2;

static class Program
{
    [STAThread]
    static void Main()
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "backup-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            Log.Information("FoldersToB2 starting");

            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.jsonc");
            if (!File.Exists(configPath))
                configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var config = ConfigLoader.Load(configPath);

            Log.Information("Configuration loaded: {FolderCount} folders, frequency {Freq} min",
                config.Folders.Count, config.BackupFrequencyMinutes);

            ApplicationConfiguration.Initialize();
            Application.Run(new BackupTrayApp(config));
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application failed to start");
            MessageBox.Show(
                $"Failed to start FoldersToB2:\n\n{ex.Message}\n\nCheck appsettings.json and try again.",
                "FoldersToB2", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Log.Information("FoldersToB2 shutting down");
            Log.CloseAndFlush();
        }
    }
}

