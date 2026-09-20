# OShare PC

[English](README.md) | **繁體中文** | [简体中文](README.zh-CN.md)

一個與 OPPO/OnePlus OShare 相容的非官方 Windows 用戶端。

OShare PC 與 OPPO 或 OnePlus 並無關聯

## 功能

- 從 PC 傳送檔案到手機
- 從手機接收檔案到 PC

## 安裝

### 安裝程式

下載 `OSharePC-Setup-<version>.exe`，執行後可依需要選擇建立桌面捷徑或 Windows 開機啟動。

### 可攜版

下載 `OSharePC-Portable-<version>-win-x64.zip`，解壓縮到任意位置後執行 `oshare_gui.exe`。
## 系統需求

- Windows 10 19041 或更新版本
- 64 位元 x64 Windows，ARM只能發檔案
- 手機支持互傳

## 使用方式

把分享打開，然後發檔案

## 從原始碼建置

後端目標為 `.NET 10`，Flutter 專案目前使用穩定版 Flutter `3.44.3` 工具鏈與 Dart `3.12.2`。

請分別安裝 .NET SDK 與 Flutter；不要使用被忽略的本機 `flutter_sdk` 目錄。

```powershell
dotnet build OShareSender.csproj -c Release -r win-x64
dotnet publish OShareSender.csproj -c Release -r win-x64 --self-contained true -o artifacts\backend

cd oshare_gui
flutter pub get
flutter analyze
flutter build windows --release
cd ..
```

若要組裝正式執行環境，請將 Flutter release 輸出複製到 `deploy-gui`，建立 `deploy-gui\engine`，並將已 publish 的 `artifacts\backend\OSharePC.exe` 放到該 engine 目錄中。GUI 會以具驗證的 bridge 模式啟動位於其旁邊的後端。`OSharePC.exe` 定位為後端 engine；不帶模式參數直接啟動時會退出，不再開啟第二套產品 UI。舊 WinForms 介面只保留作不受支援的除錯用途，必須明確使用 `OSharePC.exe --legacy-ui` 啟動。

若要進行離線後端檢查，請執行 `OSharePC.exe --selftest` 與 `OSharePC.exe --mockphone`。由於後端是 Windows GUI 執行檔，請查看 `%LOCALAPPDATA%\OSharePC\sender.log`，確認其中出現 `SELFTEST PASSED` 與 `MOCKPHONE PASSED`。


## 免責聲明

OShare PC 是非官方專案，與 OPPO 或 OnePlus 並沒有關聯

## 授權

本專案採用 **GNU General Public License v3.0 only（GPL-3.0-only）** 授權。完整授權條款請參閱 [LICENSE](LICENSE)。
