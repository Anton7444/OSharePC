# Desktop Drop Panel Performance and Transfer UX Design

**Date:** 2026-09-19

**Status:** Draft for review

## Goal

Improve the optional Windows desktop drop panel so that its drag interaction is smooth, its hit area matches the marked bottom-right region, its expanded window is compact, and the dropped-file state communicates enough information to make an informed send or cancel decision.

## User experience

- The invisible, always-present OLE hit target is a compact horizontal rectangle of approximately 360×150 logical pixels in the bottom-right work area, matching the user's marked region.
- Entering the hit target reveals the compact panel and thickens/highlights the dashed border. Hovering does not open the device picker or force a send.
- Dropping files stages them and opens a smaller expanded panel (approximately 390×300 logical pixels). The expanded panel keeps the bottom-right anchor and has a horizontally scrollable phone strip.
- The upper content changes from the drop prompt to a staged-file summary showing a representative file name, file count, and total size. If multiple files are staged, the summary indicates the count and keeps the list compact enough for the floating window.
- Before a phone is selected, a clearly labelled Cancel button clears the staged selection and returns the panel to its compact hidden state. Staging a file must never imply that it will be sent automatically.
- Selecting a phone starts the existing send request. During the transfer, the same upper content displays the file summary, determinate progress when available, percentage, transferred/total bytes, current speed, and estimated remaining time. A transfer Cancel button invokes the existing bridge cancellation endpoint.
- On completed, failed, or cancelled transfers, the existing localized status/error handling remains visible long enough to be useful, staged files are cleared, and the panel returns to its compact state.
- All new labels and status text are localized in English, Simplified Chinese, and Traditional Chinese.

## Architecture and performance

### Native window geometry

The current implementation calls `window_manager.setBounds()` from an animation listener on every frame and calls `setOpacity()` for every fade frame. The Windows plugin forwards those calls through a method channel to `SetWindowPos` and `SetLayeredWindowAttributes`, which is the root cause of the reported low FPS and discontinuous animation.

The panel will instead use three state geometries:

1. `dropTargetSize` (~360×150): the invisible hit area and compact visible panel.
2. `expandedSize` (~390×300): the staged-file/device-picker/transfer panel.
3. The existing bottom-right anchor, recalculated only when the panel is initialized or the display changes.

`setBounds()` and native opacity changes will occur only on state transitions (enter, staged, collapse), never on animation ticks. Flutter-side controllers will animate opacity, border emphasis, content cross-fades, and progress rendering inside the already-sized native window. This keeps expensive native window calls off the frame path.

The expanded height and width are intentionally smaller than the current 560–960×340 geometry. Device cards remain horizontally scrollable instead of widening the native window for every discovered phone.

### State model

The panel state is derived from four independent values:

- drag hover (`_isDragging`);
- staged files (`_hasStagedDrop` and `OutgoingStagingController`);
- selected phone (`_selectedAddress`);
- transfer state (`BridgeClient.transferState`).

The state transitions are:

```text
hidden hit target → drag hover → compact panel
compact panel + dropped files → expanded staged panel
expanded staged panel + Cancel → clear staging → hidden hit target
expanded staged panel + phone → transfer panel
transfer terminal state → clear staging → hidden hit target
```

No transition from drag hover directly starts a transfer.

## File summary and transfer details

`OutgoingStagingController` remains the source of truth for staged paths, file count, and total staged bytes. The panel derives display names from the normalized staged paths and reuses the existing size formatting used by `SendTab`.

`TransferStateModel` already exposes `sentBytes`, `totalBytes`, `speedBytesPerSec`, `phase`, `fileName`, and `fileCount`. A small pure helper will calculate remaining duration only when total bytes and positive speed are available; otherwise the UI shows an em dash or an indeterminate progress state. The helper will be unit-tested independently from the window/plugin layer.

The transfer card will update through the existing `BridgeClient` `ChangeNotifier`. No additional polling loop or per-frame timer will be introduced.

## Error handling

- Empty or invalid drops keep the compact panel visible long enough to show the existing localized staging error, then collapse.
- Staging errors do not start a transfer and do not leave stale staged files marked as sendable.
- Cancel-before-send calls `OutgoingStagingController.clear`. If clearing fails, the panel stays expanded and shows the localized staging error so the user can retry.
- A send failure uses the existing localized bridge error and clears staging after the failure path has been displayed.
- The transfer Cancel button delegates to `BridgeClient.cancelTransfer`; terminal bridge events drive cleanup and collapse.
- Device disappearance clears the visual selection and leaves the staged panel open until the user selects another phone or cancels.

## Testing and verification

- Add pure tests for the 360×150 hit-target geometry, compact/expanded geometry, panel state transition policy, file-summary formatting, and ETA calculation.
- Add widget tests for the staged panel's Cancel action and the transfer card's progress/speed/ETA content where the existing Flutter test harness permits it without starting a native window.
- Preserve and rerun existing localization, staging, device selection, and panel process tests.
- Run `flutter test`, `flutter analyze`, and `flutter build windows --debug` from `catshare_gui`.
- Run the repository `build-local.cmd` as the final package verification and confirm both portable ZIP and Setup EXE are produced.

## Scope boundaries

- Do not change the bridge HTTP contract or the backend transfer protocol.
- Do not change the full Send tab's behavior except where a shared pure formatter/helper is extracted for consistency.
- Do not add a second device discovery or transfer polling mechanism.
