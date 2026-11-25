# Changelog

## 1.8.0
- **Updates from GitHub:** the tray helper checks once a day and offers new versions (notification and tray menu). Settings → Updates has "Check now" and an on/off switch. Downloads are checked against the release's SHA-256 before installing, and installing always asks first.
- **Settings backups:** before a new version changes anything, settings are copied to `C:\ProgramData\HPControl\backups\`, named after the version they came from. Settings → **Restore a backup…** puts one back.
- HP's audio components are also kept in `C:\ProgramData\HPControl\fx-saved`, so updates keep the audio controls after the HP app is uninstalled.

## 1.7.5
- Fan curve charts: the curve stays inside the chart (up to 6,000 rpm and 100 °C), the fan-off zone is shaded, and "Right now" says when the fan is off.
- Battery smart: Power saver, and Balanced only for sustained heavy loads (no longer for CPU temperature, which was raising power at light loads with the fan kept off).
- Fewer sensor reads while the app is closed; the minimised window refreshes every 10 s; Battery manager changes no longer freeze the window.

## 1.7.4
- The fan stays at its lowest speed (1,950 rpm) up to about 66 °C; softer chip-power and internal-sensor minimums.

## 1.7.3
- Every page opens instantly and loads in the background; switching pages draws once.

## 1.7.2
- Audio: the EQ shows the driver's real state; it's off with DTS Off and comes back when you pick a preset.

## 1.7.1
- The fan switches off within seconds of a load ending.

## 1.7.0
- HP Control's own smooth fan curve; Performance for sustained heavy loads (plugged in); automatic game detection; Auto is the default.
