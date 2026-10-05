# ============================================================================
#  drelay.ps1
# ============================================================================
# The one entry point when working from this source tree.
#
# It finds (or builds) target\drelay.jar and then does exactly what double-clicking the jar does,
# because every step lives in the jar's own launcher:
#
#   1. asks for the administrator rights that claiming an address needs,
#   2. checks for and installs a newer release,
#   3. extracts or upgrades the files it ships (relay-routes.json, the claim and hosts helpers),
#   4. refreshes the game server addresses from the game's own API and DNS,
#   5. stops a relay left over from an earlier run, so the listen ports are free,
#   6. claims each route's address on the loopback interface,
#   7. starts the relay and opens the dashboard,
#   8. releases the claims when the relay stops.
#
# Nothing here duplicates that logic, so the source-tree path and the downloaded-jar path cannot
# drift apart: the only extra thing this script does is build when there is no jar yet.
#
# Usage:
#   .\drelay.ps1                     build if needed, then run
#   .\drelay.ps1 -Build              always rebuild first
#   .\drelay.ps1 -Rebuild            clean, then build
#   .\drelay.ps1 -Maven              build with Maven instead of javac
#   .\drelay.ps1 -NoBuild            fail instead of building when the jar is missing
#   .\drelay.ps1 -Stop               stop the relay and give the claimed addresses back
#   .\drelay.ps1 -Status             what is running and what is claimed; change nothing
#   .\drelay.ps1 -Claim              (re)claim the addresses without starting a relay
#   .\drelay.ps1 -Release            give the claimed addresses back, leave the relay alone
#   .\drelay.ps1 -CheckUpdate        is there a newer release?
#   .\drelay.ps1 -Update             install the newest release and restart
#   .\drelay.ps1 -JarPath            print the full path of the jar it would run
#
#   .\drelay.ps1 -JarArg -Ddrelay.nexus.enabled=true
#   .\drelay.ps1 -JarArg -Ddrelay.web.port=0
#
# Exit codes are the relay's own, so a refused claim (3) or a failed release is visible to a caller.
# ============================================================================

