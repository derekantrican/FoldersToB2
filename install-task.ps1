# install-task.ps1
# Registers a Windows Task Scheduler task to start FoldersToB2 at user logon.
# Run this script as Administrator.

param(
    [string]$ExePath = (Join-Path $PSScriptRoot "src\FoldersToB2\bin\Release\net8.0-windows\FoldersToB2.exe"),
    [string]$TaskName = "FoldersToB2"
)

# Validate the exe exists
if (-not (Test-Path $ExePath)) {
    Write-Host "ERROR: Could not find FoldersToB2.exe at:" -ForegroundColor Red
    Write-Host "  $ExePath" -ForegroundColor Red
    Write-Host ""
    Write-Host "Build the project first:" -ForegroundColor Yellow
    Write-Host "  dotnet publish src/FoldersToB2 -c Release" -ForegroundColor Yellow
    exit 1
}

$ExePath = (Resolve-Path $ExePath).Path
$WorkingDir = Split-Path $ExePath

# Remove existing task if present
$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Removing existing task '$TaskName'..." -ForegroundColor Yellow
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

# Create the task
$action = New-ScheduledTaskAction -Execute $ExePath -WorkingDirectory $WorkingDir

$trigger = New-ScheduledTaskTrigger -AtLogOn

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 5)

$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $principal `
    -Description "Backs up configured folders to Backblaze B2 on a schedule." | Out-Null

Write-Host ""
Write-Host "Task '$TaskName' registered successfully!" -ForegroundColor Green
Write-Host "  Exe:         $ExePath" -ForegroundColor Cyan
Write-Host "  Trigger:     At logon" -ForegroundColor Cyan
Write-Host "  Working dir: $WorkingDir" -ForegroundColor Cyan
Write-Host ""
Write-Host "The app will start automatically at next logon." -ForegroundColor White
Write-Host "To start it now:  Start-ScheduledTask -TaskName '$TaskName'" -ForegroundColor White
Write-Host "To remove it:     Unregister-ScheduledTask -TaskName '$TaskName'" -ForegroundColor White
