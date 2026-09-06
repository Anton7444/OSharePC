# AGENTS.md — AI 助手操作规则（所有 AI 工具必读）

> 本项目由多个 AI 工具（ZCode、Gemini/Antigravity、Mimo 等）先后修改过。
> 历史教训：两个 AI 同时改代码 → 互相覆盖、旧 exe 残留、多个实例抢占蓝牙广播槽和端口。
> **以下规则是强制的，不是建议。**

## 1. 唯一工作目录

```
<repo-root>\
```

- 只允许在这一份源代码上工作。
- **禁止**创建副本目录（publish-x / publish-fixed / deploy-SFG14-01-package 之类的散装发布目录一律不得新建）。
- 仓库里其他目录的用途，改代码前先弄清：
  - `..\CatShare-main\` — Android 端参考源码（协议真相来源，只读）
  - `..\apk\` — 反编译的互传 APK（只读参考）
  - `deploy-gui\` — 成品运行目录（`catshare_gui.exe` + `engine\CatShareSender.exe`）
  - `catshare_gui\` — Flutter GUI 源码（bridge 模式会自动拉起 `engine\CatShareSender.exe --bridge`）
  - `flutter_sdk\` — 重新编译 Flutter GUI 才需要

## 2. 动手前

```bash
git status          # 有 git 历史后：先看当前状态，禁止基于旧副本修改
tasklist | findstr /i catshare   # 确认没有别的实例在跑（单实例互斥会挡你）
```

- 用户当前实际使用的运行方式：`deploy-gui\catshare_gui.exe`（Flutter UI）。
- `MainForm.cs`（WinForms 界面）是**旧 UI**，仍可编译但不是用户主路径。

## 3. 改完代码必须全跑（全过才算完成）

```bash
dotnet build CatShareSender.csproj -c Release -r win-x64
.\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\CatShareSender.exe --selftest    # 必须 SELFTEST PASSED
.\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\CatShareSender.exe --mockphone   # 必须 MOCKPHONE PASSED
```

- 注意：程序是 WinExe，控制台输出会被吞——结果看日志 `%LOCALAPPDATA%\CatShareSender\sender.log`。

## 4. 部署（唯一位置）

编译通过后，把发布 exe 复制到：

```
deploy-gui\engine\CatShareSender.exe
catshare_gui\build\windows\x64\runner\Release\engine\CatShareSender.exe
```

**禁止**把 exe 复制到项目根目录、publish-* 目录之外的地方。`deploy-gui\engine` 被占用（正在运行）时先 `taskkill /IM CatShareSender.exe /F`，复制完再启动 GUI。

## 5. 版本号

`Program.cs` 顶部的 `Version` 常量必须随每次发布递增（格式 `yyyy.MM.dd-HHmm`）。
日志里的版本是判断"哪个构建在跑"的唯一可靠手段。

## 6. 并发纪律（历史事故的根源）

- **同一时间只允许一个 AI 会话在本项目工作。** 开始前问用户是否有其他会话开着。
- 不要启动旧版本的 exe 来"对照测试"——所有副本已统一，直接用最新源码编译。
- 发现自己要改的文件和预期不符（有陌生改动），先停下来告诉用户，不要基于它继续叠改。

## 7. 领域知识速查（改协议代码前必读）

- 协议详解、BLE 广播格式、Windows 广播能力限制：见 `README.md`（很长但权威）。
- Windows 单广播槽：8881 beacon / 9955 CatShare GATT 广播 / 非可连接 fallback 广播三者由
  `SenderEngine` 协调（`RetryGate` / `Pause` / `ResumeAfter`），**改动前必须理解谁拥有槽位**。
- 热点连接配置文件名固定 `CatShare-Link`（`WifiJoiner.ProfileName`），启动时自动清理残留——别改回随机名。
- 单实例互斥体：`Local\CatShareSender-SingleInstance`（`Program.cs`）。
- 端口 8959 = 传输服务器；8960 = Flutter bridge（只监听 127.0.0.1）。
- 日志是诊断的第一入口：`%LOCALAPPDATA%\CatShareSender\sender.log`（无日期前缀，跨天会混排）。

## 8. 已知事实（不要再"发现"一遍）

- 本机蓝牙栈：`GattServiceProvider` 广播 Aborted；0x02/0x03/0x06/0x07/0x09 raw 段 unauthorized；
  只有 raw 0x16 service data / 0x21 / 0xFF manufacturer data 可发布（详见 `--advprobe` 与
  `GattServiceFallbackAdvertiser.cs` 注释）。
- 手机→PC 接收的真实路径：手机开热点（如 `OnePlus 13T@OShareF6`）→ PC 用 `WifiJoiner` 加入 → 拉 ZIP。
- `--advprobe`、`--zipprobe`、`--serve N` 是诊断工具，改广播/下载代码后应跑。
