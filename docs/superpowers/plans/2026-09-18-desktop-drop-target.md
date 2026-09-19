# Desktop Drop Target Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Add an optional bottom-right Windows desktop drop panel that lists discovered phones, accepts files/folders, and immediately sends them to the selected phone using the existing transfer pipeline.

**Architecture:** The main Flutter GUI launches the same executable as a companion --desktop-drop-panel process when the new setting is enabled. The companion receives the main process bridge token through its environment, uses a BridgeClient without backend supervision, renders a frameless themed Flutter panel, and reuses the existing native OLE drag/drop bridge, staging controller, device model, and send API.

**Tech Stack:** Flutter 3.44.3 / Dart 3.12.2, Flutter Windows runner C++17, window_manager, shared_preferences, existing HTTP bridge, existing DragDropBridge OLE implementation, Flutter widget tests, and the PowerShell build pipeline.

**Spec:** docs/superpowers/specs/2026-09-18-desktop-drop-target-design.md

## Global Constraints

- The setting key is desktop_drop_target_enabled and defaults to false.
- The panel process is launched with --desktop-drop-panel and must never create a tray icon or start a second backend.
- The main process passes the existing bridge token through OSHAREPC_BRIDGE_TOKEN; no token is written to disk.
- All panel strings must exist in English, Simplified Chinese, and Traditional Chinese.
- All panel colors must come from AppColors, AppTheme, and the selected AccentPreset.
- Existing full-window drag/drop and send behavior must remain unchanged.
- The final verification command is the repository-root build-local.cmd supplied by the user.

---

### Task 1: Add failing tests for panel behavior and translations

Files:
- Create catshare_gui/test/desktop_drop_panel_test.dart.
- Create catshare_gui/test/language_test.dart.

Interfaces:
- Consume panelWidthForDeviceCount, resolveSelectedDevice, and appText.
- Produce tests that fail before the feature exists.

- [ ] Step 1: Write tests for panel width and device fallback. Assert that panelWidthForDeviceCount(0) is 560, two devices produce a value greater than 560, and 20 devices produce 960. Build two DeviceModel values and assert that resolveSelectedDevice preserves an existing address, falls back to the first remaining device, and returns null for an empty list.
- [ ] Step 2: Write a localization coverage test over every AppLanguage value. The required keys are desktopDropTarget, desktopDropTargetHint, desktopDropTitle, desktopDropNoDevices, desktopDropSelectDevice, and desktopDropReleaseToSend. Assert that each value is non-empty and is not equal to its key.
- [ ] Step 3: Run from catshare_gui: ..\flutter_sdk\flutter\bin\flutter.bat test test\desktop_drop_panel_test.dart test\language_test.dart. Confirm the failure is caused by the missing panel helpers or localization keys, not by test syntax.

### Task 2: Add panel process ownership and bridge-token injection

Files:
- Modify catshare_gui/lib/services/bridge_client.dart.
- Create catshare_gui/lib/services/desktop_drop_panel_service.dart.
- Modify catshare_gui/lib/main.dart.
- Modify catshare_gui/lib/services/tray_service.dart.

Interfaces:
- Produce BridgeClient({String? bridgeToken, bool manageBackend = true}), String get bridgeToken, and DesktopDropPanelService.enable/disable/dispose.
- Preserve the current constructor behavior for the main GUI.

- [ ] Step 1: Replace BridgeClient's eager generated-token field with constructor state. Use OSHAREPC_BRIDGE_TOKEN when a token is not explicitly provided, otherwise generate the current secure token. Store manageBackend and expose the token through bridgeToken.
- [ ] Step 2: Guard _ensureBackendRunning with manageBackend. The panel must be able to poll and send through the existing bridge but must never launch CatShareSender.exe.
- [ ] Step 3: Implement DesktopDropPanelService.enable. Start Platform.resolvedExecutable with the single argument --desktop-drop-panel, pass OSHAREPC_BRIDGE_TOKEN equal to bridgeClient.bridgeToken via Process.start with includeParentEnvironment true, store the Process, and clear the field when the child exits.
- [ ] Step 4: Implement disable and dispose. Kill the stored process, await exit for no more than two seconds, clear the process reference, and make repeated calls safe.
- [ ] Step 5: Construct the service beside BridgeClient, pass it to CatShareApp and TrayService, and disable it before backend shutdown and during app disposal.
- [ ] Step 6: Run Push-Location catshare_gui; ..\flutter_sdk\flutter\bin\flutter.bat analyze; Pop-Location. Fix only errors caused by this task.

### Task 3: Implement the themed desktop panel

Files:
- Create catshare_gui/lib/pages/desktop_drop_panel.dart.
- Modify catshare_gui/lib/main.dart.

Interfaces:
- Consume BridgeClient, OutgoingStagingController, DeviceModel, NativeDropZone, AppTheme, AppColors, window_manager, and appText.
- Produce panelWidthForDeviceCount(int), resolveSelectedDevice(List<DeviceModel>, String?), and DesktopDropPanelApp.

