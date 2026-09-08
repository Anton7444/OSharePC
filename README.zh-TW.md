# OShare PC

[English](README.md) | **繁體中文** | [简体中文](README.zh-CN.md)

一個與 OPPO/OnePlus OShare 相容的非官方 Windows 用戶端。

OShare PC 與 OPPO 或 OnePlus 並無關聯，亦未獲其認可或背書。

## 功能

- Windows ↔ 支援 OShare 的裝置
- 從 PC 傳送檔案到手機
- 從手機接收檔案到 PC
- 附近裝置探索
- 原生 Flutter/Windows 介面
- 系統匣運作、可選的開機啟動，以及安裝版中的最小化啟動

相容性取決於手機型號以及 OShare 相容應用程式的版本。

## 安裝

### 安裝程式

下載 `OSharePC-Setup-<version>.exe`，執行後可依需要選擇建立桌面捷徑或 Windows 開機啟動。安裝程式會將 OShare PC 安裝到目前使用者的 Programs 目錄下。

### 可攜版

下載 `OSharePC-Portable-<version>-win-x64.zip`，解壓縮到任意位置後執行 `catshare_gui.exe`。可攜版不需要安裝，也不會註冊開始功能表、桌面捷徑或 Windows 開機啟動項目。

## 系統需求

- Windows 10 19041 或更新版本
- 64 位元 x64 Windows
- 手機與 PC 必須能使用相容的 OShare 傳輸路徑

## 使用方式

開啟 OShare PC；當你希望 PC 可被其他裝置探索時，請保持接收功能啟用。若要傳送檔案，選擇附近的手機、選取檔案，然後開始傳輸。若要接收檔案，請在出現確認對話框時接受傳入要求。接收到的檔案會儲存到已設定的目的地資料夾。

設定頁面包含目的地資料夾、接收／系統匣行為、語言、外觀與開機啟動選項。

## 從原始碼建置

後端目標為 `.NET 10`，Flutter 專案目前使用穩定版 Flutter `3.44.3` 工具鏈與 Dart `3.12.2`。

請分別安裝 .NET SDK 與 Flutter；不要使用被忽略的本機 `flutter_sdk` 目錄。

```powershell
dotnet build CatShareSender.csproj -c Release -r win-x64
dotnet publish CatShareSender.csproj -c Release -r win-x64 --self-contained true -o artifacts\backend

cd catshare_gui
flutter pub get
flutter analyze
flutter build windows --release
cd ..
```

若要組裝正式執行環境，請將 Flutter release 輸出複製到 `deploy-gui`，建立 `deploy-gui\engine`，並將已 publish 的 `artifacts\backend\CatShareSender.exe` 放到該 engine 目錄中。GUI 會以 bridge 模式啟動位於其旁邊的後端。

若要進行離線後端檢查，請執行 `CatShareSender.exe --selftest` 與 `CatShareSender.exe --mockphone`。由於後端是 Windows GUI 執行檔，請查看 `%LOCALAPPDATA%\CatShareSender\sender.log`，確認其中出現 `SELFTEST PASSED` 與 `MOCKPHONE PASSED`。

## 已知限制

- 裝置探索與傳輸相容性取決於手機的 OShare 實作以及 Windows 的藍牙／Wi-Fi 環境。
- 若要確認特定手機型號與特定傳輸方向是否相容，仍需要使用實體手機測試。

## 免責聲明

OShare PC 是非官方專案，與 OPPO 或 OnePlus 並無關聯，亦未獲其認可或背書。

## 授權

此專案目前尚未發布開放原始碼授權條款。在選定授權條款之前，請勿假定你具有重新散布或修改此專案的權限。
