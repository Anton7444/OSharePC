# OShare PC

[English](README.md) | [繁體中文](README.zh-TW.md) | **简体中文**

一个与 OPPO/OnePlus OShare 兼容的非官方 Windows 客户端。

OShare PC 与 OPPO 或 OnePlus 没有关联，也未获得其认可或背书。

## 功能

- Windows ↔ 支持 OShare 的设备
- 从 PC 向手机发送文件
- 从手机接收文件到 PC
- 附近设备发现
- 原生 Flutter/Windows 界面
- 系统托盘运行、可选的开机启动，以及安装版中的最小化启动

兼容性取决于手机型号以及 OShare 兼容应用的版本。

## 安装

### 安装程序

下载 `OSharePC-Setup-<version>.exe`，运行后可根据需要选择创建桌面快捷方式或 Windows 开机启动。安装程序会将 OShare PC 安装到当前用户的 Programs 目录下。

### 便携版

下载 `OSharePC-Portable-<version>-win-x64.zip`，解压到任意位置后运行 `catshare_gui.exe`。便携版无需安装，也不会注册开始菜单、桌面快捷方式或 Windows 开机启动项。

## 系统要求

- Windows 10 19041 或更高版本
- 64 位 x64 Windows
- 手机和 PC 必须能够使用兼容的 OShare 传输路径

## 使用方法

打开 OShare PC；当你希望 PC 可被其他设备发现时，请保持接收功能启用。若要发送文件，选择附近的手机、选择文件，然后开始传输。若要接收文件，请在出现确认对话框时接受传入请求。接收到的文件会保存到已配置的目标文件夹。

设置页面包含目标文件夹、接收／系统托盘行为、语言、外观和开机启动选项。

## 从源代码构建

后端目标为 `.NET 10`，Flutter 项目当前使用稳定版 Flutter `3.44.3` 工具链和 Dart `3.12.2`。

请分别安装 .NET SDK 和 Flutter；不要使用被忽略的本地 `flutter_sdk` 目录。

```powershell
dotnet build CatShareSender.csproj -c Release -r win-x64
dotnet publish CatShareSender.csproj -c Release -r win-x64 --self-contained true -o artifacts\backend

cd catshare_gui
flutter pub get
flutter analyze
flutter build windows --release
cd ..
```

若要组装正式运行环境，请将 Flutter release 输出复制到 `deploy-gui`，创建 `deploy-gui\engine`，并将已 publish 的 `artifacts\backend\CatShareSender.exe` 放入该 engine 目录。GUI 会以 bridge 模式启动位于其旁边的后端。

若要进行离线后端检查，请运行 `CatShareSender.exe --selftest` 和 `CatShareSender.exe --mockphone`。由于后端是 Windows GUI 可执行文件，请查看 `%LOCALAPPDATA%\CatShareSender\sender.log`，确认其中出现 `SELFTEST PASSED` 和 `MOCKPHONE PASSED`。

## 已知限制

- 设备发现和传输兼容性取决于手机的 OShare 实现以及 Windows 的蓝牙／Wi-Fi 环境。
- 若要验证特定手机型号和特定传输方向是否兼容，仍需要使用实体手机测试。

## 免责声明

OShare PC 是非官方项目，与 OPPO 或 OnePlus 没有关联，也未获得其认可或背书。

## 许可证

本项目采用 **GNU General Public License v3.0 only（GPL-3.0-only）** 许可。完整许可证文本请参阅 [LICENSE](LICENSE)。