[CmdletBinding()]
param(
    # --- what to do -----------------------------------------------------------------------
    [switch]$Stop,
    [switch]$Status,
    [switch]$Claim,
    [switch]$Release,
    [switch]$CheckUpdate,
    [switch]$Update,
    [switch]$JarPath,

    # --- build ----------------------------------------------------------------------------
    [switch]$Build,
    [switch]$Rebuild,
    [switch]$NoBuild,
    [switch]$Maven,

    # --- passthrough ----------------------------------------------------------------------
    # Extra arguments for the jar, e.g. -JarArg -Ddrelay.nexus.enabled=true
    [string[]]$JarArg,
    # An explicit jar to run, instead of searching the usual places.
    [string]$JarFile
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Write-Step { param([string]$Text) Write-Host $Text -ForegroundColor Cyan }
function Write-Ok   { param([string]$Text) Write-Host $Text -ForegroundColor Green }
function Write-Bad  { param([string]$Text) Write-Host $Text -ForegroundColor Red }
function Write-Note { param([string]$Text) Write-Host $Text -ForegroundColor DarkGray }

# --- the jar ---------------------------------------------------------------------------
# Note the explicit assignment and single trailing value: a bare `return X` inside a function also
# emits anything the earlier statements wrote to the pipeline, which is how a caller ends up with an
# array where it expected a string.
function Find-Jar {
    $found = $null
    if ($JarFile) {
        if (-not (Test-Path -LiteralPath $JarFile)) { Write-Error "no jar at $JarFile" }
        $found = (Resolve-Path -LiteralPath $JarFile).Path
    } else {
        # Newest wins, not first-found. A released drelay.jar sits beside this script, a source build
        # lands in target\, and both can be present at once - in which case the one that was built
        # last is the one the caller means. Picking by directory order instead would silently run a
        # stale jar (which is exactly how a fixed launcher keeps looking broken).
        $candidates = @('drelay.jar', 'target\drelay.jar', 'dist\drelay.jar') |
            ForEach-Object { Join-Path $root $_ } |
            Where-Object { Test-Path -LiteralPath $_ } |
            ForEach-Object { Get-Item -LiteralPath $_ } |
            Sort-Object LastWriteTime -Descending
        if ($candidates) { $found = $candidates[0].FullName }
    }
    $found
}

# A build in target\ would otherwise be the odd one out: the launcher, the route table, the extracted
# helpers, work\logs and the update log all live beside the jar, and run from the repository root the
# jar would be started in a different directory than its own - which it treats as its install
# location, so it would extract a second copy of everything into the repository root. Moving it up
# keeps one jar, one directory and one set of files, and matches what a user gets from a release.
function Move-Jar-ToRoot {
    param([string]$Path)
    $targetDir = Join-Path $root 'target'
    if (-not $Path.StartsWith($targetDir, [System.StringComparison]::OrdinalIgnoreCase)) { return $Path }
    $destination = Join-Path $root 'drelay.jar'
    try {
        Move-Item -LiteralPath $Path -Destination $destination -Force
        Write-Note "moved the built jar to $destination (that directory is the launcher's working directory)"
        return $destination
    } catch {
        # Locked by something else is fine: the jar still runs, only from target\.
        Write-Note "could not move the jar out of target\ ($($_.Exception.Message)); running it in place"
        return $Path
    }
}

function Get-Java {
    $java = Get-Command java -ErrorAction SilentlyContinue
    if (-not $java) {
        Write-Bad 'java is not on PATH. drelay needs Java 25 or newer.'
        Write-Bad 'Install a JDK (Temurin, Microsoft OpenJDK or Oracle) and try again.'
        exit 2
    }
    $java.Source
}

# Action switches that need a jar but never a build: reporting on a state that already exists
# should not compile anything.
$readOnlyAction = $Status -or $JarPath -or $CheckUpdate

if ($Rebuild) {
    Write-Step 'clean: removing target...'
    Remove-Item (Join-Path $root 'target') -Recurse -Force -ErrorAction SilentlyContinue
    $Build = $true
}

$jarFile = Find-Jar
if ($JarPath) {
    # Printing the path is a query, so it must not move anything: a caller that reads this value and
    # then uses it would otherwise be holding a path this very call invalidated.
    if ($jarFile) { Write-Output $jarFile } else { Write-Error 'no jar found; build one with .\drelay.ps1' }
    exit 0
}

$needsBuild = $Build -or (-not $jarFile)
if ($needsBuild -and $readOnlyAction) { $needsBuild = $false }
if ($needsBuild -and $NoBuild) {
    Write-Error 'no jar found and -NoBuild was given. Build one with: .\build.ps1'
}

if ($needsBuild) {
    $buildScript = Join-Path $root 'build.ps1'
    if (-not (Test-Path -LiteralPath $buildScript)) { Write-Error "build.ps1 is missing from $root" }
    $buildArgs = @()
    if ($Maven) { $buildArgs += '-Maven' }
    & $buildScript @buildArgs
    if ($LASTEXITCODE -ne 0) { Write-Error 'the build failed; not starting the relay' }
    $jarFile = Find-Jar
    if (-not $jarFile) { Write-Error 'the build reported success but produced no jar' }
}

if ($jarFile) { $jarFile = Move-Jar-ToRoot $jarFile }
if ($jarFile) { Write-Note "jar: $jarFile" }

# Everything the launcher writes goes beside the jar, so the shell has to be there too: the extracted
# tools\Set-AddressClaim.ps1 resolves its own route table relative to itself, and that has to be the
# same file the jar just wrote.
if ($jarFile) { Set-Location (Split-Path -Parent $jarFile) }
$jarDirectory = if ($jarFile) { Split-Path -Parent $jarFile } else { $root }

# --- act -------------------------------------------------------------------------------
$javaExe = Get-Java

function Invoke-Jar {
    param([string[]]$Arguments)
    $all = @('-jar', $jarFile) + $Arguments
    Write-Note "  java $($all -join ' ')"
    & $javaExe @all
    return $LASTEXITCODE
}

# The claim script the jar extracts, beside the jar as well.
$claimScript = Join-Path $jarDirectory 'tools\Set-AddressClaim.ps1'

$code = 0
if ($Stop) {
    # The jar stops the processes and releases the claims, so this stays a one-liner.
    $code = Invoke-Jar @('--stop')
} elseif ($Release) {
    $code = Invoke-Jar @('--release-claims')
} elseif ($Claim -or $Status) {
    # Claim and status are the claim script's own jobs; the jar extracts it and starts nothing.
    Invoke-Jar @('--install') | Out-Null
    if (-not (Test-Path -LiteralPath $claimScript)) { Write-Error "the jar did not provide $claimScript" }
    $claimArgs = @{}
    if ($Status) { $claimArgs['Status'] = $true }
    & $claimScript @claimArgs
    $code = $LASTEXITCODE
} elseif ($CheckUpdate) {
    $code = Invoke-Jar @('--check-update')
} elseif ($Update) {
    $code = Invoke-Jar @('--update')
} else {
    Write-Ok 'the launcher will ask for administrator rights, then claim the server addresses.'
    $arguments = @()
    if ($JarArg) { $arguments += $JarArg }
    $code = Invoke-Jar $arguments
}

exit $code
