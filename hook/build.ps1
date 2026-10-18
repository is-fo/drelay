# ============================================================================
#  build.ps1 -- builds the whole Phase 1 hook: native DLL, bootstrap, launcher.
#
#  WHY A SCRIPT RATHER THAN A .vcxproj OR A .csproj SOLUTION
#  --------------------------------------------------------
#  Two reasons, both practical:
#
#    * the native half must be built by the *same* MSVC toolset that produced
#      the runtime it targets, and a .vcxproj hardcodes one.  This script finds
#      the installed toolset through vswhere and reports which one it used, so
#      "which compiler built this DLL" is in the build log;
#    * the managed half must target net9.0 to match the game's runtime, and the
#      only SDK on this machine is 8.0.x.  A plain `dotnet build` with an
#      explicit TargetFramework works; a project file that a future SDK silently
#      retargets would not.
#
#  Nothing is written into the game directory.  The build output goes to
#  hook\out\.
#
#  Usage:
#    .\build.ps1                 # build everything
#    .\build.ps1 -Native         # native only
#    .\build.ps1 -Managed        # managed only
#    .\build.ps1 -Clean
#
#  Exit codes: 0 = built, 1 = build failed, 2 = usage/toolchain problem.
# ============================================================================

[CmdletBinding()]
param(
    [switch]$Native,
    [switch]$Managed,
    [switch]$Clean,
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $root 'out'
$objDir = Join-Path $root 'obj'

function Write-Step { param([string]$t) Write-Host $t -ForegroundColor Cyan }
function Write-Ok   { param([string]$t) Write-Host $t -ForegroundColor Green }
function Write-Bad  { param([string]$t) Write-Host $t -ForegroundColor Red }
function Write-Note { param([string]$t) Write-Host $t -ForegroundColor DarkGray }

if ($Clean) {
    foreach ($d in @($outDir, $objDir)) {
        if (Test-Path -LiteralPath $d) {
            Write-Step "clean: removing $d"
            Remove-Item -LiteralPath $d -Recurse -Force
        }
    }
    if (-not ($Native -or $Managed)) { exit 0 }
}

# A guard that costs one pass over the project files and saves a confusing
# failure. MSBuild rejects an XML comment containing `--` (MSB4025, "an XML
# comment cannot contain '--'"), and this repository's prose is full of em-dashes
# written as `--`. The error names a line and column in a file nobody was
# editing, so it reads as a corrupt project rather than as a comment style problem.
$projectFiles = Get-ChildItem -Path $root -Recurse -Include '*.csproj', '*.props', '*.targets' -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
foreach ($file in $projectFiles) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($match in [regex]::Matches($text, '(?s)<!--(.*?)-->')) {
        if ($match.Groups[1].Value.Contains('--')) {
            $line = ($text.Substring(0, $match.Index) -split "`n").Count
            Write-Bad ("$($file.FullName):$line  an XML comment contains '--', which MSBuild rejects (MSB4025).")
            Write-Note '  use a comma or a colon instead; the prose in this project is full of em-dashes.'
            exit 2
        }
    }
}

$doNative = $Native -or (-not $Managed)
$doManaged = $Managed -or (-not $Native)

New-Item -ItemType Directory -Force -Path $outDir, $objDir | Out-Null

$failures = @()

