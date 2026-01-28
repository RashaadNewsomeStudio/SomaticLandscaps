# Somatic Launcher

A WPF Game Launcher with watchdog capabilities, configuration management, and server status reporting.

## Features
- **Process Management**: Start/Stop games, auto-restart on crash (< 1s watchdog).
- **Config Editor**: Auto-detects `config/ArtworkConfig.json`, edits strongly-typed fields while preserving comments.
- **Status Reporting**: Sends lifecycle events (launched, stopped, crashed) to `somaticstatusserver`.
- **UI**: Frosted glass aesthetics, accordion layout, log viewer.

## Requirements
- Windows 10/11
- .NET 8 Runtime

## Build Instructions
1. Open terminal in `src/SomaticLauncher`.
2. Run `dotnet publish -c Release -r win-x64 --self-contained false`.
3. Output will be in `bin/Release/net8.0-windows/win-x64/publish`.

## Usage
1. Run `SomaticLauncher.exe`.
2. Click "..." to select a Game executable.
3. If `config/ArtworkConfig.json` is missing, click "Create Default" in the Config section.
4. Adjust settings and click "Save Changes".
5. Click "PLAY" to launch the game.
6. Logs are shown in the "Logs" section.
