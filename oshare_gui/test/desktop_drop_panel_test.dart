import 'package:flutter_test/flutter_test.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:oshare_gui/models/models.dart';
import 'package:oshare_gui/pages/desktop_drop_panel.dart';
import 'package:oshare_gui/config/language.dart';
import 'package:oshare_gui/services/desktop_drop_panel_service.dart';
import 'package:oshare_gui/services/bridge_client.dart';
import 'package:oshare_gui/services/outgoing_staging_controller.dart';
import 'package:oshare_gui/widgets/native_drop_zone.dart';

void main() {
  testWidgets('failed staged cancel keeps panel open without sending', (
    tester,
  ) async {
    final client = BridgeClient(manageBackend: false);
    final staging = _FakeStagingController(client);
    await tester.pumpWidget(
      MaterialApp(
        home: DesktopDropPanelPage(
          client: client,
          language: AppLanguage.english,
          stagingController: staging,
          manageNativeDropPanel: false,
          sendToDeviceOverride: (_) async {
            staging.sendCalls++;
            return true;
          },
        ),
      ),
    );
    final zone = tester.widget<NativeDropZone>(find.byType(NativeDropZone));
    staging.activate();
    zone.onDropped?.call(<String>[]);
    await tester.pump();
    await tester.pumpAndSettle();
    await tester.pump(const Duration(milliseconds: 250));
    expect(
      find.byKey(const ValueKey('desktop-drop-cancel-staged')),
      findsOneWidget,
    );
    await tester.tap(find.byKey(const ValueKey('desktop-drop-cancel-staged')));
    await tester.pump();
    expect(
      find.byKey(const ValueKey('desktop-drop-cancel-staged')),
      findsOneWidget,
    );
    expect(staging.clearCalls, 1);
    expect(staging.sendCalls, 0);
    client.dispose();
  });

  test('only staged files expand the panel', () {
    expect(panelDropTargetSize, const Size(560, 260));
    expect(panelExpandedSize, const Size(364, 280));
    expect(panelSizeForStage(DesktopDropPanelStage.idle), panelDropTargetSize);
    expect(
      panelSizeForStage(DesktopDropPanelStage.dragging),
      panelDropTargetSize,
    );
    expect(panelSizeForStage(DesktopDropPanelStage.staged), panelExpandedSize);
    expect(
      panelSizeForStage(DesktopDropPanelStage.staged, deviceCount: 2),
      const Size(364, 280),
    );
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

  test(
    'desktop panel native lifecycle activates and restores the hot zone',
    () async {
      const channel = MethodChannel('oshare/drag_drop');
      final messenger =
          TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
      final calls = <MethodCall>[];
      messenger.setMockMethodCallHandler(channel, (call) async {
        calls.add(call);
        return null;
      });
      addTearDown(() => messenger.setMockMethodCallHandler(channel, null));

      await DragDropService.instance.activateDesktopDropPanel();
      await DragDropService.instance.restoreDesktopDropPanel();

      expect(calls.map((call) => call.method), <String>[
        'activateDesktopDropPanel',
        'restoreDesktopDropPanel',
      ]);
    },
  );

  test('corner anchor touches the visible work area edge', () async {
    const channel = MethodChannel('dev.leanflutter.plugins/screen_retriever');
    final messenger =
        TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
    messenger.setMockMethodCallHandler(channel, (call) async {
      expect(call.method, 'getPrimaryDisplay');
      return <String, Object?>{
        'id': '0',
        'name': 'primary',
        'size': <String, double>{'width': 1920, 'height': 1080},
        'visiblePosition': <String, double>{'dx': 0, 'dy': 0},
        'visibleSize': <String, double>{'width': 1920, 'height': 1040},
        'scaleFactor': 1.0,
      };
    });
    addTearDown(() => messenger.setMockMethodCallHandler(channel, null));

    expect(await resolveCornerAnchor(), const Offset(1920, 1040));
  });

  test('corner anchor requires work area bounds', () async {
    const channel = MethodChannel('dev.leanflutter.plugins/screen_retriever');
    final messenger =
        TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
    messenger.setMockMethodCallHandler(channel, (call) async {
      return <String, Object?>{
        'id': '0',
        'name': 'primary',
        'size': <String, double>{'width': 1920, 'height': 1080},
        'visiblePosition': <String, double>{'dx': 0, 'dy': 0},
        'visibleSize': null,
        'scaleFactor': 1.0,
      };
    });
    addTearDown(() => messenger.setMockMethodCallHandler(channel, null));

    await expectLater(resolveCornerAnchor(), throwsA(isA<StateError>()));
  });

  test('panel width grows with phones but stays within bounds', () {
    expect(panelWidthForDeviceCount(0), 364);
    expect(panelWidthForDeviceCount(1), 364);
    expect(panelWidthForDeviceCount(3), 528);
    expect(panelWidthForDeviceCount(20), 528);
  });

  test('selected device falls back to the first remaining device', () {
    final first = DeviceModel(
      address: 'a',
      name: 'A',
      kind: 'Phone',
      rssi: -40,
      oShare: false,
      addressText: 'A',
    );
    final second = DeviceModel(
      address: 'b',
      name: 'B',
      kind: 'Phone',
      rssi: -45,
      oShare: false,
      addressText: 'B',
    );

    expect(resolveSelectedDevice([first, second], 'b')?.address, 'b');
    expect(resolveSelectedDevice([first], 'b'), isNull);
    expect(resolveSelectedDevice([first], null), isNull);
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
    expect(manualCancelCleanupState(wasArmed: true, cleared: false), isFalse);
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

  testWidgets(
    'drag visual feedback uses square animation style without circle ball',
    (tester) async {
      final client = BridgeClient(manageBackend: false);
      final staging = _FakeStagingController(client);
      await tester.pumpWidget(
        MaterialApp(
          home: DesktopDropPanelPage(
            client: client,
            language: AppLanguage.english,
            stagingController: staging,
          ),
        ),
      );

      final squareTargetFinder = find.byKey(
        const ValueKey('desktop-drop-square-target'),
      );
      expect(squareTargetFinder, findsOneWidget);

      final container = tester.widget<Container>(squareTargetFinder);
      final decoration = container.decoration as BoxDecoration?;
      expect(decoration, isNotNull);
      expect(decoration!.shape, isNot(BoxShape.circle));
      expect(decoration.shape, BoxShape.rectangle);
      expect(decoration.borderRadius, isNotNull);

      final renderBox = tester.renderObject<RenderBox>(squareTargetFinder);
      expect(renderBox.size.width, renderBox.size.height);
      expect(renderBox.size.width, 176.0);

      client.dispose();
    },
  );

  test('drop instructions allow a third wrapped line in the compact panel', () {
    expect(desktopDropInstructionMaxLines, 3);
  });
}

class _FakeStagingController extends OutgoingStagingController {
  _FakeStagingController(BridgeClient client) : super(bridgeClient: client);
  bool active = false;
  int clearCalls = 0;
  int sendCalls = 0;
  void activate() {
    active = true;
    notifyListeners();
  }

  @override
  List<String> get selectedFiles => const ['C:/photo.jpg'];
  @override
  int get totalCount => 1;
  @override
  int get totalBytes => 12;
  @override
  bool get hasValidStagedSelection => active;
  @override
  Future<bool> addPaths(
    List<String> paths, {
    AppLanguage language = AppLanguage.english,
  }) async {
    active = true;
    notifyListeners();
    return true;
  }

  @override
  Future<bool> clear({AppLanguage language = AppLanguage.english}) async {
    clearCalls++;
    return false;
  }
}