# ---------------------------------------------------------------------------
# Generated: the bootstrap's pinned-build table
# ---------------------------------------------------------------------------
# Regenerated from known-build.json on every build, so the hook can never be
# built against a stale copy of the build identity Phase 0 pinned.  If Python is
# not available the committed copy is used and the build says so -- the generated
# file is deterministic, so a missing generator is a warning rather than a
# failure.
if ($doManaged) {
    # Two generators, both reading a file that is already the authority:
    #   gen_target_table.py  -- known-build.json  -> the pinned target table
    #   gen_shared_layout.py -- DrelayHookShared.h -> every FieldOffset in the
    #                          managed view of the shared block
    # Both are deterministic, so a missing generator is a warning (the committed
    # output is used) rather than a failure.
    $generators = @(
        @{ Script = 'tools\gen_target_table.py';  What = 'build table' },
        @{ Script = 'tools\gen_shared_layout.py'; What = 'shared-block layout' }
    )
    foreach ($gen in $generators) {
        $script = Join-Path $root $gen.Script
        if ((Get-Command python -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath $script)) {
            $genOut = & python $script 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-Bad ("the $($gen.What) could not be regenerated:")
                $genOut | ForEach-Object { Write-Host $_ }
                exit 1
            }
            $genOut | ForEach-Object { Write-Note $_ }
        } else {
            Write-Note "python or $($gen.Script) is unavailable; using the committed $($gen.What)"
        }
    }
}

