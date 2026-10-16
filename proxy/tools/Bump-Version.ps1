<#
.SYNOPSIS
  Bumps the drelay version in both files that carry it.

.DESCRIPTION
  The version lives in two places on purpose, and the release workflow refuses to publish while they
  disagree:

    * src/main/resources/drelay.properties - read by the build and, at runtime, by the jar answering
      "is there a newer release?";
    * pom.xml - the Maven project version, which is a second copy of the same fact.

  This writes both, keeping any prerelease suffix ("-alpha"). It does not commit: a version bump is a
  reviewable change, and the release workflow turns the pushed commit into a release when its tag does
  not exist yet.

.PARAMETER Bump
  Which component to increment. The others are reset, as semantic versioning expects.

.PARAMETER Root
  Repository root. Only useful for testing the script against a copy of the two files.

.EXAMPLE
  pwsh -File tools/Bump-Version.ps1 -Bump patch
  0.0.2-alpha -> 0.0.3-alpha
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('patch', 'minor', 'major')]
    [string]$Bump,

    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$propertiesPath = Join-Path $Root 'src/main/resources/drelay.properties'
$pomPath = Join-Path $Root 'pom.xml'
foreach ($path in $propertiesPath, $pomPath) {
    if (-not (Test-Path -LiteralPath $path)) { throw "missing $path" }
}

$properties = [System.IO.File]::ReadAllText($propertiesPath)
if ($properties -notmatch '(?m)^version=(.+)$') { throw "no version= line in $propertiesPath" }
$current = $Matches[1].Trim()

# <major>.<minor>.<patch> with an optional -prerelease suffix, which is preserved.
if ($current -notmatch '^(?<base>\d+\.\d+\.\d+)(?<suffix>-.+)?$') {
    throw "version '$current' is not major.minor.patch, optionally with a -prerelease suffix"
}
$base = $Matches['base']
$suffix = [string]$Matches['suffix']

$parts = @($base.Split('.') | ForEach-Object { [int]$_ })
switch ($Bump) {
    'patch' { $parts[2]++ }
    'minor' { $parts[1]++; $parts[2] = 0 }
    'major' { $parts[0]++; $parts[1] = 0; $parts[2] = 0 }
}
$next = '{0}.{1}.{2}{3}' -f $parts[0], $parts[1], $parts[2], $suffix

# Exactly one replacement each: the properties key by name, and the first <version> in the pom, which
# is the project's - the rest belong to plugins and must not move. Note this uses the instance
# Replace(input, replacement, count) overload: the static [regex]::Replace(input, pattern, replacement, n)
# takes RegexOptions for its fourth argument, so passing a count there silently replaces every match
# (with IgnoreCase, which is worse).
$newProperties = [regex]::new('(?m)^version=.*$').Replace($properties, "version=$next", 1)
$pom = [System.IO.File]::ReadAllText($pomPath)
$newPom = [regex]::new('<version>[^<]*</version>').Replace($pom, "<version>$next</version>", 1)

# Everything after the first version element has to be byte-for-byte what it was. Checking only the
# values would pass while every plugin version had been rewritten too, which is exactly the bug this
# guard exists for.
$afterFirst = $pom.IndexOf('</version>')
$afterFirstNew = $newPom.IndexOf('</version>')
if ($afterFirst -lt 0 -or $afterFirstNew -lt 0 -or
        $newPom.Substring($afterFirstNew) -ne $pom.Substring($afterFirst)) {
    throw "the pom rewrite touched more than the project version"
}

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($propertiesPath, $newProperties, $utf8NoBom)
[System.IO.File]::WriteAllText($pomPath, $newPom, $utf8NoBom)

# Read them back rather than trusting the strings above: this is the invariant a release fails on.
$written = ([regex]::Match([System.IO.File]::ReadAllText($propertiesPath), '(?m)^version=(.+)$')).Groups[1].Value.Trim()
$pomVersion = ([regex]::Match([System.IO.File]::ReadAllText($pomPath), '<version>([^<]*)</version>')).Groups[1].Value
if ($written -ne $next -or $pomVersion -ne $next) {
    throw "the files disagree after writing: drelay.properties=$written pom.xml=$pomVersion, expected $next"
}

Write-Host "$current -> $next"
Write-Host "  $propertiesPath"
Write-Host "  $pomPath"
Write-Host ''
Write-Host "Commit both, then merge to master: the release workflow tags v$next and publishes it if that tag does not exist yet."
