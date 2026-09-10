import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';
import 'package:flutter/foundation.dart';
import 'package:http/http.dart' as http;
import 'package:path/path.dart' as p;
import 'package:shared_preferences/shared_preferences.dart';
import '../config/language.dart';
import '../models/models.dart';

class BridgeClient extends ChangeNotifier {
  static const String baseUrl = 'http://127.0.0.1:8960';
  static const String _tokenHeader = 'X-OSharePC-Bridge-Token';
  static const String _tokenEnvironment = 'OSHAREPC_BRIDGE_TOKEN';
  final String _bridgeToken = _generateBridgeToken();

  static String _generateBridgeToken() {
    final random = Random.secure();
    final bytes = List<int>.generate(32, (_) => random.nextInt(256));
    return base64UrlEncode(bytes).replaceAll('=', '');
  }

  Map<String, String> get _authHeaders => {_tokenHeader: _bridgeToken};
  Map<String, String> get _jsonHeaders => {
    ..._authHeaders,
    'Content-Type': 'application/json',
  };

  Future<http.Response> _get(String path) =>
      http.get(Uri.parse('$baseUrl$path'), headers: _authHeaders);

  Future<http.Response> _post(String path) =>
      http.post(Uri.parse('$baseUrl$path'), headers: _authHeaders);

  Future<http.Response> _postJson(String path, Object body) => http.post(
    Uri.parse('$baseUrl$path'),
    headers: _jsonHeaders,
    body: jsonEncode(body),
  );

  EngineStatus _status = EngineStatus.initial();
  List<DeviceModel> _devices = [];
  QuickSaveMode _quickSaveMode = QuickSaveMode.favorites;
  bool _receiveSuccessNotifications = true;
  AppLanguage _language = AppLanguage.english;
  TransferStateModel _transferState = TransferStateModel();
  IncomingTransferOffer? _pendingIncomingOffer;
  final Set<String> _dismissedTransferIds = {};
  int _lastEventSeq = 0;
  bool _isConnecting = true;
  Timer? _pollTimer;
  Process? _backendProcess;
  bool _backendStartInFlight = false;
  Timer? _restartTimer;
  int _restartAttempts = 0;
  DateTime? _restartWindowStarted;
  bool _disposed = false;
  DateTime? _lastProgressTime;
  int _lastProgressBytes = 0;
  void Function(String title, String message)? onNotification;

  EngineStatus get status => _status;
  List<DeviceModel> get devices => _devices;
  QuickSaveMode get quickSaveMode => _quickSaveMode;
  bool get receiveSuccessNotifications => _receiveSuccessNotifications;
  TransferStateModel get transferState => _transferState;
  IncomingTransferOffer? get pendingIncomingOffer => _pendingIncomingOffer;
  bool get isConnecting => _isConnecting;

  BridgeClient() {
    _initPreferences();
    _startSupervisor();
  }

