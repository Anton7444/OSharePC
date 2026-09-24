import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';

import 'bridge_client.dart';

const desktopDropPanelArgument = '--desktop-drop-panel';
const desktopDropPanelEnvironment = 'OSHAREPC_DESKTOP_DROP_PANEL';

bool isDesktopDropPanelProcess({
  required Iterable<String> arguments,
  required String? environmentValue,
}) {
  return environmentValue == '1' ||
      arguments.contains(desktopDropPanelArgument);
}

class DesktopDropPanelService {
  static const _tokenEnvironment = 'OSHAREPC_BRIDGE_TOKEN';

  final BridgeClient bridgeClient;
  Process? _process;
  Future<void> _lifecycle = Future<void>.value();

  DesktopDropPanelService({required this.bridgeClient});

  bool get isRunning => _process != null;

  Future<bool> enable() {
    final operation = _lifecycle.then<bool>((_) => _enableInternal());
    _lifecycle = operation.then<void>(
      (_) {},
      onError: (Object error, StackTrace stackTrace) {
        debugPrint('[DesktopDropPanelService] Lifecycle error: $error');
      },
    );
    return operation;
  }

  Future<bool> _enableInternal() async {
    if (_process != null) return true;

    try {
      final process = await Process.start(
        Platform.resolvedExecutable,
        const [desktopDropPanelArgument],
        environment: {
          _tokenEnvironment: bridgeClient.bridgeToken,
          desktopDropPanelEnvironment: '1',
        },
        includeParentEnvironment: true,
      );
      unawaited(process.stdout.drain<void>());
      unawaited(process.stderr.drain<void>());
      _process = process;
      unawaited(
        process.exitCode.then((_) {
          if (identical(_process, process)) {
            _process = null;
          }
        }),
      );
      return true;
    } catch (error) {
      debugPrint('[DesktopDropPanelService] Failed to start panel: $error');
      return false;
    }
  }

  Future<void> disable() {
    final operation = _lifecycle.then<void>((_) => _disableInternal());
    _lifecycle = operation.then<void>(
      (_) {},
      onError: (Object error, StackTrace stackTrace) {
        debugPrint('[DesktopDropPanelService] Lifecycle error: $error');
      },
    );
    return operation;
  }

  Future<void> _disableInternal() async {
    final process = _process;
    if (process == null) return;

    var exited = false;
    try {
      process.kill();
      var timedOut = false;
      await process.exitCode.timeout(
        const Duration(seconds: 2),
        onTimeout: () {
          timedOut = true;
          return -1;
        },
      );
      exited = !timedOut;
    } catch (error) {
      debugPrint('[DesktopDropPanelService] Failed to stop panel: $error');
    } finally {
      if (exited && identical(_process, process)) {
        _process = null;
      }
    }
  }

  Future<void> dispose() => disable();
}
