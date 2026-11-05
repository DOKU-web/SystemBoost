# Third-party components

SystemBoost's own code (app, native engines, shaders) is original. The macOS
"윈도우 게임" feature downloads these components on first use; they are not
bundled in the installer.

| Component | License | Source | How it is used |
|---|---|---|---|
| Wine (macOS build by Gcenx) | LGPL-2.1-or-later | https://github.com/Gcenx/macOS_Wine_builds , https://gitlab.winehq.org/wine/wine | Downloaded unmodified, run as a separate process |
| DXVK-macOS | zlib | https://github.com/Gcenx/DXVK-macOS | Downloaded unmodified, loaded by Wine |
| MoltenVK (inside the Wine build) | Apache-2.0 | https://github.com/KhronosGroup/MoltenVK | Part of the Wine build |
| Apple Game Porting Toolkit / D3DMetal | Apple license | https://developer.apple.com/games/ | **Not distributed.** The user downloads it from Apple, accepts Apple's license, and selects its folder |
| Avalonia | MIT | https://github.com/AvaloniaUI/Avalonia | UI framework |
| .NET runtime | MIT | https://github.com/dotnet/runtime | Bundled (self-contained) |
| NVAPI | MIT | https://github.com/NVIDIA/nvapi | Linked into the Windows admin helper (per-game driver setting) |

LGPL note: because Wine is downloaded as-is from its public release and run as
a separate program, users can replace it with any other Wine build in
~/Library/Application Support/SystemBoost/wine/runtime. Source for the exact
build is linked above.

## Velopack (automatic updates)
https://github.com/velopack/velopack — MIT License. The app uses the `Velopack` NuGet package to check
GitHub Releases and apply updates; the installer/updater binaries come from the `vpk` tool at build time.