  Future<void> _initPreferences() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      final modeIndex = prefs.getInt('quick_save_mode') ?? 1;
      _quickSaveMode = QuickSaveMode.values[modeIndex.clamp(0, 2)];
      _receiveSuccessNotifications =
          prefs.getBool('receive_success_notifications') ?? true;
      final languageIndex =
          prefs.getInt('language') ?? AppLanguage.english.index;
      _language = AppLanguage
          .values[languageIndex.clamp(0, AppLanguage.values.length - 1)];
      notifyListeners();
    } catch (e) {
      debugPrint('Error loading prefs: $e');
    }
  }

  void setLanguage(AppLanguage language) {
    _language = language;
  }

  String _localizedTransferError(String? raw, {required bool isSending}) {
    final lower = (raw ?? '').toLowerCase();
    if (lower.contains('user interrupt') || lower.contains('cancel')) {
      return appText(_language, 'remoteCancelled');
    }
    if (lower.contains('reject') || lower.contains('declin')) {
      return appText(_language, 'remoteRejected');
    }
    if (lower.contains('timeout') || lower.contains('timed out')) {
      return appText(_language, 'transferTimedOut');
    }
    if (lower.contains('disconnect') ||
        lower.contains('connection') ||
        lower.contains('closed') ||
        lower.contains('websocket')) {
      return appText(_language, 'connectionLost');
    }
    return appText(_language, isSending ? 'sendFailed' : 'receiveFailed');
  }

  Future<void> setQuickSaveMode(QuickSaveMode mode) async {
    _quickSaveMode = mode;
    notifyListeners();
    try {
      final prefs = await SharedPreferences.getInstance();
      await prefs.setInt('quick_save_mode', mode.index);
      await updateSettings(quickSaveMode: mode.index);
    } catch (e) {
      debugPrint('Error saving quick_save_mode: $e');
    }
  }

  Future<void> setReceiveSuccessNotifications(bool enabled) async {
    _receiveSuccessNotifications = enabled;
    notifyListeners();
    try {
      final prefs = await SharedPreferences.getInstance();
      await prefs.setBool('receive_success_notifications', enabled);
    } catch (e) {
      debugPrint('Error saving receive_success_notifications: $e');
    }
  }

  void _startSupervisor() {
    _pollTimer = Timer.periodic(
      const Duration(milliseconds: 1000),
      (_) => _poll(),
    );
    _poll();
  }

  Future<void> _poll() async {
    try {
      final statusResp = await _get(
        '/api/status',
      ).timeout(const Duration(milliseconds: 1500));
      if (statusResp.statusCode == 200) {
        _isConnecting = false;
        _restartAttempts = 0;
        final json = jsonDecode(statusResp.body);
        _status = EngineStatus.fromJson(json);

        // Fast-forward cursor on initial connect to avoid replaying stale events
        if (_lastEventSeq == 0 && _status.seq > 0) {
          _lastEventSeq = _status.seq;
        }

        // Sync pending transfer with server status
        if (_status.pendingTransfer != null) {
          final current = _status.pendingTransfer!;
          if (!_dismissedTransferIds.contains(current.id)) {
            if (_quickSaveMode == QuickSaveMode.on) {
              confirmReceive(current.id, true);
            } else if (_pendingIncomingOffer?.id != current.id) {
              _pendingIncomingOffer = current;
            }
          } else {
            _pendingIncomingOffer = null;
          }
        } else {
          // No active pending transfer on server
          if (_pendingIncomingOffer != null) {
            _pendingIncomingOffer = null;
          }
        }

        // Fetch devices
        final devResp = await _get(
          '/api/devices',
        ).timeout(const Duration(milliseconds: 1500));
        if (devResp.statusCode == 200) {
          final devList = jsonDecode(devResp.body) as List;
          _devices = devList.map((d) => DeviceModel.fromJson(d)).toList();
        }

        // Fetch incremental events
        final evResp = await _get(
          '/api/events?since=$_lastEventSeq',
        ).timeout(const Duration(milliseconds: 1500));
        if (evResp.statusCode == 200) {
          final events = jsonDecode(evResp.body) as List;
          _processEvents(events);
        }

        notifyListeners();
        return;
      }
    } catch (_) {
      // Server not reachable yet
      _isConnecting = true;
      notifyListeners();
      _ensureBackendRunning();
    }
  }

  void _ensureBackendRunning() {
    if (_backendProcess != null || _backendStartInFlight || _disposed) return;
    final now = DateTime.now();
    if (_restartWindowStarted == null ||
        now.difference(_restartWindowStarted!) > const Duration(minutes: 1)) {
      _restartWindowStarted = now;
      _restartAttempts = 0;
    }
    if (_restartAttempts >= 3) {
      debugPrint('Backend restart limit reached; waiting before retrying.');
      return;
    }
    _restartAttempts++;
    _backendStartInFlight = true;

    final exeDir = File(Platform.resolvedExecutable).parent.path;
    final projectRoot = Directory.current.path;
    final candidates = [
      p.join(exeDir, 'engine', 'CatShareSender.exe'),
      p.join(exeDir, 'CatShareSender.exe'),
      p.join(projectRoot, 'deploy-gui', 'engine', 'CatShareSender.exe'),
      p.join(
        projectRoot,
        'bin',
        'Release',
        'net10.0-windows10.0.19041.0',
        'win-x64',
        'publish',
        'CatShareSender.exe',
      ),
    ];

    for (final cand in candidates) {
      if (File(cand).existsSync()) {
        debugPrint('Spawning backend engine with parent PID $pid from $cand');
        _startBackendProcess(cand, ['--bridge', '--parent-pid', '$pid']);
        return;
      }
    }

    // Development fallback only. Never silently launch an arbitrary old EXE.
    final projectFile = p.join(projectRoot, 'CatShareSender.csproj');
    if (File(projectFile).existsSync()) {
      debugPrint('Starting backend via dotnet run with parent PID $pid');
      _startBackendProcess('dotnet', [
        'run',
        '--project',
        projectFile,
        '--',
        '--bridge',
        '--parent-pid',
        '$pid',
      ]);
      return;
    }
    if (_backendStartInFlight) _backendStartInFlight = false;
  }

  Future<void> _startBackendProcess(
    String executable,
    List<String> arguments,
  ) async {
    try {
      final process = await Process.start(
        executable,
        arguments,
        environment: {_tokenEnvironment: _bridgeToken},
        includeParentEnvironment: true,
      );
      if (_disposed) {
        process.kill();
        return;
      }
      _backendProcess = process;
      _backendStartInFlight = false;
      debugPrint('Backend started with pid ${process.pid}.');
      process.exitCode.then((code) {
        if (identical(_backendProcess, process)) _backendProcess = null;
        _backendStartInFlight = false;
        if (!_disposed) {
          _isConnecting = true;
          notifyListeners();
          debugPrint('Backend exited with code $code; scheduling recovery.');
          _restartTimer?.cancel();
          _restartTimer = Timer(
            const Duration(milliseconds: 700),
            _ensureBackendRunning,
          );
        }
      });
    } catch (e) {
      _backendStartInFlight = false;
      debugPrint('Failed to start backend: $e');
    }
  }

  void _processEvents(List events) {
    for (final ev in events) {
      final seq = (ev['seq'] as num?)?.toInt() ?? 0;
      if (seq > _lastEventSeq) {
        _lastEventSeq = seq;
      }

      final type = ev['type']?.toString();
      final data = ev['data'];

      if (type == 'incomingTransfer' && data is Map) {
        final offer = IncomingTransferOffer.fromJson(
          Map<String, dynamic>.from(data),
        );
        // Only accept if not dismissed and server currently reports this as active pending transfer
        if (!_dismissedTransferIds.contains(offer.id) &&
            _status.pendingTransfer?.id == offer.id) {
          if (_quickSaveMode == QuickSaveMode.on) {
            confirmReceive(offer.id, true);
          } else {
            _pendingIncomingOffer = offer;
          }
        }
      } else if (type == 'transferCancelled' || type == 'transferResolved') {
        final id = data is Map ? data['id']?.toString() : null;
        if (id != null) {
          _dismissedTransferIds.add(id);
        }
        if (_pendingIncomingOffer != null &&
            (id == null || _pendingIncomingOffer!.id == id)) {
          _pendingIncomingOffer = null;
        }
      } else if (type == 'receiveMetadata' && data is Map) {
        _transferState = TransferStateModel(
          active: true,
          isSending: false,
          targetDevice:
              data['senderName']?.toString() ?? _transferState.targetDevice,
          sentBytes: _transferState.sentBytes,
          totalBytes:
              (data['totalSize'] as num?)?.toInt() ?? _transferState.totalBytes,
          speedBytesPerSec: _transferState.speedBytesPerSec,
          statusText: _transferState.statusText.isEmpty
              ? 'Receiving files...'
              : _transferState.statusText,
          phase: 'receiving',
          fileName: data['fileName']?.toString() ?? _transferState.fileName,
          fileCount:
              (data['fileCount'] as num?)?.toInt() ?? _transferState.fileCount,
          saveDirectory: _status.saveDirectory,
        );
      } else if (type == 'receiveProgress' && data is Map) {
        final done = (data['done'] as num?)?.toInt() ?? 0;
        final total = (data['total'] as num?)?.toInt() ?? 0;
        _updateProgress(done, total, isSending: false);
      } else if (type == 'sendProgress' && data is Map) {
        final sent = (data['sent'] as num?)?.toInt() ?? 0;
        final total = (data['total'] as num?)?.toInt() ?? 0;
        _updateProgress(sent, total, isSending: true);
      } else if (type == 'sendCompleted') {
        onNotification?.call(
          'OsharePC',
          'File transfer finished successfully.',
        );
        _transferState = TransferStateModel(
          active: true,
          isSending: true,
          targetDevice: _transferState.targetDevice,
          fileName: _transferState.fileName,
          fileCount: _transferState.fileCount,
          sentBytes: _transferState.totalBytes,
          totalBytes: _transferState.totalBytes,
          phase: 'completed',
          statusText: 'Transfer complete',
        );
      } else if (type == 'receiveCompleted') {
        if (_receiveSuccessNotifications) {
          onNotification?.call(
            'OsharePC',
            'File receive finished successfully.',
          );
        }
        _transferState = TransferStateModel(
          active: true,
          isSending: false,
          targetDevice: _transferState.targetDevice,
          fileName: _transferState.fileName,
          fileCount: _transferState.fileCount,
          sentBytes: _transferState.totalBytes > 0
              ? _transferState.totalBytes
              : _transferState.sentBytes,
          totalBytes: _transferState.totalBytes,
          phase: 'completed',
          statusText: 'Transfer complete',
          saveDirectory: _status.saveDirectory,
        );
        _pendingIncomingOffer = null;
      } else if (type == 'receiveFailed' || type == 'sendFailed') {
        final isSendingFailure = type == 'sendFailed';
        final error = data is Map ? data['error']?.toString() : null;
        final localizedError = _localizedTransferError(
          error,
          isSending: isSendingFailure,
        );
        debugPrint('Backend transfer failure detail: ${error ?? '(none)'}');
        onNotification?.call('OsharePC', appText(_language, 'transferFailed'));
        _transferState = TransferStateModel(
          active: true,
          isSending: isSendingFailure,
          targetDevice: _transferState.targetDevice,
          fileName: _transferState.fileName,
          fileCount: _transferState.fileCount,
          sentBytes: _transferState.sentBytes,
          totalBytes: _transferState.totalBytes,
          phase: 'failed',
          statusText: appText(_language, 'transferFailed'),
          errorText: localizedError,
        );
        _pendingIncomingOffer = null;
      } else if (type == 'state' && data is Map) {
        final st = data['state']?.toString() ?? '';
        if (st.contains('fail') ||
            st.contains('abort') ||
            st.contains('reject') ||
            st.contains('cancel')) {
          _pendingIncomingOffer = null;
          if (st.contains('cancel') && _transferState.active) {
            _transferState = TransferStateModel(
              active: true,
              isSending: _transferState.isSending,
              targetDevice: _transferState.targetDevice,
              fileName: _transferState.fileName,
              fileCount: _transferState.fileCount,
              sentBytes: _transferState.sentBytes,
              totalBytes: _transferState.totalBytes,
              phase: 'cancelled',
              statusText: 'Transfer cancelled',
            );
          }
        }
      }
    }
  }

  void _updateProgress(int current, int total, {required bool isSending}) {
    final now = DateTime.now();
    double speed = 0;
    if (_lastProgressTime != null && _lastProgressBytes > 0) {
      final diffMs = now.difference(_lastProgressTime!).inMilliseconds;
      if (diffMs > 200) {
        speed = ((current - _lastProgressBytes) / (diffMs / 1000.0)).clamp(
          0,
          double.infinity,
        );
        _lastProgressTime = now;
        _lastProgressBytes = current;
      } else {
        speed = _transferState.speedBytesPerSec;
      }
    } else {
      _lastProgressTime = now;
      _lastProgressBytes = current;
    }

    _transferState = TransferStateModel(
      active: true,
      isSending: isSending,
      targetDevice: isSending
          ? 'Phone'
          : (_transferState.targetDevice.isEmpty
                ? 'Incoming'
                : _transferState.targetDevice),
      sentBytes: current,
      totalBytes: total > 0 ? total : _transferState.totalBytes,
      speedBytesPerSec: speed,
      statusText: isSending ? 'Sending files...' : 'Receiving files...',
      phase: isSending ? 'sending' : 'receiving',
      fileName: _transferState.fileName,
      fileCount: _transferState.fileCount,
      saveDirectory: _transferState.saveDirectory,
    );
  }

  Future<bool> setReceiveEnabled(bool enabled) async {
    try {
      final resp = await _postJson('/api/receive', {'enabled': enabled});
      if (resp.statusCode == 200) {
        _status = EngineStatus(
          connected: _status.connected,
          receiveEnabled: enabled,
          senderId: _status.senderId,
          deviceName: _status.deviceName,
          saveDirectory: _status.saveDirectory,
          lanIp: _status.lanIp,
          mac: _status.mac,
          transferPort: _status.transferPort,
          state: enabled ? 'Active' : 'Paused',
          pendingTransfer: enabled ? _status.pendingTransfer : null,
        );
        if (!enabled) {
          _pendingIncomingOffer = null;
        }
        notifyListeners();
        return true;
      }
    } catch (e) {
      debugPrint('Error setting receive enabled: $e');
    }
    return false;
  }

  Future<Map<String, dynamic>?> stageFiles(List<String> files) async {
    try {
      final resp = await _postJson('/api/stage', {'files': files});
      if (resp.statusCode == 200) {
        return jsonDecode(resp.body) as Map<String, dynamic>;
      }
    } catch (e) {
      debugPrint('Error staging files: $e');
    }
    return null;
  }

  Future<bool> sendToDevice(DeviceModel device) async {
    try {
      _transferState = TransferStateModel(
        active: true,
        isSending: true,
        targetDevice: device.name,
        statusText: 'Connecting to ${device.name}...',
      );
      notifyListeners();

      final resp = await _postJson('/api/send', {'address': device.address});
      if (resp.statusCode == 202) return true;
      final body = resp.body.isNotEmpty ? jsonDecode(resp.body) : null;
      final error = body is Map ? body['error']?.toString() : null;
      debugPrint(
        'Backend send failure detail: ${error ?? 'HTTP ${resp.statusCode}'}',
      );
      _transferState = TransferStateModel(
        active: true,
        isSending: true,
        targetDevice: device.name,
        phase: 'failed',
        statusText: appText(_language, 'transferFailed'),
        errorText: _localizedTransferError(error, isSending: true),
      );
      notifyListeners();
      return false;
    } catch (e) {
      debugPrint('Error sending to device: $e');
      _transferState = TransferStateModel(
        active: true,
        isSending: true,
        targetDevice: device.name,
        phase: 'failed',
        statusText: appText(_language, 'transferFailed'),
        errorText: appText(_language, 'sendFailed'),
      );
      notifyListeners();
      Future.delayed(const Duration(seconds: 4), () {
        _transferState = TransferStateModel();
        notifyListeners();
      });
    }
    return false;
  }

  Future<void> confirmReceive(String id, bool accept) async {
    final offer = _pendingIncomingOffer;
    _dismissedTransferIds.add(id);
    _pendingIncomingOffer = null;
    if (accept && offer != null && offer.id == id) {
      _lastProgressTime = null;
      _lastProgressBytes = 0;
      _transferState = TransferStateModel(
        active: true,
        isSending: false,
        targetDevice: offer.name,
        fileName: offer.count == '1' ? '' : '${offer.count} files',
        fileCount: int.tryParse(offer.count) ?? 1,
        totalBytes: offer.totalBytes,
        phase: 'accepted',
        statusText: 'Preparing transfer…',
        saveDirectory: _status.saveDirectory,
      );
    }
    notifyListeners();

    try {
      await _postJson('/api/confirm-receive', {'id': id, 'accept': accept});
    } catch (e) {
      debugPrint('Error confirming receive: $e');
    }
  }

  void dismissIncomingOffer() {
    if (_pendingIncomingOffer != null) {
      final id = _pendingIncomingOffer!.id;
      _dismissedTransferIds.add(id);
      _pendingIncomingOffer = null;
      notifyListeners();
      _postJson('/api/confirm-receive', {
        'id': id,
        'accept': false,
      }).catchError((_) => http.Response('', 500));
    }
  }

  Future<bool> updateSettings({
    String? saveDirectory,
    int? themeMode,
    int? quickSaveMode,
    bool? minimizeToTray,
    bool? closeToTray,
  }) async {
    try {
      final payload = <String, dynamic>{};
      if (saveDirectory != null) payload['saveDirectory'] = saveDirectory;
      if (themeMode != null) payload['themeMode'] = themeMode;
      if (quickSaveMode != null) payload['quickSaveMode'] = quickSaveMode;
      if (minimizeToTray != null) payload['minimizeToTray'] = minimizeToTray;
      if (closeToTray != null) payload['closeToTray'] = closeToTray;

      final resp = await _postJson('/api/settings', payload);
      if (resp.statusCode == 200) {
        final json = jsonDecode(resp.body);
        _status = EngineStatus(
          connected: _status.connected,
          receiveEnabled: _status.receiveEnabled,
          senderId: _status.senderId,
          deviceName: json['deviceName']?.toString() ?? _status.deviceName,
          saveDirectory:
              json['saveDirectory']?.toString() ?? _status.saveDirectory,
          lanIp: _status.lanIp,
          mac: _status.mac,
          transferPort: _status.transferPort,
          state: _status.state,
          pendingTransfer: _status.pendingTransfer,
        );
        notifyListeners();
        return true;
      }
    } catch (e) {
      debugPrint('Error updating settings: $e');
    }
    return false;
  }

  void dismissTransferModal() {
    _transferState = TransferStateModel();
    notifyListeners();
  }

  Future<bool> cancelTransfer() async {
    try {
      final resp = await _post('/api/cancel');
      return resp.statusCode == 200;
    } catch (e) {
      debugPrint('Error cancelling transfer: $e');
      return false;
    }
  }

  Future<void> shutdownBackend() async {
    _pollTimer?.cancel();
    _pendingIncomingOffer = null;
    try {
      await _post('/api/shutdown').timeout(const Duration(milliseconds: 500));
    } catch (_) {}
    try {
      _backendProcess?.kill();
    } catch (_) {}
  }

  @override
  void dispose() {
    _disposed = true;
    _pollTimer?.cancel();
    _restartTimer?.cancel();
    try {
      _backendProcess?.kill();
    } catch (_) {}
    super.dispose();
  }
}
