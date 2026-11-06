# Puts the current source on this PC's Desktop SystemBoost shortcut, with no public release.
# Each build goes to a new folder, so a running copy is left alone; the next start from the
# shortcut gets the new build. The copy uses the same settings and license as the installed app.
#   powershell -ExecutionPolicy Bypass -File build\windows\update_desktop_launcher.ps1
#   ... -Restore   points the shortcut back at the installed app
# Run this after every change to the app or the engines.
param([switch]$Restore)
$ErrorActionPreference = "Continue"
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
$base = Join-Path $env:LOCALAPPDATA "SystemBoost-Launcher"
$installed = Join-Path $env:LOCALAPPDATA "SystemBoost\current\SystemBoost.exe"
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
    Run "dotnet" @("publish", "$root\app\SystemBoost.App\SystemBoost.App.csproj", "-c", "Release", "-r", "win-x64",
                   "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
                   "-v", "q", "-nologo", "-o", $staging)

    New-Item -ItemType Directory -Force "$staging\native\framegen\shaders" | Out-Null
    Copy-Item "$root\native\framegen\build\Release\sb_framegen.exe" "$staging\native\framegen\"
    Copy-Item "$root\native\framegen\build\Release\shaders\*" "$staging\native\framegen\shaders\"
    Copy-Item "$root\native\helper\build\Release\sb_helper.exe" $staging
    Get-ChildItem $staging -Filter *.pdb -File | Remove-Item -Force

    $dir = Join-Path $base $name
    Rename-Item $staging $dir
    $staging = $null
    $exe = Join-Path $dir "SystemBoost.exe"
    foreach ($need in @($exe, "$dir\sb_helper.exe", "$dir\native\framegen\sb_framegen.exe", "$dir\native\framegen\shaders\interpolate.cso")) {
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
