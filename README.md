# OShare PC

**English** | [繁體中文](README.zh-TW.md) | [简体中文](README.zh-CN.md)

An unofficial Windows client compatible with OPPO/OnePlus OShare.

OShare PC is not affiliated with OPPO or OnePlus.

## Features

- Send files from PC to phone
- Receive files from phone to PC

## Installation

### Installer

Download `OSharePC-Setup-<version>.exe`, run it, and optionally choose to create a desktop shortcut or start OShare PC with Windows.

### Portable

Download `OSharePC-Portable-<version>-win-x64.zip`, extract it anywhere, and run `oshare_gui.exe`.
## System Requirements

- Windows 10 19041 or later
- 64-bit x64 Windows; ARM can only send files
- A phone that supports mutual transfer

## Usage

Turn on sharing, then send files.

## Building from Source

The backend targets `.NET 10`, and the Flutter project currently uses the stable Flutter `3.44.3` toolchain with Dart `3.12.2`.

Install the .NET SDK and Flutter separately; do not use the ignored local `flutter_sdk` directory.

```powershell
dotnet build OShareSender.csproj -c Release -r win-x64
dotnet publish OShareSender.csproj -c Release -r win-x64 --self-contained true -o artifacts\backend

cd oshare_gui
flutter pub get
flutter analyze
flutter build windows --release
cd ..
```

To assemble the production runtime, copy the Flutter release output to `deploy-gui`, create `deploy-gui\engine`, and place the published `artifacts\backend\OSharePC.exe` in that engine directory. The GUI starts the backend beside it in authenticated bridge mode. `OSharePC.exe` is the backend engine; running it without a mode exits instead of opening a second product UI. The old WinForms interface is retained only as an unsupported debugging surface and must be started explicitly with `OSharePC.exe --legacy-ui`.

For offline backend checks, run `OSharePC.exe --selftest` and `OSharePC.exe --mockphone`. Because the backend is a Windows GUI executable, check `%LOCALAPPDATA%\OSharePC\sender.log` and confirm that `SELFTEST PASSED` and `MOCKPHONE PASSED` appear.

## Disclaimer

OShare PC is an unofficial project and is not affiliated with OPPO or OnePlus.

## License

This project is licensed under the **GNU General Public License v3.0 only (GPL-3.0-only)**. See [LICENSE](LICENSE) for the full license text.
