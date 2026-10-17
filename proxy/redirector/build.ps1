# ============================================================================
#  build.ps1 -- builds the packet redirector and stages it beside the jar.
#
#  What it produces, in proxy\target\redirector\:
#
#    drelay-redirect.exe   the redirector (framework-dependent, single file, ~200 KB)
#    WinDivert.dll         WinDivert 2.2 x64, user-mode library
#    WinDivert64.sys       WinDivert 2.2 x64, the signed WFP driver
#    NOTICE.txt            where those two came from, and their licence
#
#  The launcher looks for exactly this directory (see Launcher.redirectorPath), and it runs
#  `--selftest` on the exe it just built, because the interesting half of the redirector needs
#  administrator rights to exercise and a broken packet transform would otherwise only be found in a
#  live session.
#
#  WHY THE WINDIVERT BINARIES ARE NOT IN THIS REPOSITORY
#
#  They are third-party binaries under the LGPLv3, and the repository's .gitignore excludes *.dll and
#  *.exe outright ("third-party binaries and archives (not ours to publish)"). So this script copies
#  them from, in order: -WinDivertDir; an existing WindivertDotnet extraction under %APPDATA%; the
#  official WinDivert release zip, which it downloads into .cache\ and verifies.
#
#  Usage:
#    .\build.ps1                       build and stage everything
#    .\build.ps1 -SkipPublish          only stage WinDivert beside an existing exe
#    .\build.ps1 -WinDivertDir <dir>    take WinDivert.dll/.sys from <dir> (x64)
#    .\build.ps1 -OutputDir <dir>       stage somewhere other than proxy\target\redirector
#    .\build.ps1 -Clean                 remove the publish output first
#
#  Exit codes: 0 = built and staged, 1 = build or staging failed, 2 = usage/toolchain problem.
# ============================================================================

