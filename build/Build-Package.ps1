<#
.SYNOPSIS
  Builds the FancyZones Standalone zip from an official PowerToys release.

.DESCRIPTION
  1. Downloads PowerToysUserSetup-<ver>-x64.exe and checks its Microsoft signature.
  2. Extracts the MSI from the bundle (WiX `burn extract`, falling back to `/layout`).
  3. Unpacks the MSI with an admin install (`msiexec /a`, runs no custom actions).
  4. Copies only what FancyZones needs: the editor's deps.json file list, FancyZones.exe and its
     PowerToys-local imports, plus the tray companion app.

.EXAMPLE
  pwsh build/Build-Package.ps1 -ManagerDir src/FancyZonesStandalone/bin/Release/net48
#>
param(
    [string]$Tag = "",                  # PowerToys tag, e.g. v0.102.3001. Blank = latest stable.
    [int]$Build = 0,                    # CI build number (used by the updater)
    [string]$FzHash = "",               # hash of the FancyZones source dirs at $Tag
    [Parameter(Mandatory)][string]$ManagerDir,
    [string]$OutDir = "out",
    [string]$WorkDir = "work"
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Log($m) { Write-Host "[build] $m" }

$headers = @{ 'User-Agent' = 'FancyZonesStandalone-CI' }
if ($env:GH_TOKEN) { $headers['Authorization'] = "Bearer $($env:GH_TOKEN)" }

New-Item -ItemType Directory -Force $WorkDir, $OutDir | Out-Null
$WorkDir = (Resolve-Path $WorkDir).Path
$OutDir = (Resolve-Path $OutDir).Path
$ManagerDir = (Resolve-Path $ManagerDir).Path

# --- 1. Download ---------------------------------------------------------------------------
$api = if ($Tag) { "https://api.github.com/repos/microsoft/PowerToys/releases/tags/$Tag" }
       else { "https://api.github.com/repos/microsoft/PowerToys/releases/latest" }
$rel = Invoke-RestMethod $api -Headers $headers
$Tag = $rel.tag_name
$version = $Tag.TrimStart('v')
$asset = $rel.assets | Where-Object { $_.name -match '^PowerToysUserSetup-.*-x64\.exe$' } | Select-Object -First 1
if (-not $asset) { throw "No PowerToysUserSetup x64 asset in $Tag" }
Log "PowerToys $Tag -> $($asset.name)"

$setup = Join-Path $WorkDir $asset.name
if (-not (Test-Path $setup)) { Invoke-WebRequest $asset.browser_download_url -OutFile $setup -Headers @{ 'User-Agent' = 'FancyZonesStandalone-CI' } }

$sig = Get-AuthenticodeSignature $setup
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    throw "Installer signature check failed: $($sig.Status) / $($sig.SignerCertificate.Subject)"
}
Log "Signature OK: $($sig.SignerCertificate.Subject)"

# --- 2. Get the MSI out of the Burn bundle -------------------------------------------------
function Find-Msi($dir) {
    $msi = Get-ChildItem $dir -Recurse -File | Where-Object { $_.Extension -eq '.msi' } | Sort-Object Length -Descending | Select-Object -First 1
    if ($msi) { return $msi.FullName }
    # burn extract may write payloads without extensions; detect the OLE compound-file header.
    foreach ($f in Get-ChildItem $dir -Recurse -File | Sort-Object Length -Descending) {
        $b = [byte[]]::new(8); $s = [IO.File]::OpenRead($f.FullName); [void]$s.Read($b, 0, 8); $s.Close()
        if (($b -join ',') -eq '208,207,17,224,161,177,26,225') { return $f.FullName }
    }
    return $null
}

$bundleDir = Join-Path $WorkDir 'bundle'
Remove-Item $bundleDir -Recurse -Force -ErrorAction SilentlyContinue
$msiPath = $null
try {
    if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
        dotnet tool install --global wix --version 6.* | Out-Host
        $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
    }
    wix burn extract $setup -o $bundleDir | Out-Host
    $msiPath = Find-Msi $bundleDir
} catch { Log "wix burn extract failed: $_" }

