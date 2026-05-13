# FoldersToB2

A lightweight Windows tray application that incrementally backs up folders to a Backblaze B2 bucket on a configurable schedule.

## Features

- **Incremental backups** — SHA-256 change detection with a local SQLite manifest. Only changed/new files are uploaded.
- **Large file support** — automatically uses B2's multi-part upload API for files over ~100MB.
- **Mirror structure** — local folder paths are preserved in B2 (e.g. `C:\Users\you\Documents\file.txt` → `Users/you/Documents/file.txt`).
- **`.gitignore` support** — optionally skip files ignored by git, using `git ls-files` to efficiently query each repo in a single call.
- **Configurable exclusions** — exclude folders by name and files by extension.
- **Version cleanup** — automatically deletes old B2 file versions after a configurable retention period (default: 7 days).
- **System tray icon** — color-coded status (blue=idle, orange=running, green=success, red=error) with a context menu.
- **Webhook alerts** — sends notifications on failure via a configurable webhook URL with `{MSG}` and `{DESC}` variable substitution.
- **Retry with backoff** — failed uploads are retried up to 3 times with exponential backoff and automatic re-authorization.
- **Locked file handling** — files locked by other processes are skipped with a warning.
- **Rolling logs** — daily log files with 30-day retention.

## Requirements

- Windows 10 or later
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (or build as self-contained)
- [Git](https://git-scm.com/) on PATH (only if `respectGitIgnore` is enabled)

## Setup

### 1. Build

```powershell
dotnet build src/FoldersToB2 -c Release
```

Or publish as a self-contained single file:

```powershell
dotnet publish src/FoldersToB2 -c Release -r win-x64 --self-contained
```

### 2. Configure

Copy the example config and fill in your values:

```powershell
copy src\FoldersToB2\appsettings-example.json src\FoldersToB2\appsettings.json
```

Edit `appsettings.json` (or `appsettings.jsonc` — both are supported):

| Setting | Description |
|---|---|
| `backblaze.applicationKeyId` | B2 Application Key ID (from B2 > Application Keys) |
| `backblaze.applicationKey` | B2 Application Key (shown once at creation) |
| `backblaze.bucketId` | B2 Bucket ID (from B2 > Buckets) |
| `backupFrequencyMinutes` | How often to run backups (default: `15`) |
| `folders` | List of local folders to back up |
| `files` | List of individual file paths to back up |
| `excludeFolders` | Folder names to skip at any depth (e.g. `node_modules`, `.git`) |
| `excludeFileTypes` | File extensions to skip (e.g. `.tmp`, `.log`) |
| `respectGitIgnore` | Skip files matching `.gitignore` rules (default: `true`) |
| `webhookUrl` | URL for failure alerts — supports `{MSG}` and `{DESC}` placeholders |
| `oldVersionRetentionDays` | Days to keep old file versions in B2 (default: `7`) |

### 3. Run

Double-click the built `.exe`, or run from terminal:

```powershell
src\FoldersToB2\bin\Release\net8.0-windows\FoldersToB2.exe
```

A tray icon will appear. Right-click for options:
- **Run Backup Now** — trigger an immediate backup
- **Open Log Folder** — view log files
- **Exit** — stop the app

### 4. Start with Windows (optional)

Run the install script as Administrator to register a Task Scheduler task:

```powershell
powershell -ExecutionPolicy Bypass -File install-task.ps1
```

This starts the app at logon. To remove:

```powershell
Unregister-ScheduledTask -TaskName "FoldersToB2"
```

## Tray Icon Colors

| Color | Meaning |
|---|---|
| 🔵 Blue | Idle / starting |
| 🟠 Orange | Backup in progress |
| 🟢 Green | Last backup succeeded |
| 🔴 Red | Last backup failed |

## Logs

Logs are written to the `logs/` folder next to the executable, with daily rotation and 30-day retention.

## License

MIT