[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [switch]$Clean,
    [string]$WinDivertDir,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'drelay-redirect.csproj'
if (-not $OutputDir) { $OutputDir = Join-Path (Split-Path -Parent $root) 'target\redirector' }
$cache = Join-Path $root '.cache'
$exe = Join-Path $OutputDir 'drelay-redirect.exe'

function Write-Step { param([string]$Text) Write-Host $Text -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host $Text -ForegroundColor Green }
function Write-Bad  { param([string]$Text) Write-Host $Text -ForegroundColor Red }
function Write-Note { param([string]$Text) Write-Host $Text -ForegroundColor DarkGray }

$WindivertVersion = '2.2'
$WindivertSources = @(
    'https://reqrypt.org/download/WinDivert-2.2-A.zip',
    'https://github.com/basil00/Divert/releases/download/v2.2/WinDivert-2.2-A.zip'
)

if ($Clean -and (Test-Path -LiteralPath $OutputDir)) {
    Write-Step "clean: removing $OutputDir"
    Remove-Item -LiteralPath $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# --- the SDK ------------------------------------------------------------------------------
# .NET 8 or newer will build a net8.0 project, so the check is the major version rather than an exact
# match: this machine has 8.0.x, and a future SDK must not be refused for being newer.
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Bad '.NET SDK not found. Install the .NET 8 SDK, or run this on a machine that has one.'
    exit 2
}
$sdks = @(& dotnet --list-sdks 2>&1 | Where-Object { $_ -match '^\d+\.\d+\.\d+' })
$usable = @($sdks | Where-Object { [int](($_ -split '\.')[0]) -ge 8 })
if ($usable.Count -eq 0) {
    Write-Bad "no .NET SDK 8.0 or newer found; the installed SDKs are:"
    $sdks | ForEach-Object { Write-Note "  $_" }
    exit 2
}
Write-Note ("using " + $usable[-1])

# --- publish ------------------------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Step 'publishing drelay-redirect.exe...'
    & dotnet publish $project -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:DebugType=none -p:DebugType=None `
        -p:GenerateRuntimeConfigurationFiles=true -o $OutputDir --nologo
    if ($LASTEXITCODE -ne 0) { Write-Bad 'dotnet publish failed'; exit 1 }
}

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Bad "no exe at $exe"
    Write-Note '  run without -SkipPublish, or build with: dotnet publish -c Release -r win-x64'
    exit 1
}

# A single-file framework-dependent publish leaves nothing else behind but the exe. Say so if it
# did: a stale runtimeconfig beside a single-file app is not used, but it is a sign of a different
# publish mode and worth knowing about.
Get-ChildItem -LiteralPath $OutputDir -File | Where-Object { $_.Name -match '\.(runtimeconfig\.json|deps\.json)$' } |
    ForEach-Object { Write-Note "  note: publish also produced $($_.Name) - harmless, but unexpected" }

# --- the checks that do not need the driver or elevation ------------------------------------
Write-Step 'running the offline selftest...'
& $exe --selftest
if ($LASTEXITCODE -ne 0) { Write-Bad 'the selftest failed'; exit 1 }

# --- WinDivert ----------------------------------------------------------------------------
function Find-WindivertDirectory {
    param([string]$Hint)

    if ($Hint) {
        if (-not (Test-Path -LiteralPath (Join-Path $Hint 'WinDivert.dll'))) {
            Write-Bad "$Hint does not contain WinDivert.dll"
            exit 2
        }
        return $Hint
    }

    # An existing WindivertDotnet extraction: the wrapper downloads the official binaries here on
    # first use, and they are the same files this script would fetch.
    $appData = Join-Path $env:APPDATA 'WindivertDotnet\v222\x64'
    if (Test-Path -LiteralPath (Join-Path $appData 'WinDivert.dll')) {
        Write-Note "  using the WinDivert binaries already on this machine: $appData"
        return $appData
    }

    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    $zip = Join-Path $cache 'WinDivert-2.2-A.zip'
    $extracted = Join-Path $cache 'WinDivert-2.2-A'
    $wanted = Join-Path $extracted 'x64\WinDivert.dll'
    if (Test-Path -LiteralPath $wanted) {
        Write-Note "  using the cached WinDivert download: $extracted"
        return (Join-Path $extracted 'x64')
    }

    foreach ($url in $WindivertSources) {
        Write-Step "downloading WinDivert $WindivertVersion from $url"
        try {
            Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing -TimeoutSec 60
            break
        } catch {
            Write-Note "  failed: $($_.Exception.Message)"
        }
    }
    if (-not (Test-Path -LiteralPath $zip) -or (Get-Item -LiteralPath $zip).Length -eq 0) {
        Write-Bad 'WinDivert could not be downloaded, and it is not beside the exe either.'
        Write-Note '  download the official 2.2 A release and point this script at its x64 folder:'
        Write-Note '    .\build.ps1 -WinDivertDir C:\path\to\WinDivert-2.2-A\x64'
        exit 1
    }
    Expand-Archive -LiteralPath $zip -DestinationPath $cache -Force
    if (-not (Test-Path -LiteralPath $wanted)) {
        Write-Bad "the downloaded archive does not contain x64\WinDivert.dll"
        exit 1
    }
    return (Join-Path $extracted 'x64')
}

$windivert = Find-WindivertDirectory -Hint $WinDivertDir
foreach ($name in 'WinDivert.dll', 'WinDivert64.sys') {
    $from = Join-Path $windivert $name
    if (-not (Test-Path -LiteralPath $from)) {
        Write-Bad "missing $name in $windivert"
        exit 1
    }
    Copy-Item -LiteralPath $from -Destination (Join-Path $OutputDir $name) -Force
}
Write-Ok "staged WinDivert $WindivertVersion (x64) beside the exe"

# The licence travels with the binaries: LGPLv3 requires the notice to be distributed with them.
$licenseFrom = Join-Path (Split-Path -Parent $windivert) 'LICENSE'
$notice = @(
    "drelay-redirect.exe, beside this file, uses WinDivert $WindivertVersion (x64).",
    '',
    'WinDivert is Copyright (C) 2019 basil, and is dual-licensed under the GNU Lesser General',
    'Public License version 3 and the GNU General Public License version 2. It is distributed here',
    'unmodified, as WinDivert.dll and WinDivert64.sys, because WinDivert is dynamically linked.',
    '',
    'Source and licence: https://github.com/basil00/Divert  and  https://reqrypt.org/windivert.html',
    '',
    'The WinDivert driver is installed on demand by WinDivertOpen() and can be removed with:',
    '  sc stop WinDivert && sc delete WinDivert'
) -join "`r`n"
[System.IO.File]::WriteAllText((Join-Path $OutputDir 'NOTICE.txt'), $notice + "`r`n",
    [System.Text.UTF8Encoding]::new($false))
if (Test-Path -LiteralPath $licenseFrom) {
    Copy-Item -LiteralPath $licenseFrom -Destination (Join-Path $OutputDir 'WinDivert-LICENSE.txt') -Force
} else {
    Write-Note '  note: the WinDivert licence text was not beside the binaries; NOTICE.txt names its source'
}

# --- summary ------------------------------------------------------------------------------
$size = [math]::Round((Get-Item -LiteralPath $exe).Length / 1KB, 1)
Write-Ok "built $exe ($size KB)"
Get-ChildItem -LiteralPath $OutputDir -File | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.Substring(0, 16)
    Write-Note ("  {0,-28} {1,8} bytes  sha256:{2}..." -f $_.Name, $_.Length, $hash)
}
Write-Host ''
Write-Note 'Start it with the launcher, which manages its lifetime:'
Write-Note '  .\drelay.ps1 -Redirect'
exit 0
