# ============================================================================
#  build.ps1
# ============================================================================
# Builds the release artifact, target\drelay.jar, from this source tree.
#
# Two build paths exist on purpose:
#
#   * this script uses the JDK directly (javac + jar), which is what a user who
#     has only a JDK needs, and it is what CI and verify.ps1 call to keep the
#     local story identical to the released one;
#   * `mvn clean package` uses pom.xml, which is the conventional Java entry
#     point and the one GitHub Actions runs.
#
# Both produce the same jar name and the same Main-Class, so anything that runs
# one runs the other. The version comes from src\main\resources\drelay.properties,
# which the launcher also reads at runtime, so there is one version string in the
# project rather than three.
#
# Usage:
#   .\build.ps1                  # compile and package target\drelay.jar
#   .\build.ps1 -Clean           # remove target\ first
#   .\build.ps1 -Maven           # delegate to mvn clean package
#   .\build.ps1 -SkipTests       # do not run the javac-side checks after building
#
# Exit codes: 0 = built, 1 = build failed, 2 = usage problem.
# ============================================================================

[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$Maven,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Write-Step { param([string]$Text) Write-Host $Text -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host $Text -ForegroundColor Green }
function Write-Bad  { param([string]$Text) Write-Host $Text -ForegroundColor Red }
function Write-Note { param([string]$Text) Write-Host $Text -ForegroundColor DarkGray }

$target = Join-Path $root 'target'
$classes = Join-Path $target 'classes'
$testClasses = Join-Path $target 'test-classes'
$jarPath = Join-Path $target 'drelay.jar'
$propertiesPath = Join-Path $root 'src\main\resources\drelay.properties'

if ($Clean -and (Test-Path -LiteralPath $target)) {
    Write-Step 'clean: removing target...'
    Remove-Item -LiteralPath $target -Recurse -Force
}

if ($Maven) {
    if (-not (Get-Command mvn -ErrorAction SilentlyContinue)) {
        Write-Bad 'mvn is not on PATH. Build with the JDK instead by dropping -Maven.'
        exit 2
    }
    Write-Step 'building with Maven...'
    & mvn -B clean package
    if ($LASTEXITCODE -ne 0) { Write-Bad 'the Maven build failed'; exit 1 }
    Write-Ok "built $jarPath"
    exit 0
}

# --- read the version ------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $propertiesPath)) {
    Write-Bad "missing $propertiesPath"
    exit 2
}
$version = '0.0.0'
$repository = 'is-fo/drelay'
foreach ($line in [System.IO.File]::ReadAllLines($propertiesPath)) {
    if ($line -match '^\s*version\s*=\s*(.+?)\s*$') { $version = $Matches[1] }
    if ($line -match '^\s*repository\s*=\s*(.+?)\s*$') { $repository = $Matches[1] }
}
Write-Step "drelay $version ($repository)"

# --- compile ---------------------------------------------------------------------------
# Sources and tests live in separate trees and are compiled separately, which is what makes the jar
# provably free of test classes: only target\classes is packaged, and only src\main\java goes into it.
New-Item -ItemType Directory -Force -Path $classes | Out-Null
New-Item -ItemType Directory -Force -Path $testClasses | Out-Null
$sources = Get-ChildItem (Join-Path $root 'src\main\java') -Recurse -Filter *.java |
    ForEach-Object { $_.FullName }
if ($sources.Count -eq 0) { Write-Bad 'no sources found under src\main\java'; exit 2 }
$testSources = @(Get-ChildItem (Join-Path $root 'src\test\java') -Recurse -Filter *.java -ErrorAction SilentlyContinue |
    ForEach-Object { $_.FullName })

# An argument file keeps a long source list off the command line, which Windows limits. Written
# as UTF-8 without a BOM: javac reads a BOM as part of the first path.
$argFile = Join-Path $target 'sources.txt'
[System.IO.File]::WriteAllLines($argFile, $sources, [System.Text.UTF8Encoding]::new($false))

$javacVersion = (& javac -version 2>&1 | Out-String).Trim()
Write-Step "compiling $($sources.Count) source files with $javacVersion..."
& javac -encoding UTF-8 -Xlint:all,-serial -d $classes "@$argFile"
if ($LASTEXITCODE -ne 0) { Write-Bad 'compilation failed'; exit 1 }

if ($testSources.Count -gt 0) {
    $testArgFile = Join-Path $target 'test-sources.txt'
    [System.IO.File]::WriteAllLines($testArgFile, $testSources, [System.Text.UTF8Encoding]::new($false))
    Write-Step "compiling $($testSources.Count) test sources..."
    & javac -encoding UTF-8 -Xlint:all,-serial -cp $classes -d $testClasses "@$testArgFile"
    if ($LASTEXITCODE -ne 0) { Write-Bad 'test compilation failed'; exit 1 }
}

# --- stage the resources, in the layout the jar must have ------------------------------
# Resource files are copied with .NET rather than Copy-Item so the bytes are exactly the bytes
# in the source tree: a PowerShell copy can rewrite line endings and add a BOM, and both would
# change the hash the launcher records in its install manifest.
$resources = Join-Path $root 'src\main\resources'
Get-ChildItem $resources -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($resources.Length).TrimStart('\')
    $destination = Join-Path $classes $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    [System.IO.File]::WriteAllBytes($destination, [System.IO.File]::ReadAllBytes($_.FullName))
}

# --- package ---------------------------------------------------------------------------
$manifestPath = Join-Path $target 'MANIFEST.MF'
$manifest = @(
    'Manifest-Version: 1.0',
    'Main-Class: networking.Launcher',
    "Implementation-Version: $version",
    'Implementation-Title: drelay',
    'Automatic-Module-Name: drelay',
    ''
) -join "`r`n"
[System.IO.File]::WriteAllText($manifestPath, $manifest, [System.Text.UTF8Encoding]::new($false))

if (Test-Path -LiteralPath $jarPath) { Remove-Item -LiteralPath $jarPath -Force }
Write-Step 'packaging...'
& jar --create --file $jarPath --manifest $manifestPath -C $classes .
if ($LASTEXITCODE -ne 0) { Write-Bad 'packaging failed'; exit 1 }

$size = [math]::Round((Get-Item -LiteralPath $jarPath).Length / 1KB, 1)
Write-Ok "built $jarPath ($size KB, $(($sources).Count) sources, drelay $version)"

# --- checks ----------------------------------------------------------------------------
if (-not $SkipTests) {
    if ($testSources.Count -eq 0) {
        Write-Note 'no tests under src\test\java; skipping the offline checks'
    } else {
        Write-Step 'running the offline checks...'
        $testClasspath = "$classes;$testClasses"
        foreach ($main in 'networking.PrimitiveTests', 'networking.FrameTests',
                          'networking.ClientPacketsTests', 'networking.InjectionTests',
                          'networking.UpdateScanTests') {
            & java -cp $testClasspath $main
            if ($LASTEXITCODE -ne 0) { Write-Bad "$main failed"; exit 1 }
        }
        Write-Ok 'checks passed'
    }
}

Write-Host ''
Write-Note 'Run it with:  java -jar target\drelay.jar'
Write-Note 'Or from this tree, with elevation handled for you:  .\drelay.ps1'
Write-Note 'The Python end-to-end checks live in tools\tests and drive target\classes.'