if (-not $msiPath) {
    Log "Falling back to setup /layout"
    Start-Process $setup -ArgumentList "/layout `"$bundleDir`" /quiet" -Wait
    $msiPath = Find-Msi $bundleDir
}
if (-not $msiPath) { throw "Could not extract the MSI from $($asset.name)" }
Log "MSI: $msiPath"

# --- 3. Admin-install the MSI to a folder ---------------------------------------------------
$msiDir = Join-Path $WorkDir 'msi'
Remove-Item $msiDir -Recurse -Force -ErrorAction SilentlyContinue
$p = Start-Process msiexec.exe -ArgumentList "/a `"$msiPath`" /qn TARGETDIR=`"$msiDir`" /l*v `"$WorkDir\msi.log`"" -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "msiexec /a failed with exit code $($p.ExitCode) (see msi.log)" }

$fzExe = Get-ChildItem $msiDir -Recurse -Filter 'PowerToys.FancyZones.exe' | Select-Object -First 1
$edExe = Get-ChildItem $msiDir -Recurse -Filter 'PowerToys.FancyZonesEditor.exe' | Select-Object -First 1
if (-not $fzExe -or -not $edExe) { throw "FancyZones binaries not found in the MSI" }
$ptRoot = $fzExe.DirectoryName
$edDir = $edExe.DirectoryName
Log "PowerToys root: $ptRoot"

# --- 4. Pick the FancyZones files -----------------------------------------------------------
$pkgName = "FancyZones-Standalone"
$pkg = Join-Path $OutDir $pkgName
Remove-Item $pkg -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $pkg | Out-Null

$files = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-File($dir, $name, [switch]$Optional) {
    $src = Join-Path $dir $name
    if (Test-Path $src -PathType Leaf) {
        Copy-Item $src (Join-Path $pkg (Split-Path $name -Leaf)) -Force
        [void]$files.Add((Split-Path $name -Leaf))
    } elseif (-not $Optional) { Write-Warning "missing: $name" }
}

# Everything the self-contained .NET editor lists in its deps.json (runtime + native assets).
$depsPath = Join-Path $edDir 'PowerToys.FancyZonesEditor.deps.json'
$deps = Get-Content $depsPath -Raw | ConvertFrom-Json
foreach ($target in $deps.targets.PSObject.Properties) {
    foreach ($lib in $target.Value.PSObject.Properties) {
        foreach ($kind in 'runtime', 'native') {
            $section = $lib.Value.$kind
            if ($section) { foreach ($k in $section.PSObject.Properties.Name) { Add-File $edDir (Split-Path $k -Leaf) } }
        }
    }
}
foreach ($n in 'PowerToys.FancyZonesEditor.exe', 'PowerToys.FancyZonesEditor.deps.json', 'PowerToys.FancyZonesEditor.runtimeconfig.json') { Add-File $edDir $n }
Add-File $ptRoot 'PowerToys.FancyZones.exe'
foreach ($n in 'PowerToys.GPOWrapper.dll', 'PowerToys.Interop.dll') { Add-File $edDir $n -Optional; Add-File $ptRoot $n -Optional }

# Any non-system DLL that FancyZones.exe (or a copied native DLL) imports from the PowerToys folder.
$dumpbin = $null
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vs = & $vswhere -latest -products * -property installationPath
    $dumpbin = Get-ChildItem "$vs\VC\Tools\MSVC" -Recurse -Filter dumpbin.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'Hostx64\\x64' } | Select-Object -First 1 -ExpandProperty FullName
}
if ($dumpbin) {
    $queue = [System.Collections.Generic.Queue[string]]::new(); $queue.Enqueue('PowerToys.FancyZones.exe')
    $seen = @{}
    while ($queue.Count) {
        $f = $queue.Dequeue(); if ($seen[$f]) { continue }; $seen[$f] = $true
        $imports = & $dumpbin /nologo /dependents (Join-Path $pkg $f) | Where-Object { $_ -match '^\s+\S+\.dll\s*$' } | ForEach-Object { $_.Trim() }
        foreach ($d in $imports) {
            if (-not $files.Contains($d) -and (Test-Path (Join-Path $ptRoot $d))) { Log "FancyZones.exe import: $d"; Add-File $ptRoot $d; $queue.Enqueue($d) }
        }
    }
} else { Log "dumpbin not found; skipping import scan" }

# Assets (icons) if the release has a FancyZones folder.
foreach ($a in Get-ChildItem $ptRoot -Recurse -Directory -Filter 'FancyZones*' -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\Assets\\' }) {
    $rel = $a.FullName.Substring($ptRoot.Length).TrimStart('\')
    Copy-Item $a.FullName (Join-Path $pkg $rel) -Recurse -Force
}

# Companion app + docs.
Copy-Item (Join-Path $ManagerDir 'FancyZonesStandalone.exe') $pkg
Copy-Item (Join-Path $ManagerDir 'FancyZonesStandalone.exe.config') $pkg -ErrorAction SilentlyContinue
$repoRoot = Split-Path $PSScriptRoot
Copy-Item (Join-Path $repoRoot 'LICENSE') (Join-Path $pkg 'LICENSE.txt')
Copy-Item (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt') $pkg

$tagOut = "v$version-b$Build"
@{ tag = $tagOut; powertoys = $version; build = $Build; fzHash = $FzHash; built = (Get-Date).ToUniversalTime().ToString('o') } |
    ConvertTo-Json | Set-Content (Join-Path $pkg 'version.json') -Encoding utf8

# Manifest lets the updater remove files dropped by later releases.
$all = Get-ChildItem $pkg -Recurse -File | ForEach-Object { $_.FullName.Substring($pkg.Length).TrimStart('\') }
($all + 'manifest.txt') | Sort-Object | Set-Content (Join-Path $pkg 'manifest.txt') -Encoding utf8

$zip = Join-Path $OutDir "$pkgName-$tagOut.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$pkg\*" -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
"$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding ascii -NoNewline

Log "Files: $((Get-ChildItem $pkg -Recurse -File).Count)  Zip: $zip  SHA256: $hash"
if ($env:GITHUB_OUTPUT) {
    "zip=$zip" >> $env:GITHUB_OUTPUT
    "tag=$tagOut" >> $env:GITHUB_OUTPUT
    "pkg=$pkg" >> $env:GITHUB_OUTPUT
    "version=$version" >> $env:GITHUB_OUTPUT
    "sha256=$hash" >> $env:GITHUB_OUTPUT
}
