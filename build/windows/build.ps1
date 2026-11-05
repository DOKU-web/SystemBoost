# SystemBoost Windows build: native engine + app -> Velopack installer, portable zip, update packages.
# Requirements: VS 2022 (C++), CMake, .NET 8 SDK, vpk (dotnet tool install -g vpk --version 0.0.1298).
# Usage: powershell -ExecutionPolicy Bypass -File build\windows\build.ps1 [-Version 0.6.0]
param([string]$Version = "0.6.0")
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\..\.."
$out = "$root\dist\windows"
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null

# 1) native frame generation engine (D3D11)
cmake -S "$root\native\framegen" -B "$root\native\framegen\build" -A x64
cmake --build "$root\native\framegen\build" --config Release

# 2) app: self-contained single exe (no .NET install needed)
$pub = "$out\SystemBoost"
dotnet publish "$root\app\SystemBoost.App\SystemBoost.App.csproj" -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version -o $pub
New-Item -ItemType Directory -Force "$pub\native\framegen\shaders" | Out-Null
Copy-Item "$root\native\framegen\build\Release\sb_framegen.exe" "$pub\native\framegen\"
Copy-Item "$root\native\framegen\build\Release\shaders\*" "$pub\native\framegen\shaders\"
# TODO(v0.5): copy the admin helper here, e.g. Copy-Item ...\SystemBoost.Helper.exe $pub

# 3) code signing (optional: set SIGN_CERT_THUMBPRINT) is done by vpk pack below (--signParams),
#    which signs the app files, the installer and the updater.

# 4) Velopack: installer (SystemBoost-win-Setup.exe), portable zip and update packages in one go.
#    Installed and portable copies both update themselves from GitHub Releases (Services/Updater.cs).
#    Deltas: if the previous release is already in $rel (CI runs `vpk download github` first), only
#    the changed parts are packed for updating users.
$rel = "$out\Releases"
New-Item -ItemType Directory -Force $rel | Out-Null
if ($env:VPK_REPO_URL) { vpk download github --repoUrl $env:VPK_REPO_URL --token $env:VPK_TOKEN -o $rel --channel win }
$notes = "$root\RELEASE_NOTES.md"
$vpkArgs = @("pack", "-u", "SystemBoost", "-v", $Version, "-p", $pub, "--mainExe", "SystemBoost.exe",
             "--packTitle", "SystemBoost", "--icon", "$root\assets\brand\SystemBoost.ico", "--channel", "win", "-o", $rel)
if (Test-Path $notes) { $vpkArgs += @("--releaseNotes", $notes) }
if ($env:SIGN_CERT_THUMBPRINT) { $vpkArgs += @("--signParams", "/sha1 $env:SIGN_CERT_THUMBPRINT /fd sha256 /tr http://timestamp.digicert.com /td sha256") }
vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

# 5) the same installer / portable zip under fixed names for the download page (releases/latest/download/...)
$dl = "$out\download"
New-Item -ItemType Directory -Force $dl | Out-Null
Copy-Item (Get-ChildItem $rel -Filter "*-Setup.exe" | Select-Object -First 1).FullName "$dl\SystemBoost-Windows-Setup.exe"
Copy-Item (Get-ChildItem $rel -Filter "*-Portable.zip" | Select-Object -First 1).FullName "$dl\SystemBoost-Windows-Portable.zip"
Get-ChildItem $rel, $dl -File | Format-Table Name, Length
