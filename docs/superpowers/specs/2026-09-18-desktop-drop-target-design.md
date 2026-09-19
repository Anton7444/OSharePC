# Desktop Drop Target Design

**Date:** 2026-09-18

**Status:** Approved in conversation

## Goal

Add an optional Windows desktop drop target that appears as a compact floating panel in the bottom-right work area. The panel lets a user choose one of the phones currently discovered by OSharePC, drag files onto the panel, and send those files to the selected phone without opening the full Send tab.

## User experience

- The feature is disabled by default so existing installations keep their current behavior.
- The Settings page contains a localized enable/disable switch named “Desktop drop target”.
- When enabled, the main GUI launches a separate frameless companion process. The panel is always on top, skips the taskbar, is anchored to the bottom-right of the Windows work area, and has no separate tray icon.
- The panel has three visual regions:
  1. A header that says files can be dragged here and names the selected phone.
  2. A large dashed drop surface that changes to the accent color while files are dragged over it.
  3. A horizontal phone strip at the bottom containing the currently discovered phones.
- The panel expands horizontally as more phones are discovered, within a bounded minimum and maximum width. If the device count exceeds the available width, the phone strip scrolls horizontally.
- Clicking a phone card makes it the target. The first discovered phone is selected automatically when no target exists; the selection falls back to the first remaining phone if the selected phone disappears.
- Dropping files or folders stages them using the existing `OutgoingStagingController`, then immediately invokes the existing `BridgeClient.sendToDevice` endpoint for the selected phone.
- When there are no phones, the drop surface remains visible but explains that a phone must be nearby. When a transfer is active, dropping is disabled and the panel shows the existing localized “drag and drop is unavailable during transfer” message.
- Disabling the setting terminates the companion process. Re-enabling starts a fresh panel. Closing the main app also terminates the companion process.

## Architecture

### Companion process

The current Flutter Windows executable is started a second time with `--desktop-drop-panel`. The native runner bypasses the single-instance mutex only for this flag and creates a normal Flutter window with an initial panel-sized client area. The Dart entrypoint detects the flag and runs the panel app instead of initializing the full app/tray UI.

The main process passes its generated bridge token to the child process through `OSHAREPC_BRIDGE_TOKEN`. The panel constructs `BridgeClient` with that token and `manageBackend: false`; therefore it can poll the existing bridge and issue stage/send requests but never starts another backend or tray service.

The panel process owns its own native OLE drop target through the existing `DragDropBridge`, so drag-and-drop continues to work when the full application is hidden to the tray.

### State and persistence

- `SharedPreferences` key `desktop_drop_target_enabled` stores the setting and defaults to `false`.
- The existing shared preferences for `language`, `theme_mode`, and `accent_color` remain the source of truth. The panel reloads them once per second so changes made in the main Settings page appear without restarting the panel.
- `BridgeClient` accepts an optional token and a backend-supervision flag. The existing main process keeps the current generated-token behavior.
- A new `DesktopDropPanelService` owns child-process start/stop behavior in the main process and cleans up the child during application exit.

### Visual system

The panel uses `AppTheme`, `AppColors`, and `AccentPreset` from the existing Flutter app. It must not introduce fixed accent hex values. The panel uses the current light/dark `ThemeMode`, selected accent preset, `darkAccent*`/`lightAccent*` tiers, and the existing typography.

All user-facing panel strings are added to the existing `appText` tables for `en`, `zh-CN`, and `zh-TW`:

- panel title and selected-device wording;
- empty-device prompt;
- drag-over/release wording;
- disabled-during-transfer wording;
- staging/send errors;
- the Settings label and hint.

## Error handling

- If the companion executable cannot be started, the main process logs the failure and leaves the persisted setting enabled so the next normal launch can retry; the Settings switch reports the failed runtime state only through the existing debug log.
- If the bridge is unavailable, the panel shows the existing connecting state and does not accept a send until a device list is available.
- Empty drops, duplicate files, directory expansion, and staging failures use the existing `OutgoingStagingController` behavior and localized error strings.
- If the selected phone disappears, the panel selects the first remaining phone. If none remain, it clears the selection and disables sending.
- The panel process uses the existing transfer state to prevent concurrent sends.

## Verification requirements

- Dart unit/widget tests cover panel width bounds, target selection fallback, and all new localization keys.
- The Flutter analyzer and test suite run cleanly.
- The native Windows runner compiles with the panel argument path and the existing drag-drop bridge.
- `build-local.cmd` completes all existing package stages and produces both the portable ZIP and Setup EXE.
- Manual smoke verification checks enabling/disabling, bottom-right placement, topmost behavior, device strip updates, file/folder drops, active-transfer rejection, all three languages, both theme modes, and accent-color changes.