# ---------------------------------------------------------------------------
# Native: Drelay.Hook.dll
# ---------------------------------------------------------------------------
if ($doNative) {
    Write-Step '--- native: Drelay.Hook.dll ---'

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere)) {
        Write-Bad 'vswhere.exe not found: no Visual Studio installation is discoverable.'
        exit 2
    }
    $vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vsPath) {
        # Fall back to a plain install and let vcvars decide whether the C++
        # toolset is really there; the error message from cl.exe is better than
        # ours would be.
        $vsPath = & $vswhere -latest -products * -property installationPath
    }
    if (-not $vsPath) {
        Write-Bad 'no Visual Studio installation found.'
        exit 2
    }
    $vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
    if (-not (Test-Path -LiteralPath $vcvars)) {
        Write-Bad "vcvars64.bat not found under $vsPath -- install the Desktop development with C++ workload."
        exit 2
    }

    $nativeDir = Join-Path $root 'native'
    $minhookDir = Join-Path $root 'thirdparty\MinHook'
    $sources = @(
        'DrelayMain.cpp', 'DrelayInstall.cpp', 'DrelayFrame.cpp',
        'DrelaySelfCheck.cpp', 'DrelayLog.cpp', 'DrelayPe.cpp', 'DrelayEvidence.cpp',
        'DrelayCrash.cpp'
    ) | ForEach-Object { Join-Path $nativeDir $_ }
    $minhookSources = @(
        'src\hook.c', 'src\buffer.c', 'src\trampoline.c', 'src\hde\hde64.c'
    ) | ForEach-Object { Join-Path $minhookDir $_ }

    foreach ($s in ($sources + $minhookSources)) {
        if (-not (Test-Path -LiteralPath $s)) { Write-Bad "missing source: $s"; exit 1 }
    }

    $defines = @(
        'WIN32_LEAN_AND_MEAN', 'NOMINMAX', '_CRT_SECURE_NO_WARNINGS',
        'UNICODE', '_UNICODE', 'MH_STATIC'
    ) | ForEach-Object { "/D$_" }

    # /W4 and warnings-as-errors on the hook's own sources: this code runs inside
    # the game's frame and a truncation or an unused-parameter bug is exactly the
    # kind of thing that becomes a crash under a different build.
    # Object layout.  Each pass writes into its own directory, which matters
    # because /Fo names each object after its *source* basename: with both passes
    # sharing one directory, MinHook's single-letter file names are exactly the
    # kind that would eventually collide with a future file of ours and silently
    # link the wrong object.  Two directories removes the possibility rather than
    # documenting it.
    $objOurDir = Join-Path $objDir 'our'
    $objMhDir = Join-Path $objDir 'minhook'
    $objSizeDir = Join-Path $objDir 'size'
    New-Item -ItemType Directory -Force -Path $objOurDir, $objMhDir, $objSizeDir | Out-Null

    $nativeObjects = @()
    foreach ($src in $sources) {
        $nativeObjects += Join-Path $objOurDir ((Split-Path -Leaf $src) -replace '\.cpp$', '.obj')
    }
    $minhookObjects = @()
    foreach ($src in $minhookSources) {
        $minhookObjects += Join-Path $objMhDir ((Split-Path -Leaf $src) -replace '\.c$', '.obj')
    }

    # Two passes, on purpose.
    #
    # Pass 1 compiles the hook's own sources at /W4 /WX: this code runs inside
    # the game's frame, and a truncation or an uninitialised read is exactly the
    # kind of thing that becomes a crash only under a different build.
    #
    # Pass 2 compiles the vendored MinHook at /W3 with warnings *not* errors.
    # Its warnings are real but they are its author's to fix, and a hook that
    # cannot be rebuilt because a third-party file grew a new warning is a worse
    # failure than four C4244s in a disassembler's length arithmetic.  The
    # distinction is recorded here rather than by lowering the bar for our own
    # code.
    # Built as literal strings rather than as arrays joined later, because the
    # quoting is the fiddly part and PowerShell's native-command argument passing
    # does not preserve embedded quotes the way a naive `-join ' '` assumes: an
    # array element that is `"/Fo"` plus a quoted path arrives at link.exe with
    # the quotes stripped and the path broken into two arguments, which surfaces
    # as the unhelpful "no argument specified with option '/Fe'".
    $sourceList = ($sources | ForEach-Object { "`"$_`"" }) -join ' '
    $minhookList = ($minhookSources | ForEach-Object { "`"$_`"" }) -join ' '
    $objectList = (($nativeObjects + $minhookObjects) | ForEach-Object { "`"$_`"" }) -join ' '
    $includeList = @(
        ('/I"' + (Join-Path $minhookDir 'include') + '"'),
        ('/I"' + (Join-Path $minhookDir 'src') + '"'),
        ('/I"' + $nativeDir + '"')
    ) -join ' '
    $defineList = ($defines -join ' ')

    # /MD = the runtime the .NET runtime itself ships against (ucrtbase +
    # vcruntime140), /EHsc = the exception model MinHook and the __try blocks
    # assume, /GS and /guard:cf are the defaults and are kept deliberately.
    # NOTE on the trailing separator: `/Fo"<dir>\\"` looks natural but the pair of
    # backslashes before the closing quote escapes it in the command line, so the
    # path arrives unterminated and cl writes every object into the *current*
    # directory under its own name.  A forward slash ends the directory without
    # ending the quote.
    $ourObjArg = ($objOurDir -replace '\\', '/') + '/'
    $mhObjArg = ($objMhDir -replace '\\', '/') + '/'
    $ourArgs = "$sourceList /Fo`"$ourObjArg`" /Fd`"$objDir\Drelay.Hook.pdb`" $includeList $defineList " +
               '/nologo /c /MD /O2 /GL /EHsc /W4 /WX /std:c++17 /Gy /Gw /guard:cf /GS /Zi'
    $mhArgs = "$minhookList /Fo`"$mhObjArg`" $includeList $defineList " +
              '/nologo /c /MD /O2 /GL /W3 /std:c17 /Zi'
    # link.exe takes /OUT: and /PDB:, where cl.exe's driver takes /Fe and /Fd.
    # The system import libraries are named here explicitly rather than left to
    # the `#pragma comment(lib, ...)` in the sources: a pragma is honoured only
    # when cl drives the link, and this script invokes link.exe directly so that
    # the two warning policies above can differ per pass.
    # `user32.lib` is here for the foreground-window census in DrLogProcessIdentity:
    # GetForegroundWindow/GetWindowThreadProcessId/GetWindowTextW are in user32, and
    # the process census in the same function uses tlhelp32, which lives in kernel32
    # and therefore needs nothing extra.
    $linkArgs = "/nologo /DLL /OUT:`"$outDir\Drelay.Hook.dll`" /PDB:`"$objDir\Drelay.Hook.pdb`" " +
                "/MAP:`"$objDir\Drelay.Hook.map`" /MAPINFO:EXPORTS " +
                "$objectList /LTCG /OPT:REF /OPT:ICF /INCREMENTAL:NO /DEBUG " +
                '/SUBSYSTEM:WINDOWS version.lib crypt32.lib advapi32.lib user32.lib'

    $bat = Join-Path $objDir 'build_native.bat'
    $logPath = Join-Path $objDir 'native_build.log'
    $sizeLog = Join-Path $objDir 'sizeof.txt'
    # The size probe is compiled first and run, because it is the check that the
    # generator's model of MSVC's packing agrees with MSVC.  Building the hook
    # first and discovering the disagreement from a static_assert would name the
    # total but not which field moved.
    $sizeProbeArgs = "/nologo `"$(Join-Path $nativeDir 'SizeProbe.c')`" " +
                     "/I`"$nativeDir`" /DDRELAY_LAYOUT_PROBE /Fo`"$objSizeDir/`" " +
                     "/Fe`"$(Join-Path $objDir 'SizeProbe.exe')`" /MD /O2 /W3"
    # The compiler's output is redirected to a file by the batch itself rather
    # than captured through the pipeline: MSVC writes its banner to stderr, and
    # PowerShell turns a native command's stderr into an error record, which
    # under $ErrorActionPreference='Stop' aborts the build on a *successful*
    # compile.  A file is also the artefact you want when a build fails.
    #
    # Each cl invocation is ONE physical line, with `< nul` on it.  Two reasons,
    # and both cost a debugging session the first time: a batch file does not
    # continue an argument list across a line break without a trailing caret, so
    # a multi-line command silently becomes "cl.exe" plus a stray redirect; and
    # cl with no input file waits on stdin rather than exiting, so a malformed
    # invocation hangs the build instead of failing it.
    $lines = @(
        '@echo off',
        "call `"$vcvars`" >nul",
        'if errorlevel 1 exit /b 2',
        ('cl.exe ' + $sizeProbeArgs + ' < nul > "' + $logPath + '" 2>&1'),
        'if errorlevel 1 exit /b 1',
        ('"' + (Join-Path $objDir 'SizeProbe.exe') + '" > "' + $sizeLog + '"'),
        'if errorlevel 1 exit /b 1',
        ('cl.exe ' + $ourArgs + ' < nul >> "' + $logPath + '" 2>&1'),
        'if errorlevel 1 exit /b 1',
        ('cl.exe ' + $mhArgs + ' < nul >> "' + $logPath + '" 2>&1'),
        'if errorlevel 1 exit /b 1',
        ('link.exe ' + $linkArgs + ' < nul >> "' + $logPath + '" 2>&1'),
        'exit /b %errorlevel%'
    )
    Set-Content -LiteralPath $bat -Value $lines -Encoding ASCII
    Write-Note "compiling $($sources.Count) hook source(s) at /W4 /WX, then $($minhookSources.Count) MinHook source(s) at /W3"

    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & cmd.exe /c "`"$bat`""
    $ErrorActionPreference = $prevEap
    $clExit = $LASTEXITCODE

    $clOut = if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath } else { @() }
    $clOut | Where-Object { $_ -match ': error|: warning|fatal error|error LNK|error C' } | Select-Object -First 60 | ForEach-Object { Write-Host $_ }

    # The compiler's own view of the layout, checked against the generator's
    # prediction.  This is the check that caught the two wrong strides, and it is
    # a build step rather than a comment because "the model is right" is exactly
    # the assumption that was wrong twice.
    if (Test-Path -LiteralPath $sizeLog) {
        Write-Note 'compiler-reported layout:'
        Get-Content -LiteralPath $sizeLog | ForEach-Object { Write-Note ("  " + $_) }
        $reported = @{}
        foreach ($line in (Get-Content -LiteralPath $sizeLog)) {
            $parts = $line -split '\s+'
            if ($parts.Count -ge 2) { $reported[$parts[0]] = [int]$parts[1] }
        }
        $declared = 0
        if ($reported.ContainsKey('DRELAY_SHARED_SIZE')) { $declared = $reported['DRELAY_SHARED_SIZE'] }
        if ($reported.ContainsKey('DrelayShared') -and $declared -ne 0 -and $reported['DrelayShared'] -ne $declared) {
            Write-Bad ("the compiler says sizeof(DrelayShared) = $($reported['DrelayShared']) but the header " +
                       "declares DRELAY_SHARED_SIZE = $declared")
            $failures += 'sizeof'
        }
    } elseif ($clExit -eq 0) {
        Write-Bad 'the size probe did not produce sizeof.txt'
        $failures += 'sizeof-probe'
    }

    if ($clExit -ne 0) {
        Write-Bad "native build FAILED (toolchain exit $clExit); full log: $logPath"
        $clOut | Select-Object -Last 40 | ForEach-Object { Write-Host $_ }
        $failures += 'native'
    } else {
        $dll = Join-Path $outDir 'Drelay.Hook.dll'
        $size = (Get-Item -LiteralPath $dll).Length
        Write-Ok ("native: {0} ({1:N0} bytes)" -f $dll, $size)
        Write-Note ("  compiler: " + (($clOut | Select-String -Pattern 'Microsoft \(R\) C/C\+\+').Line | Select-Object -First 1))
    }
}

# ---------------------------------------------------------------------------
# Managed: Drelay.Bootstrap.dll and Drelay.Launcher.exe
# ---------------------------------------------------------------------------
if ($doManaged) {
    Write-Step '--- managed: Drelay.Bootstrap.dll + Drelay.Launcher.exe ---'

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Bad 'dotnet not on PATH.'
        exit 2
    }

    $managedDir = Join-Path $root 'managed'
    # ---------------------------------------------------------------------------------------
    # THE PRIVATE FARM MODULE (`PLAN-AUTOFARM.md` section 13).
    #
    # `hook/private` is git-ignored and holds the auto-farmer's decision code. When it is present
    # the two project files compile it INSTEAD OF the public `Farmer.cs` stub beside them; when it
    # is absent the stub is compiled and the farmer cannot walk. Nothing here copies `private` into
    # `out\`: the release artefact is `out\`, so a build made without the module is the only thing
    # that can be packaged -- and this line is how a build made WITH it says so out loud, because
    # the mistake that matters is a private build being mistaken for a distributable one.
    $privateDir = Join-Path $root 'private'
    $privateSources = @(Get-ChildItem -LiteralPath $privateDir -Recurse -Filter '*.cs' -File -ErrorAction SilentlyContinue)
    $privateModule = $privateSources.Count -gt 0
    if ($privateModule) {
        Write-Note "private farm module: $($privateSources.Count) source(s) under hook\private"
        Write-Bad  '  THIS BUILD IS NOT FOR DISTRIBUTION: it contains the private farmer module.'
        Write-Note '  PLAN-AUTOFARM.md section 13 -- hook\private is git-ignored and never in a release.'
    } else {
        Write-Note 'private farm module: absent; the public Farmer stub is compiled and cannot walk'
    }
    foreach ($proj in @('Drelay.Bootstrap', 'Drelay.Launcher')) {
        $projPath = Join-Path $managedDir "$proj\$proj.csproj"
        if (-not (Test-Path -LiteralPath $projPath)) { Write-Bad "missing project: $projPath"; exit 1 }
        Write-Note "building $proj"
        $dotnetOut = & dotnet build $projPath -c Release -v minimal --nologo 2>&1
        $dotnetExit = $LASTEXITCODE
        if ($dotnetExit -ne 0) {
            Write-Bad "$proj build FAILED"
            $dotnetOut | Select-Object -Last 40 | ForEach-Object { Write-Host $_ }
            $failures += $proj
        } else {
            Write-Ok "$proj built"
        }
    }

    # The projects build into `bin\Release` directly (AppendTargetFrameworkToOutputPath
    # is off precisely so that this path is stable), and the hook needs both DLLs
    # beside the launcher, which is also where the native DLL lands.  Copying
    # explicitly rather than publishing keeps the output a flat directory of
    # exactly the files that must travel together, which is what makes "copy this
    # directory next to the game and run the launcher" true.
    if ($failures.Count -eq 0) {
        foreach ($pair in @(
            @{ From = 'Drelay.Bootstrap\bin\Release\Drelay.Bootstrap.dll'; To = 'Drelay.Bootstrap.dll' },
            @{ From = 'Drelay.Bootstrap\bin\Release\Drelay.Bootstrap.deps.json'; To = 'Drelay.Bootstrap.deps.json' },
            @{ From = 'Drelay.Bootstrap\bin\Release\Drelay.Bootstrap.pdb'; To = 'Drelay.Bootstrap.pdb'; Optional = $true },
            @{ From = 'Drelay.Launcher\bin\Release\Drelay.Launcher.exe'; To = 'Drelay.Launcher.exe' },
            @{ From = 'Drelay.Launcher\bin\Release\Drelay.Launcher.dll'; To = 'Drelay.Launcher.dll' },
            @{ From = 'Drelay.Launcher\bin\Release\Drelay.Launcher.runtimeconfig.json'; To = 'Drelay.Launcher.runtimeconfig.json' },
            @{ From = 'Drelay.Launcher\bin\Release\Drelay.Launcher.deps.json'; To = 'Drelay.Launcher.deps.json' },
            @{ From = 'Drelay.Launcher\bin\Release\Drelay.Launcher.pdb'; To = 'Drelay.Launcher.pdb'; Optional = $true }
        )) {
            $src = Join-Path $managedDir $pair.From
            if (Test-Path -LiteralPath $src) {
                Copy-Item -LiteralPath $src -Destination (Join-Path $outDir $pair.To) -Force
            } elseif (-not $pair.Optional) {
                Write-Bad "expected build output is missing: $src"
                $failures += $pair.To
            }
        }
    }

    # ---------------------------------------------------------------------------
    # Spike 3.2's offline half: SolverHarness, which is NOT part of the hook.
    # ---------------------------------------------------------------------------
    #
    # IT IS BUILT HERE ANYWAY, AND NOT COPIED TO `out`.  The harness compiles the same two source
    # files the solver does (`DodgeSnapshot.cs`, `DodgeSolver.cs`) rather than referencing the
    # hook's DLL, so building it is the check that the solver is still a pure function of plain
    # data -- a change that reaches for a game type breaks this build, and that is exactly the
    # moment to find out.  It stays out of `out` because `out` is the directory that gets copied
    # next to the game, and a test program has no business there.
    $harnessProj = Join-Path $root 'tools\SolverHarness\SolverHarness.csproj'
    if (Test-Path -LiteralPath $harnessProj) {
        Write-Note 'building SolverHarness (Spike 3.2 offline)'
        $harnessOut = & dotnet build $harnessProj -c Release -v minimal --nologo 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Bad 'SolverHarness build FAILED -- and because it compiles the solver''s own source,'
            Write-Bad 'that means the pure-data contract is broken, not that the harness is wrong.'
            $harnessOut | Select-Object -Last 30 | ForEach-Object { Write-Host $_ }
            $failures += 'SolverHarness'
        } else {
            Write-Ok 'SolverHarness built'
        }
    }

    # ---------------------------------------------------------------------------
    # WorldLogVerify, which is ALSO not part of the hook and IS built here.
    # ---------------------------------------------------------------------------
    #
    # SAME ARGUMENT AS THE SOLVER HARNESS, one step further out.  It compiles the REAL
    # `WorldLog.cs` and the REAL `EnemyReader.cs` against a fake game and parses every line the
    # world log writes, so a writer that emits malformed JSON -- or that fails to escape a portal
    # title -- breaks THIS build instead of the operator's one collection session.  That session is
    # the only source of the map names and portal titles `PLAN-AUTOFARM.md` sections 5 and 8 are
    # blocked on, and the project has already lost a recording to an unescaped newline once.
    # `Test-HookOffline.ps1` runs it.
    $worldLogProj = Join-Path $root 'tools\WorldLogVerify\WorldLogVerify.csproj'
    if (Test-Path -LiteralPath $worldLogProj) {
        Write-Note 'building WorldLogVerify (the world log, offline)'
        $worldLogOut = & dotnet build $worldLogProj -c Release -v minimal --nologo 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Bad 'WorldLogVerify build FAILED -- and because it compiles WorldLog.cs and'
            Write-Bad 'EnemyReader.cs from managed/Drelay.Bootstrap, that means those two no longer'
            Write-Bad 'compile without the game in sight.'
            $worldLogOut | Select-Object -Last 30 | ForEach-Object { Write-Host $_ }
            $failures += 'WorldLogVerify'
        } else {
            Write-Ok 'WorldLogVerify built'
        }
    }

    # ---------------------------------------------------------------------------
    # The launcher's configuration, offline: LauncherHarness, also NOT part of the hook.
    # ---------------------------------------------------------------------------
    #
    # SAME ARGUMENT AS THE HARNESS ABOVE.  It compiles the launcher's own settings, config reader,
    # command-line parser, discovery walk and environment builder, so building it is the check that
    # those files are still a pure function of their inputs -- a change that reaches for the
    # registry, the console or the game breaks this build rather than the live one (the registry
    # itself is behind `DiscoveryWindows.cs`, which this project deliberately does not compile).
    # `Test-HookOffline.ps1` runs its C1..C9 invariants.
    $launcherHarnessProj = Join-Path $root 'tools\LauncherHarness\LauncherHarness.csproj'
    if (Test-Path -LiteralPath $launcherHarnessProj) {
        Write-Note 'building LauncherHarness (the launcher configuration, offline)'
        $launcherHarnessOut = & dotnet build $launcherHarnessProj -c Release -v minimal --nologo 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Bad 'LauncherHarness build FAILED -- and because it compiles the launcher''s own'
            Write-Bad 'configuration sources, that means their pure-input contract is broken.'
            $launcherHarnessOut | Select-Object -Last 30 | ForEach-Object { Write-Host $_ }
            $failures += 'LauncherHarness'
        } else {
            Write-Ok 'LauncherHarness built'
        }
    }
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Bad ("BUILD FAILED: " + ($failures -join ', '))
    exit 1
}

# Stamp the output with what it is. A hook build is only meaningful against one pinned build of one
# game, and the two facts a reader of `out\` needs -- which hook this is, and which game build it was
# compiled against -- are not visible in a directory listing of three DLLs and an exe. Written from
# the same two sources the build itself used, so the stamp cannot disagree with what was built.
$versionPath = Join-Path $root 'version.txt'
$version = if (Test-Path -LiteralPath $versionPath) {
    (Get-Content -LiteralPath $versionPath -Raw).Trim()
} else { 'unversioned' }
$gameVersion = 'unknown'
$recordPath = Join-Path $root 'known-build.json'
if (Test-Path -LiteralPath $recordPath) {
    $gameVersion = (Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json).game.version
}
$farmStamp = if (@(Get-ChildItem -LiteralPath (Join-Path $root 'private') -Recurse -Filter '*.cs' -File -ErrorAction SilentlyContinue).Count -gt 0) {
    'private (NOT FOR DISTRIBUTION)'
} else {
    'public stub'
}
$stamp = @(
    "drelay hook $version",
    "game build: Darza's Dominion $gameVersion",
    "built: $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))",
    "farm module: $farmStamp"
) -join "`n"
[System.IO.File]::WriteAllText((Join-Path $outDir 'VERSION.txt'), $stamp + "`n", [System.Text.UTF8Encoding]::new($false))
Write-Note "  stamped out\VERSION.txt as drelay hook $version (game build $gameVersion)"

Write-Ok "build OK -- output in $outDir"
Get-ChildItem -LiteralPath $outDir -File |
    Where-Object { $_.Extension -in '.dll', '.exe' } |
    Sort-Object Name |
    ForEach-Object { Write-Host ("  {0,-28} {1,10:N0} bytes" -f $_.Name, $_.Length) }
exit 0
