# OShare PC

An unofficial Windows client compatible with OPPO/OnePlus OShare.

OShare PC is not affiliated with or endorsed by OPPO or OnePlus.

## Features

- Windows ↔ supported OShare devices
- Send files from PC to phone
- Receive files from phone to PC
- Nearby device discovery
- Native Flutter/Windows interface
- System-tray operation, optional startup, and minimized startup in the installed version

Compatibility depends on the phone model and the OShare-compatible app version.

## Installation

### Installer

Download `OSharePC-Setup-<version>.exe`, run it, and choose the optional desktop-shortcut or Windows-startup tasks if needed. The installer places OShare PC under the per-user Programs directory.

### Portable

Download `OSharePC-Portable-<version>-win-x64.zip`, extract it anywhere, and run `catshare_gui.exe`. The portable package has no installer and does not register Start Menu, desktop, or Windows-startup entries.

## Requirements

- Windows 10 version 19041 or later
- 64-bit x64 Windows
- A phone and PC that can use a compatible OShare transfer path

## Usage

Open OShare PC and leave receiving enabled when you want the PC to be discoverable. To send, choose a nearby phone, select files, and start the transfer. To receive, accept the incoming request when the confirmation dialog appears. Received files are saved to the configured destination folder.

The Settings page contains the destination folder, receive/tray behavior, language, appearance, and startup options.

## Building from Source

The backend targets `.NET 10` and the Flutter project currently uses the stable Flutter `3.44.3` toolchain with Dart `3.12.2`.

Install the .NET SDK and Flutter independently; do not use the ignored local `flutter_sdk` directory.

```powershell
dotnet build CatShareSender.csproj -c Release -r win-x64
dotnet publish CatShareSender.csproj -c Release -r win-x64 --self-contained true -o artifacts\backend

cd catshare_gui
flutter pub get
flutter analyze
flutter build windows --release
cd ..
```

To assemble the production runtime, copy the Flutter release output to `deploy-gui`, create `deploy-gui\engine`, and place the published `artifacts\backend\CatShareSender.exe` in that engine directory. The GUI starts that backend beside itself in bridge mode.

For offline backend checks, run `CatShareSender.exe --selftest` and `CatShareSender.exe --mockphone`. Because the backend is a Windows GUI executable, inspect `%LOCALAPPDATA%\CatShareSender\sender.log` for `SELFTEST PASSED` and `MOCKPHONE PASSED`.

## Privacy

Transfers are designed to occur locally between the Windows PC and the connected device. The application writes operational logs and received files locally; review the configured destination and log path before sharing diagnostic data.

## Known Limitations

- Device discovery and transfer compatibility depends on the phone's OShare implementation and Windows Bluetooth/Wi-Fi environment.
- Physical phone testing is required to validate a particular phone model and transfer direction.
- The project currently has no published open-source license.

## Contributing

Please open an issue with reproducible steps, Windows version, phone/app version, and relevant redacted log excerpts. Pull requests should preserve the existing transfer protocol behavior and include appropriate build or test evidence.

## Disclaimer

OShare PC is an unofficial project and is not affiliated with or endorsed by OPPO or OnePlus.

## License

This project currently has no published open-source license. Do not assume permission to redistribute or modify it until a license is chosen.
