using FoldersToB2.Backup;
using FoldersToB2.Config;
using FoldersToB2.Notifications;
using Serilog;

namespace FoldersToB2.Tray;

public class BackupTrayApp : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly BackupService _backupService;
    private readonly FileManifest _manifest;
    private bool _isRunning;
    private CancellationTokenSource? _cts;

    public BackupTrayApp(BackupConfig config)
    {
        _manifest = new FileManifest(Path.Combine(AppContext.BaseDirectory, "data", "manifest.db"));

        WebhookNotifier? webhook = null;
        if (!string.IsNullOrWhiteSpace(config.WebhookUrl))
            webhook = new WebhookNotifier(config.WebhookUrl);

        _backupService = new BackupService(config, _manifest, webhook);
        _backupService.StatusChanged += OnStatusChanged;

        _trayIcon = new NotifyIcon
        {
            Icon = CreateIcon(Color.DodgerBlue),
            Text = "FoldersToB2 - Starting...",
            Visible = true,
            ContextMenuStrip = CreateContextMenu(config)
        };

        // Set up recurring timer
        _timer = new System.Windows.Forms.Timer
        {
            Interval = config.BackupFrequencyMinutes * 60 * 1000
        };
        _timer.Tick += async (_, _) => await RunBackupAsync();
        _timer.Start();

        // Run initial backup shortly after startup
        var startDelay = new System.Windows.Forms.Timer { Interval = 2000 };
        startDelay.Tick += async (s, _) =>
        {
            ((System.Windows.Forms.Timer)s!).Stop();
            ((System.Windows.Forms.Timer)s).Dispose();
            await RunBackupAsync();
        };
        startDelay.Start();
    }

    private void OnStatusChanged(string status)
    {
        try
        {
            if (_trayIcon.Visible)
                _trayIcon.Text = TruncateText($"FoldersToB2 - {status}", 127);
        }
        catch
        {
            // Tray icon may be disposed during shutdown
        }
    }

    private ContextMenuStrip CreateContextMenu(BackupConfig config)
    {
        var menu = new ContextMenuStrip();

        var infoItem = menu.Items.Add($"Backup every {config.BackupFrequencyMinutes} min");
        infoItem.Enabled = false;

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Run Backup Now", null, async (_, _) => await RunBackupAsync());
        menu.Items.Add("Open Log Folder", null, (_, _) => OpenLogFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        return menu;
    }

    private async Task RunBackupAsync()
    {
        if (_isRunning)
        {
            Log.Information("Backup already in progress, skipping");
            return;
        }

        _isRunning = true;
        _cts = new CancellationTokenSource();
        _trayIcon.Icon = CreateIcon(Color.Orange);

        try
        {
            Log.Information("=== Backup cycle started ===");
            var success = await _backupService.RunBackupAsync(_cts.Token);
            _trayIcon.Icon = CreateIcon(success ? Color.LimeGreen : Color.Red);
            Log.Information("=== Backup cycle completed ===");
        }
        catch (OperationCanceledException)
        {
            Log.Information("Backup cancelled");
            _trayIcon.Icon = CreateIcon(Color.DodgerBlue);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Backup cycle failed");
            _trayIcon.Icon = CreateIcon(Color.Red);
            _trayIcon.ShowBalloonTip(5000, "Backup Failed", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _isRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static Icon CreateIcon(Color color)
    {
        var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 1, 1, 14, 14);

        using var font = new Font("Segoe UI", 8, FontStyle.Bold);
        using var textBrush = new SolidBrush(Color.White);
        g.DrawString("B", font, textBrush, 2, 0);

        var handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    private static void OpenLogFolder()
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "logs");
        if (!Directory.Exists(logPath))
            Directory.CreateDirectory(logPath);

        System.Diagnostics.Process.Start("explorer.exe", logPath);
    }

    private void ExitApp()
    {
        _cts?.Cancel();
        _timer.Stop();
        _trayIcon.Visible = false;
        _manifest.Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Dispose();
            _timer.Dispose();
            _manifest.Dispose();
        }
        base.Dispose(disposing);
    }

    private static string TruncateText(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";
    }
}
