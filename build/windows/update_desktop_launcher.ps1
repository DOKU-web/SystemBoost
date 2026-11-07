# Puts the current source on this PC's Desktop SystemBoost shortcut, with no public release.
# Each build goes to a new folder, so a running copy is left alone; the next start from the
# shortcut gets the new build. The copy uses the same settings and license as the installed app.
# Each folder is laid out like an install (Update.exe beside current\, a manifest inside), which is
# what the app checks for: its update card and update check work here too.
#   powershell -ExecutionPolicy Bypass -File build\windows\update_desktop_launcher.ps1
#   ... -Restore   points the shortcut back at the installed app
# Run this after every change to the app or the engines.
param([switch]$Restore)
$ErrorActionPreference = "Continue"
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
$base = Join-Path $env:LOCALAPPDATA "SystemBoost-Launcher"
$installed = Join-Path $env:LOCALAPPDATA "SystemBoost\current\SystemBoost.exe"
$updater = Join-Path $env:LOCALAPPDATA "SystemBoost\Update.exe"
$link = Join-Path ([Environment]::GetFolderPath("Desktop")) "SystemBoost.lnk"
$cmake = "C:\Program\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
$log = Join-Path $base "update.log"
New-Item -ItemType Directory -Force $base | Out-Null

function Say($text) { Add-Content $log "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $text" -Encoding UTF8; Write-Output $text }
function Run($exe, [string[]]$arguments) {
    & $exe @arguments 2>&1 | ForEach-Object { "$_" } | Add-Content $log -Encoding UTF8
    if ($LASTEXITCODE -ne 0) { throw "$(Split-Path $exe -Leaf) failed with exit code $LASTEXITCODE" }
}
function Point-Link($exe) {
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($link)
    $s.TargetPath = $exe
    $s.WorkingDirectory = Split-Path $exe
    $s.IconLocation = "$exe,0"
    $s.Save()
    if ($shell.CreateShortcut($link).TargetPath -ne $exe) { throw "the shortcut did not change" }
}

if ($Restore) {
    if (-not (Test-Path $installed)) { throw "no installed app at $installed" }
    Point-Link $installed
    Say "shortcut points at the installed app again"
    exit 0
}

$lock = New-Object System.Threading.Mutex($false, "Local\SystemBoostLauncherUpdate")
try { [void]$lock.WaitOne() } catch [System.Threading.AbandonedMutexException] { }
$staging = $null
try {
    $sha = (& git -C $root rev-parse --short HEAD).Trim()
    $name = "$(Get-Date -Format 'yyyyMMdd-HHmmss')-$sha"
    Say "start $name"
    $staging = Join-Path $base "$name.tmp"

    Run $cmake @("--build", "$root\native\framegen\build", "--config", "Release")
    Run $cmake @("--build", "$root\native\helper\build", "--config", "Release")
    $app = Join-Path $staging "current"
    Run "dotnet" @("publish", "$root\app\SystemBoost.App\SystemBoost.App.csproj", "-c", "Release", "-r", "win-x64",
                   "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
                   "-v", "q", "-nologo", "-o", $app)

    New-Item -ItemType Directory -Force "$app\native\framegen\shaders" | Out-Null
    Copy-Item "$root\native\framegen\build\Release\sb_framegen.exe" "$app\native\framegen\"
    Copy-Item "$root\native\framegen\build\Release\shaders\*" "$app\native\framegen\shaders\"
    Copy-Item "$root\native\helper\build\Release\sb_helper.exe" $app
    Get-ChildItem $app -Filter *.pdb -File | Remove-Item -Force

    # Same shortcut fields as the real package: an update then has no new shortcut to create.
    if (Test-Path $updater) {
        $csproj = "$root\app\SystemBoost.App\SystemBoost.App.csproj"
        $version = (Select-String -Path $csproj -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
        Copy-Item $updater $staging
        New-Item -ItemType Directory -Force "$staging\packages" | Out-Null
        $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
<metadata>
<id>SystemBoost</id>
<title>SystemBoost</title>
<description>SystemBoost</description>
<authors>SystemBoost</authors>
<version>$version</version>
<channel>win</channel>
<mainExe>SystemBoost.exe</mainExe>
<os>win</os>
<rid>win</rid>
<shortcutLocations>Desktop,StartMenuRoot</shortcutLocations>
<shortcutAmuid>velopack.SystemBoost</shortcutAmuid>
</metadata>
</package>
"@
        [IO.File]::WriteAllText("$app\sq.version", $manifest, (New-Object Text.UTF8Encoding($false)))
    } else {
        Say "no installed app: this build has no update card"
    }

    $dir = Join-Path $base $name
    Rename-Item $staging $dir
    $staging = $null
    $exe = Join-Path $dir "current\SystemBoost.exe"
    foreach ($need in @($exe, "$dir\current\sb_helper.exe", "$dir\current\native\framegen\sb_framegen.exe", "$dir\current\native\framegen\shaders\interpolate.cso")) {
        if (-not (Test-Path $need)) { throw "missing $need" }
    }
    Point-Link $exe
    Set-Content (Join-Path $base "latest.txt") $name -Encoding ascii

    # Keeps the two newest builds and anything still running.
    $running = @(Get-Process -Name SystemBoost -ErrorAction SilentlyContinue | ForEach-Object { $_.Path } | Where-Object { $_ })
    Get-ChildItem $base -Directory | Sort-Object Name -Descending | Select-Object -Skip 2 | ForEach-Object {
        $folder = $_.FullName
        if (-not ($running | Where-Object { $_.StartsWith("$folder\", [StringComparison]::OrdinalIgnoreCase) })) {
            Remove-Item $folder -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Say "done $name version $((Get-Item $exe).VersionInfo.ProductVersion)"
}
catch {
    Say "FAILED: $($_.Exception.Message)"
    if ($staging -and (Test-Path $staging)) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
    exit 1
}
finally {
    $lock.ReleaseMutex()
}
