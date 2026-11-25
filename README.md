# HP Control

A lightweight replacement for the HP app's **System control**, **Battery manager** and **Audio** pages, made for the **HP OmniBook X Flip 16** (Intel Core Ultra 200V, board 8DA1).

- **Quiet fan control:** HP Control drives the fan itself through HP's firmware: off while the laptop is cool and idle, then a smooth, ramped curve instead of the fan controller's on/off jumps.
- **Auto mode:** HP Quiet for everyday use, Performance (35 W) for sustained heavy loads and games when plugged in, Power saver on battery.
- **Battery manager:** HP's charge limits (Maximize battery health, Adaptive battery extender), battery health and details.
- **Audio:** DTS presets and EQ, microphone effects, device and volume controls.
- Battery efficient: a small Windows service plus an event-driven tray helper. No kernel driver.

## Install
Download the latest **HPControl-x.y.z.zip** from [Releases](https://github.com/Hellohi3654/HPControl/releases), extract it, and run `install.cmd`. Full details: [installer/README.md](installer/README.md).

From 1.8.0 on, HP Control checks for new versions once a day and offers to install them (Settings → Updates). Your settings are backed up before every update.

## HP's components
The audio features use HP's own audio components, which the installer copies from the HP app (myHP) already installed on your laptop. They are not part of this repository or its releases.

## Build
- **Windows:** .NET SDK 8 and the .NET Framework 4.8 targeting pack.
  `dotnet build src/Service/Service.csproj -c Release -o out/bin` then the same for `src/App/App.csproj`.
- **Linux:** .NET SDK 8 and Mono (`/usr/lib/mono/4.8-api`): `tools/package.sh` builds and packages a release zip.

## Releasing
1. Bump `AppInfo.Version` in `src/Common/Protocol.cs` and add a section to `CHANGELOG.md`.
2. If settings change, add a `MigrateToVn` step in `src/Service/Store.cs`: only change values still at their old default (settings are backed up automatically first).
3. Push to `main`. When the version has no release yet, the release workflow builds the zip and publishes it as `vX.Y.Z` with its SHA-256 (the app's updater checks it).

## Disclaimer
Not affiliated with or endorsed by HP. It talks to the laptop's firmware through HP's own WMI interface and only uses calls that were verified on this model. Use at your own risk.
