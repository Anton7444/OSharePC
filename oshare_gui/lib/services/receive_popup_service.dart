import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../models/models.dart';
import 'bridge_client.dart';

const receivePopupEnabledPref = 'receive_popup_enabled';

const receivePopupArgument = '--receive-popup';
const receivePopupEnvironment = 'OSHAREPC_RECEIVE_POPUP';

bool isReceivePopupProcess({
  required Iterable<String> arguments,
  required String? environmentValue,
}) {
  return (environmentValue != null && environmentValue.isNotEmpty) ||
      arguments.contains(receivePopupArgument);
}

enum ReceivePopupMode { offer, completed }

/// What the popup process shows. Serialized as JSON into
/// [receivePopupEnvironment] because Flutter does not reliably expose runner
/// arguments to Dart.
class ReceivePopupRequest {
  final ReceivePopupMode mode;
  final String id;
  final String senderName;
  final int fileCount;
  final int totalBytes;
  final String saveDirectory;

  const ReceivePopupRequest({
    required this.mode,
    this.id = '',
    required this.senderName,
    required this.fileCount,
    this.totalBytes = 0,
    this.saveDirectory = '',
  });

  Map<String, dynamic> toJson() => {
    'mode': mode.name,
    'id': id,
    'senderName': senderName,
    'fileCount': fileCount,
    'totalBytes': totalBytes,
    'saveDirectory': saveDirectory,
  };

  static ReceivePopupRequest? fromEnvironment() {
    try {
      final raw = Platform.environment[receivePopupEnvironment];
      if (raw == null || raw.isEmpty) return null;
      final json = jsonDecode(raw) as Map<String, dynamic>;
      return ReceivePopupRequest(
        mode: ReceivePopupMode.values.byName(json['mode'] as String),
        id: json['id']?.toString() ?? '',
        senderName: json['senderName']?.toString() ?? '',
        fileCount: (json['fileCount'] as num?)?.toInt() ?? 1,
        totalBytes: (json['totalBytes'] as num?)?.toInt() ?? 0,
        saveDirectory: json['saveDirectory']?.toString() ?? '',
      );
    } catch (error) {
      debugPrint('[ReceivePopup] Invalid request: $error');
      return null;
    }
  }
}

/// Shows the corner receive popup while the main window is minimized or
/// hidden: an Accept/Decline prompt for offers that Quick Save did not
/// auto-accept, and a completion card once the files have been received.
class ReceivePopupService {
  static const _tokenEnvironment = 'OSHAREPC_BRIDGE_TOKEN';

  final BridgeClient bridgeClient;
  Process? _process;
  ReceivePopupMode? _processMode;
  String? _offerId;
  Future<void> _lifecycle = Future<void>.value();

  ReceivePopupService({required this.bridgeClient}) {
    bridgeClient.addListener(_onClientChanged);
    bridgeClient.onReceiveCompleted = _onReceiveCompleted;
  }

  void _onClientChanged() {
    final offer = bridgeClient.pendingIncomingOffer;
    final hidden = !bridgeClient.mainWindowVisible;

    if (offer != null && hidden && offer.id != _offerId) {
      _offerId = offer.id;
      _enqueue(() => _launch(_offerRequest(offer)));
      return;
    }

    // The offer was answered elsewhere, expired, or the main window is back
    // and shows its own modal: the prompt is no longer needed.
    final offerGone = offer == null || offer.id != _offerId;
    if (_processMode == ReceivePopupMode.offer && (offerGone || !hidden)) {
      if (!hidden && !offerGone) _offerId = null;
      _enqueue(_close);
    }
  }

  void _onReceiveCompleted(TransferStateModel transfer) {
    if (bridgeClient.mainWindowVisible) return;
    _enqueue(
      () => _launch(
        ReceivePopupRequest(
          mode: ReceivePopupMode.completed,
          senderName: transfer.targetDevice,
          fileCount: transfer.fileCount,
          totalBytes: transfer.totalBytes,
          saveDirectory: transfer.saveDirectory.isNotEmpty
              ? transfer.saveDirectory
              : bridgeClient.status.saveDirectory,
        ),
      ),
    );
  }

  ReceivePopupRequest _offerRequest(IncomingTransferOffer offer) =>
      ReceivePopupRequest(
        mode: ReceivePopupMode.offer,
        id: offer.id,
        senderName: offer.name,
        fileCount: int.tryParse(offer.count) ?? 1,
        totalBytes: offer.totalBytes,
      );

  void _enqueue(Future<void> Function() operation) {
    _lifecycle = _lifecycle.then((_) => operation()).catchError((Object error) {
      debugPrint('[ReceivePopupService] Lifecycle error: $error');
    });
  }

  Future<bool> _isEnabled() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      return prefs.getBool(receivePopupEnabledPref) ?? true;
    } catch (_) {
      return true;
    }
  }

  Future<void> _launch(ReceivePopupRequest request) async {
    await _close();
    if (!await _isEnabled()) return;
    try {
      final process = await Process.start(
        Platform.resolvedExecutable,
        const [receivePopupArgument],
        environment: {
          _tokenEnvironment: bridgeClient.bridgeToken,
          receivePopupEnvironment: jsonEncode(request.toJson()),
        },
        includeParentEnvironment: true,
      );
      unawaited(process.stdout.drain<void>());
      unawaited(process.stderr.drain<void>());
      _process = process;
      _processMode = request.mode;
      // The offer may have been resolved, or the main window restored, while
      // the process was starting; _onClientChanged could not close it then.
      if (request.mode == ReceivePopupMode.offer &&
          (bridgeClient.pendingIncomingOffer?.id != request.id ||
              bridgeClient.mainWindowVisible)) {
        await _close();
        return;
      }
      unawaited(
        process.exitCode.then((_) {
          if (identical(_process, process)) {
            _process = null;
            _processMode = null;
          }
        }),
      );
    } catch (error) {
      debugPrint('[ReceivePopupService] Failed to start popup: $error');
    }
  }

  Future<void> _close() async {
    final process = _process;
    if (process == null) return;
    _process = null;
    _processMode = null;
    try {
      process.kill();
      await process.exitCode.timeout(
        const Duration(seconds: 2),
        onTimeout: () => -1,
      );
    } catch (error) {
      debugPrint('[ReceivePopupService] Failed to stop popup: $error');
    }
  }

  Future<void> dispose() {
    bridgeClient.removeListener(_onClientChanged);
    bridgeClient.onReceiveCompleted = null;
    final done = _lifecycle.then((_) => _close());
    _lifecycle = done;
    return done;
  }
}
