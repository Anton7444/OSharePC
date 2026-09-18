# Desktop Drop Panel Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (\`- [ ]\`) syntax for tracking.

**Goal:** Make the Windows desktop drop panel smooth and compact while adding staged-file details, transfer progress/ETA, and an explicit cancel path.

**Architecture:** Keep the existing companion Flutter process, \`BridgeClient\`, \`OutgoingStagingController\`, and native OLE drop bridge. Replace per-frame native window geometry/opacity calls with state-transition calls only; animate visible Flutter content locally. Add pure transfer presentation helpers and keep send/cancel requests on the existing bridge API.

**Tech Stack:** Flutter/Dart 3.12.2, \`window_manager\`, \`screen_retriever\`, existing bridge services, Flutter tests, Windows runner, and the repository \`build-local.cmd\` pipeline.

**Spec:** \`docs/superpowers/specs/2026-09-19-desktop-drop-panel-polish-design.md\`

## Global Constraints

- The invisible OLE hit target is approximately 360×150 logical pixels and remains anchored to the bottom-right work area.
- The expanded panel is approximately 390×300 logical pixels and uses a horizontally scrollable device strip instead of growing with device count.
- No \`window_manager.setBounds()\` or native opacity call may run from an animation listener or per-frame callback.
- Dropping files stages them only; selecting a phone starts the existing send request.
- Cancel-before-send clears staging and collapses the panel; transfer cancel delegates to \`BridgeClient.cancelTransfer\`.
- All new copy must exist in English, Simplified Chinese, and Traditional Chinese.
- Do not change the bridge HTTP contract, backend transfer protocol, or full Send tab behavior except for explicitly shared formatter extraction.
- Preserve unrelated dirty worktree changes; stage only current-task files.

---

### Task 1: Define the behavior with failing tests

**Files:**
- Modify: \`catshare_gui/test/desktop_drop_panel_test.dart\`
- Create: \`catshare_gui/test/transfer_presentation_test.dart\`

**Interfaces:**
- Consume the planned \`panelDropTargetSize\`, \`panelExpandedSize\`, \`panelShouldExpand\`, \`estimateRemaining\`, and \`formatTransferDuration\` APIs.
- Produce red tests for geometry, state transitions, and ETA formatting.

- [ ] **Step 1: Write the geometry/state tests first.** Assert 360×150 hit geometry, expanded dimensions below the current 560×340 panel, no expansion while merely dragging, and expansion after staging.

\`\`\`dart
test('only staged files expand the panel', () {
  expect(panelDropTargetSize, const Size(360, 150));
  expect(panelExpandedSize.width, lessThan(560));
  expect(panelExpandedSize.height, lessThan(340));
  expect(panelShouldExpand(isDragging: true, hasStagedFiles: false), isFalse);
  expect(panelShouldExpand(isDragging: false, hasStagedFiles: true), isTrue);
});
\`\`\`

- [ ] **Step 2: Write the ETA tests first.** Cover zero speed and unknown totals returning \`null\`, one-minute remaining time, and compact duration formatting.

\`\`\`dart
test('ETA is unavailable without a positive speed', () {
  expect(
    estimateRemaining(sentBytes: 50, totalBytes: 100, speedBytesPerSec: 0),
    isNull,
  );
  expect(
    estimateRemaining(sentBytes: 50, totalBytes: 100, speedBytesPerSec: 0.833333),
    const Duration(seconds: 60),
  );
  expect(formatTransferDuration(const Duration(seconds: 84)), '1m 24s');
});
\`\`\`

- [ ] **Step 3: Run focused tests to verify the expected red failure.**

\`\`\`powershell
Push-Location catshare_gui
..\\flutter_sdk\\flutter\\bin\\flutter.bat test test\\desktop_drop_panel_test.dart test\\transfer_presentation_test.dart
Pop-Location
\`\`\`

Expected: failure because the new constants/helpers are not defined, not a test syntax error.

- [ ] **Step 4: Commit only the failing tests.**

\`\`\`powershell
git add catshare_gui/test/desktop_drop_panel_test.dart catshare_gui/test/transfer_presentation_test.dart
git commit -m "test: define desktop drop panel polish behavior"
\`\`\`

### Task 2: Implement pure geometry and transfer presentation helpers

**Files:**
- Modify: \`catshare_gui/lib/pages/desktop_drop_panel.dart\`
- Create: \`catshare_gui/lib/services/transfer_presentation.dart\`
- Test: \`catshare_gui/test/desktop_drop_panel_test.dart\`, \`catshare_gui/test/transfer_presentation_test.dart\`

**Interfaces:**
- Produce \`const Size panelDropTargetSize\`, \`const Size panelExpandedSize\`, \`Size panelSizeForStage(DesktopDropPanelStage)\`, and \`bool panelShouldExpand({required bool isDragging, required bool hasStagedFiles})\`.
- Produce \`Duration? estimateRemaining({required int sentBytes, required int totalBytes, required double speedBytesPerSec})\`, \`String formatTransferDuration(Duration)\`, \`String formatByteSize(int)\`, and \`String fileNameForPath(String)\`.

- [ ] **Step 1: Add the minimal geometry mapping.** Keep \`panelWidthForDeviceCount\` for compatibility, but use fixed 360×150 and 390×300 states so devices scroll instead of widening the native window.

\`\`\`dart
const panelDropTargetSize = Size(360, 150);
const panelExpandedSize = Size(390, 300);

Size panelSizeForStage(DesktopDropPanelStage stage) => switch (stage) {
  DesktopDropPanelStage.idle => panelDropTargetSize,
  DesktopDropPanelStage.dragging => panelDropTargetSize,
  DesktopDropPanelStage.staged => panelExpandedSize,
};
\`\`\`

- [ ] **Step 2: Add formatters.** Return \`null\` for non-positive speed or unknown totals, clamp remaining bytes at zero, and format durations as seconds, minutes plus seconds, or hours plus minutes.

- [ ] **Step 3: Run the focused tests and verify green.**

\`\`\`powershell
Push-Location catshare_gui
..\\flutter_sdk\\flutter\\bin\\flutter.bat test test\\desktop_drop_panel_test.dart test\\transfer_presentation_test.dart
Pop-Location
\`\`\`

- [ ] **Step 4: Commit the helpers.**

\`\`\`powershell
git add catshare_gui/lib/pages/desktop_drop_panel.dart catshare_gui/lib/services/transfer_presentation.dart catshare_gui/test/desktop_drop_panel_test.dart catshare_gui/test/transfer_presentation_test.dart
git commit -m "feat: add drop panel geometry and transfer formatters"
\`\`\`

### Task 3: Remove per-frame native window work

**Files:**
- Modify: \`catshare_gui/lib/pages/desktop_drop_panel.dart\`
- Modify: \`catshare_gui/lib/main.dart\`
- Test: \`catshare_gui/test/desktop_drop_panel_test.dart\`

**Interfaces:**
- Consume Task 2 geometry constants and existing \`resolveCornerAnchor\`/\`anchoredRect\` helpers.
- Produce state-transition-only native geometry/opacity and Flutter-only visual animation.

- [ ] **Step 1: Add a regression test for the stage policy.** Assert that drag hover does not enter the staged state and that cancel/terminal transfer returns to idle.

- [ ] **Step 2: Remove \`setBounds\` and \`setOpacity\` from animation listeners.** Keep one Flutter controller for cross-fading the pill, compact panel, and expanded content; no listener may call a native window method.

- [ ] **Step 3: Add a coalesced native stage transition.** Set the native window to 360×150 at initialization/collapse and 390×300 after successful staging; reassert topmost after each call. Use a generation counter so stale collapse requests cannot overwrite a newer staged state.

\`\`\`dart
Future<void> _setNativePanelStage(DesktopDropPanelStage stage) async {
  final generation = ++_geometryGeneration;
  await windowManager.setBounds(
    anchoredRect(_anchor, panelSizeForStage(stage)),
  );
  if (generation == _geometryGeneration) {
    await windowManager.setAlwaysOnTop(true);
  }
}
\`\`\`

- [ ] **Step 4: Make opacity a transition call.** Call \`setOpacity(1.0)\` once when drag enters and \`setOpacity(panelInvisibleOpacity)\` once after collapse; animate only Flutter \`Opacity\`/border/content widgets.

- [ ] **Step 5: Change \`main.dart\` initial/minimum geometry to 360×150.** Retain frameless, topmost, skip-taskbar, non-resizable behavior and the bottom-right anchor.

- [ ] **Step 6: Run focused tests, analyzer, and debug Windows build.**

\`\`\`powershell
Push-Location catshare_gui
..\\flutter_sdk\\flutter\\bin\\flutter.bat test test\\desktop_drop_panel_test.dart test\\transfer_presentation_test.dart
..\\flutter_sdk\\flutter\\bin\\flutter.bat analyze
..\\flutter_sdk\\flutter\\bin\\flutter.bat build windows --debug
Pop-Location
\`\`\`

Expected: focused tests pass, analyzer reports no issues, and the debug executable builds.

- [ ] **Step 7: Commit the performance/geometry change.**

\`\`\`powershell
git add catshare_gui/lib/pages/desktop_drop_panel.dart catshare_gui/lib/main.dart catshare_gui/test/desktop_drop_panel_test.dart
git commit -m "perf: keep drop panel animation off native window calls"
\`\`\`

### Task 4: Add staged-file summary and Cancel-before-send

**Files:**
- Modify: \`catshare_gui/lib/pages/desktop_drop_panel.dart\`
- Modify: \`catshare_gui/lib/config/language.dart\`
- Test: \`catshare_gui/test/desktop_drop_panel_test.dart\`, \`catshare_gui/test/language_test.dart\`

**Interfaces:**
- Consume \`OutgoingStagingController.selectedFiles\`, \`totalCount\`, \`totalBytes\`, and \`clear\`.
- Produce a staged summary card and \`ValueKey('desktop-drop-cancel-staged')\` cancel button.

- [ ] **Step 1: Add failing tests for file names and cancel policy.** Verify \`fileNameForPath\` returns the final path component and the staged state can cancel without invoking \`sendToDevice\`.

- [ ] **Step 2: Render the staged summary.** Show the first file name, file count, and total size above the phone strip; show the compact drop prompt only while hovering.

- [ ] **Step 3: Implement \`_cancelStagedDrop()\`.** Guard active transfers, call \`_clearStagedFiles\`, clear \`_selectedAddress\`, and collapse only after the bridge confirms staging was cleared. Keep the panel open and show the staging error if clear fails.

- [ ] **Step 4: Keep phone selection as the only send trigger.** \`_handleDroppedPaths\` stages and expands; the phone card calls \`_sendStagedFilesTo\`; no drop callback calls \`sendToDevice\`.

- [ ] **Step 5: Add localized staged-file and cancel labels** to English, Simplified Chinese, and Traditional Chinese tables.

- [ ] **Step 6: Run focused tests and analyzer.**

\`\`\`powershell
Push-Location catshare_gui
..\\flutter_sdk\\flutter\\bin\\flutter.bat test test\\desktop_drop_panel_test.dart test\\language_test.dart test\\transfer_presentation_test.dart
..\\flutter_sdk\\flutter\\bin\\flutter.bat analyze
Pop-Location
\`\`\`

- [ ] **Step 7: Commit the staged summary/cancel flow.**

\`\`\`powershell
git add catshare_gui/lib/pages/desktop_drop_panel.dart catshare_gui/lib/config/language.dart catshare_gui/test/desktop_drop_panel_test.dart catshare_gui/test/language_test.dart
git commit -m "feat: add staged file summary and cancel action"
\`\`\`

### Task 5: Add transfer progress, speed, ETA, and transfer cancellation

**Files:**
- Modify: \`catshare_gui/lib/pages/desktop_drop_panel.dart\`
- Modify: \`catshare_gui/lib/services/transfer_presentation.dart\`
- Modify: \`catshare_gui/lib/config/language.dart\`
- Test: \`catshare_gui/test/transfer_presentation_test.dart\`

**Interfaces:**
- Consume \`TransferStateModel.progress\`, \`sentBytes\`, \`totalBytes\`, \`speedBytesPerSec\`, \`phase\`, and \`statusText\`.
- Produce an upper transfer card with progress, percentage, transferred/total bytes, speed, ETA, and a cancel button wired to \`BridgeClient.cancelTransfer\`.

- [ ] **Step 1: Add failing ETA edge-case tests.** Cover completed/overrun transfers returning zero remaining time, unknown totals rendering no determinate ETA, and durations over one hour.

- [ ] **Step 2: Render \`_buildTransferDetails\`.** Use a determinate bar when total bytes are positive, an indeterminate bar otherwise, an em dash when ETA is unavailable, and the staged summary above the progress row.

- [ ] **Step 3: Use existing \`ListenableBuilder\` updates.** Do not add a polling loop or per-frame timer; progress updates must not call native geometry or opacity methods.

- [ ] **Step 4: Wire transfer cancellation and cleanup.** Active phases call \`widget.client.cancelTransfer\`; terminal phases clear staging once and collapse after a successful clear.

- [ ] **Step 5: Run all Flutter tests and analyzer.**

\`\`\`powershell
Push-Location catshare_gui
..\\flutter_sdk\\flutter\\bin\\flutter.bat test
..\\flutter_sdk\\flutter\\bin\\flutter.bat analyze
Pop-Location
\`\`\`

Expected: zero test failures and \`No issues found!\`.

- [ ] **Step 6: Commit transfer details.**

\`\`\`powershell
git add catshare_gui/lib/pages/desktop_drop_panel.dart catshare_gui/lib/services/transfer_presentation.dart catshare_gui/lib/config/language.dart catshare_gui/test/transfer_presentation_test.dart
git commit -m "feat: show desktop transfer progress and ETA"
\`\`\`

### Task 6: Full verification and package build

**Files:**
- Verify: all files changed by Tasks 1–5
- Do not stage: unrelated pre-existing dirty files

- [ ] **Step 1: Run the complete Flutter test suite and analyzer.**

\`\`\`powershell
Push-Location catshare_gui
..\\flutter_sdk\\flutter\\bin\\flutter.bat test
..\\flutter_sdk\\flutter\\bin\\flutter.bat analyze
Pop-Location
\`\`\`

- [ ] **Step 2: Build the Windows debug executable.**

\`\`\`powershell
Push-Location catshare_gui
..\\flutter_sdk\\flutter\\bin\\flutter.bat build windows --debug
Pop-Location
\`\`\`

- [ ] **Step 3: Run the requested package build.**

\`\`\`powershell
& 'C:\\Users\\Anton\\Downloads\\windows-sender\\windows-sender\\build-local.cmd'
\`\`\`

Expected: exit code 0, \`OSharePC local build completed\`, and both portable ZIP and Setup EXE under \`artifacts\`.

- [ ] **Step 4: Inspect final status and artifact sizes.**

\`\`\`powershell
git status --short
git diff --stat HEAD~5..HEAD
$portable = Get-Item artifacts\\OSharePC-Portable-win-x64.zip
$installer = Get-Item artifacts\\OSharePC-Setup-win-x64.exe
"$($portable.FullName) $($portable.Length) bytes"
"$($installer.FullName) $($installer.Length) bytes"
\`\`\`

- [ ] **Step 5: Perform the manual smoke checklist.** Verify the 360×150 hit area, border-only hover, compact 390×300 expansion, staged summary, Cancel-before-send, phone selection, progress/speed/ETA, transfer cancel, terminal cleanup, no-device state, all three languages, both themes, and accent colors.

- [ ] **Step 6: Commit any verification-only fix separately, then rerun the complete verification commands before reporting completion.**
