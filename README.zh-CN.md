# OShare PC

[English](README.md) | [繁體中文](README.zh-TW.md) | **简体中文**

一个与 OPPO/OnePlus OShare 兼容的非官方 Windows 客户端。

OShare PC 与 OPPO 或 OnePlus 并没有关联。

## 功能

- 从 PC 发送文件到手机
- 从手机接收文件到 PC

## 安装

### 安装程序

下载 `OSharePC-Setup-<version>.exe`，运行后可根据需要选择创建桌面快捷方式或 Windows 开机启动。

### 便携版

下载 `OSharePC-Portable-<version>-win-x64.zip`，解压到任意位置后运行 `catshare_gui.exe`。

## 系统要求

- Windows 10 19041 或更新版本
- 64 位 x64 Windows，ARM 只能发送文件
- 手机支持互传

## 使用方式

把分享打开，然后发文件。

## 从源代码构建

后端目标为 `.NET 10`，Flutter 项目目前使用稳定版 Flutter `3.44.3` 工具链与 Dart `3.12.2`。

请分别安装 .NET SDK 与 Flutter；不要使用被忽略的本地 `flutter_sdk` 目录。

```powershell
dotnet build CatShareSender.csproj -c Release -r win-x64
dotnet publish CatShareSender.csproj -c Release -r win-x64 --self-contained true -o artifacts\backend

cd catshare_gui
flutter pub get
flutter analyze
flutter build windows --release
cd ..
```

若要组装正式运行环境，请将 Flutter release 输出复制到 `deploy-gui`，创建 `deploy-gui\engine`，并将已 publish 的 `artifacts\backend\CatShareSender.exe` 放到该 engine 目录中。GUI 会以带验证的 bridge 模式启动位于其旁边的后端。`CatShareSender.exe` 定位为后端 engine；不带模式参数直接启动时会退出，不再打开第二套产品 UI。旧 WinForms 界面只保留作不受支持的调试用途，必须明确使用 `CatShareSender.exe --legacy-ui` 启动。

若要进行离线后端检查，请运行 `CatShareSender.exe --selftest` 与 `CatShareSender.exe --mockphone`。由于后端是 Windows GUI 可执行文件，请查看 `%LOCALAPPDATA%\CatShareSender\sender.log`，确认其中出现 `SELFTEST PASSED` 与 `MOCKPHONE PASSED`。

## 免责声明

OShare PC 是非官方项目，与 OPPO 或 OnePlus 并没有关联。

## 许可证

本项目采用 **GNU General Public License v3.0 only（GPL-3.0-only）** 许可。完整许可证文本请参阅 [LICENSE](LICENSE)。
