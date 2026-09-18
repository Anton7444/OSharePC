import 'package:flutter_test/flutter_test.dart';
import 'package:flutter/material.dart';
import 'package:catshare_gui/models/models.dart';
import 'package:catshare_gui/pages/desktop_drop_panel.dart';
import 'package:catshare_gui/services/desktop_drop_panel_service.dart';

void main() {
  test('only staged files expand the panel', () {
    expect(panelDropTargetSize, const Size(360, 150));
    expect(panelExpandedSize, const Size(390, 300));
    expect(panelSizeForStage(DesktopDropPanelStage.idle), panelDropTargetSize);
    expect(
      panelSizeForStage(DesktopDropPanelStage.dragging),
      panelDropTargetSize,
    );
    expect(panelSizeForStage(DesktopDropPanelStage.staged), panelExpandedSize);
    expect(panelShouldExpand(isDragging: true, hasStagedFiles: false), isFalse);
    expect(panelShouldExpand(isDragging: false, hasStagedFiles: true), isTrue);
  });

  test('stage policy keeps hover compact and terminal states idle', () {
    expect(
      panelStageForState(isDragging: true, hasStagedFiles: false),
      DesktopDropPanelStage.dragging,
    );
    expect(
      panelStageForState(isDragging: true, hasStagedFiles: false),
      isNot(DesktopDropPanelStage.staged),
    );
    expect(
      panelStageForState(isDragging: false, hasStagedFiles: false),
      DesktopDropPanelStage.idle,
    );
    expect(
      panelStageForState(
        isDragging: false,
        hasStagedFiles: true,
        terminal: true,
      ),
      DesktopDropPanelStage.idle,
    );
  });

  test('panel width grows with phones but stays within bounds', () {
    expect(panelWidthForDeviceCount(0), 560);
    expect(panelWidthForDeviceCount(2), greaterThan(560));
    expect(panelWidthForDeviceCount(20), 960);
  });

  test('selected device falls back to the first remaining device', () {
    final first = DeviceModel(
      address: 'a',
      name: 'A',
      kind: 'Phone',
      rssi: -40,
      catShare: false,
      addressText: 'A',
    );
    final second = DeviceModel(
      address: 'b',
      name: 'B',
      kind: 'Phone',
      rssi: -45,
      catShare: false,
      addressText: 'B',
    );

    expect(resolveSelectedDevice([first, second], 'b')?.address, 'b');
    expect(resolveSelectedDevice([first], 'b')?.address, 'a');
    expect(resolveSelectedDevice([], 'b'), isNull);
  });

  test('fileNameForPath returns the final path component', () {
    expect(fileNameForPath(r'C:\Users\Anton\Downloads\photo.jpg'), 'photo.jpg');
    expect(fileNameForPath('/tmp/archive.zip'), 'archive.zip');
  });

  test('cancel policy never arms transfer cleanup', () {
    // Manual cancellation must remain a local staging operation. In
    // particular, a failed clear must not trigger another clear/collapse from
    // terminal transfer-state handling, and it must never send a device.
    expect(manualCancelArmsTransferCleanup, isFalse);
  });

  test(
    'panel mode is detected from the environment when Dart loses runner args',
    () {
      expect(
        isDesktopDropPanelProcess(arguments: const [], environmentValue: '1'),
        isTrue,
      );
      expect(
        isDesktopDropPanelProcess(arguments: const [], environmentValue: null),
        isFalse,
      );
      expect(
        isDesktopDropPanelProcess(
          arguments: const ['--desktop-drop-panel'],
          environmentValue: null,
        ),
        isTrue,
      );
    },
  );
}
