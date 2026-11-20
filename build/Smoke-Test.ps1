<#
.SYNOPSIS
  Runs the built package on the CI machine: tray app starts FancyZones, the editor opens,
  export/import round-trips. Saves screenshots to $ShotDir.
#>
param([Parameter(Mandatory)][string]$Pkg, [string]$ShotDir = "screenshots")

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
New-Item -ItemType Directory -Force $ShotDir | Out-Null
$ShotDir = (Resolve-Path $ShotDir).Path
$failures = @()

function Shot($name) {
    try {
        $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
        $bmp.Save((Join-Path $ShotDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Write-Host "[smoke] screenshot $name.png"
    } catch { Write-Warning "screenshot $name failed: $_" }
}
function Wait-Proc($name, $sec) {
    $end = (Get-Date).AddSeconds($sec)
    while ((Get-Date) -lt $end) { if (Get-Process $name -ErrorAction SilentlyContinue) { return $true }; Start-Sleep -Milliseconds 500 }
    return $false
}

$mgr = Join-Path $Pkg 'FancyZonesStandalone.exe'

# 1. Tray app starts FancyZones.
Start-Process $mgr -ArgumentList '--show-menu'
if (Wait-Proc 'PowerToys.FancyZones' 20) { Write-Host "[smoke] FancyZones is running" } else { $failures += 'FancyZones did not start' }
Start-Sleep 4
Shot 'tray-menu'
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')

# 2. Second launch asks FancyZones to open the editor.
Start-Process $mgr
if (Wait-Proc 'PowerToys.FancyZonesEditor' 30) {
    Start-Sleep 10
    if (Get-Process 'PowerToys.FancyZonesEditor' -ErrorAction SilentlyContinue) { Write-Host "[smoke] editor is open"; Shot 'editor' }
    else { $failures += 'Editor exited right after starting' }
} else { $failures += 'Editor did not open' }

# The "Please report the bug" dialog would show up in the editor log.
$logs = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\PowerToys\FancyZones\Editor\Logs" -Recurse -File -ErrorAction SilentlyContinue
foreach ($l in $logs) { $bad = Select-String -Path $l.FullName -Pattern 'Unhandled exception' -SimpleMatch; if ($bad) { $failures += "Editor log: $($bad[0].Line)" } }
Get-Process 'PowerToys.FancyZonesEditor' -ErrorAction SilentlyContinue | Stop-Process -Force

# 3. Export / import round trip via the CLI.
$zip = Join-Path $env:RUNNER_TEMP 'fz-export.zip'
$p = Start-Process $mgr -ArgumentList "--export `"$zip`"" -Wait -PassThru
if ($p.ExitCode -ne 0 -or -not (Test-Path $zip)) { $failures += 'Export failed' } else { Write-Host "[smoke] export OK ($((Get-Item $zip).Length) bytes)" }
$p = Start-Process $mgr -ArgumentList "--import `"$zip`"" -Wait -PassThru
if ($p.ExitCode -ne 0) { $failures += 'Import failed' } else { Write-Host "[smoke] import OK" }

Get-Process 'FancyZonesStandalone', 'PowerToys.FancyZones*' -ErrorAction SilentlyContinue | Stop-Process -Force

if ($failures) { $failures | ForEach-Object { Write-Host "::error::$_" }; exit 1 }
Write-Host "[smoke] all checks passed"