- [ ] Step 1: Implement panelWidthForDeviceCount. Clamp the input to 0 through 20 and return (560 + count * 120) clamped to 560 through 960.
- [ ] Step 2: Implement resolveSelectedDevice. Return null for no devices, preserve the selected address when present, otherwise return the first device.
- [ ] Step 3: Run the focused desktop_drop_panel_test.dart. Confirm the geometry and device-selection tests pass.
- [ ] Step 4: Add the panel application state. Load language, theme_mode, and accent_color from SharedPreferences; apply the accent before building MaterialApp; call bridgeClient.setLanguage; reload preferences once per second; and stop the timer and dispose the panel BridgeClient on teardown.
- [ ] Step 5: Configure the panel window before showing it. Use window_manager to set minimum size 560x320, maximum size 960x420, frameless title bar, always-on-top, skip-taskbar, non-resizable, and bottom-right alignment. Show only after the native Flutter view is ready.
- [ ] Step 6: Build the panel layout. Use a rounded themed surface, draggable header, localized selected-phone title, large dashed NativeDropZone, accent drag-over state, and a horizontally scrolling phone strip at the bottom. Each phone card must show name, kind, RSSI, and selected styling. Include localized no-phone and active-transfer states.
- [ ] Step 7: Implement drop-to-send. Resolve the selected device, show a localized no-device message if none exists, call OutgoingStagingController.addPaths, show its localized error if staging fails, then call client.sendToDevice(target). Disable drops while a transfer is active and clear the panel staging selection after the transfer finishes.
- [ ] Step 8: Resize only when the device count changes. Call windowManager.setSize(Size(panelWidthForDeviceCount(count), 360)) followed by setAlignment(Alignment.bottomRight), and keep the phone strip scrollable at the maximum width.

### Task 4: Add the setting and translations

Files:
- Modify catshare_gui/lib/config/language.dart.
- Modify catshare_gui/lib/pages/settings_tab.dart.
- Modify catshare_gui/lib/pages/home_page.dart.
- Modify catshare_gui/lib/main.dart.
- Modify catshare_gui/test/language_test.dart.

Interfaces:
- Consume DesktopDropPanelService.enable/disable, SharedPreferences, and the existing Settings/Home/Main callback pattern.
- Produce persisted desktopDropTargetEnabled state and a localized Settings switch.

- [ ] Step 1: Add non-empty translations to en, zh-CN, and zh-TW for desktopDropTarget, desktopDropTargetHint, desktopDropTitle, desktopDropNoDevices, desktopDropSelectDevice, desktopDropReleaseToSend, desktopDropUnavailable, desktopDropStagingFailed, and desktopDropSendFailed. Traditional Chinese wording must be consistent with the existing app and include terms such as 桌面拖放視窗, 桌面右下角, 附近的手機, and 放開滑鼠以傳送檔案.
- [ ] Step 2: Add _desktopDropTargetEnabled = false to _CatShareAppState. Load desktop_drop_target_enabled in initState, start the panel when saved true, persist changes, call enable or disable on the service, and pass the value and callback through HomePage to SettingsTab.
- [ ] Step 3: Add a SwitchListTile to the existing System Tray section after minimize-to-tray. Use the new localized label and hint and the current light/dark accent as activeThumbColor.
- [ ] Step 4: Run from catshare_gui: ..\flutter_sdk\flutter\bin\flutter.bat test test\language_test.dart. Confirm all AppLanguage values pass.

### Task 5: Add the native --desktop-drop-panel runner mode

Files:
- Modify catshare_gui/windows/runner/main.cpp.
- Modify catshare_gui/windows/runner/flutter_window.cpp only if panel startup needs native show/hide changes.
- Modify catshare_gui/windows/runner/drag_drop_bridge.cpp only if frameless registration exposes a real limitation.

Interfaces:
- Consume GetCommandLineArguments, FlutterWindow, and DragDropBridge.
- Produce a second executable mode that skips the main mutex and starts with a panel-sized client area.

- [ ] Step 1: Parse arguments before CreateMutex. If --desktop-drop-panel is present, skip the normal single-instance activation path; retain it for normal launches.
- [ ] Step 2: Create the panel window with start_hidden || is_drop_panel, initial size 640x360, and title OsharePC Drop Target. Keep SetQuitOnClose(true). The Dart panel config will remove the frame, set topmost and skip-taskbar, align bottom-right, and show it.
- [ ] Step 3: Compile native code with dotnet build CatShareSender.csproj -c Release and flutter build windows --release --no-version-check --no-pub from catshare_gui. Confirm both projects compile.

### Task 6: Run complete verification and the requested package build

Files:
- Modify only files identified by failing verification; leave unrelated dirty files unchanged.

- [ ] Step 1: Run Push-Location catshare_gui; ..\flutter_sdk\flutter\bin\flutter.bat test; Pop-Location. Expected: zero failures.
- [ ] Step 2: Run Push-Location catshare_gui; ..\flutter_sdk\flutter\bin\flutter.bat analyze; Pop-Location. Expected: No issues found.
- [ ] Step 3: From the repository root run cmd /c "C:\Users\Anton\Downloads\windows-sender\windows-sender\build-local.cmd". Expected: exit code 0, OSharePC local build completed, artifacts\OSharePC-Portable-win-x64.zip, and artifacts\OSharePC-Setup-win-x64.exe.
- [ ] Step 4: Inspect git status --short, git diff --stat, and the sizes of both artifact files. Verify unrelated pre-existing modifications remain untouched.
- [ ] Step 5: Manually smoke-test enabling/disabling, bottom-right placement, topmost behavior, phone-strip resizing and selection, file/folder drops, active-transfer rejection, all three languages, light/dark/system themes, and all accent presets.
