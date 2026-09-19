import 'dart:io';
import 'package:flutter/foundation.dart';
import 'package:path/path.dart' as p;
import '../config/language.dart';
import 'bridge_client.dart';

class OutgoingStagingController extends ChangeNotifier {
  final BridgeClient bridgeClient;

  final List<String> _selectedFiles = [];
  Map<String, dynamic>? _stagedResult;
  String? _stagingError;
  bool _isStaging = false;
  int _requestSeq = 0;

  OutgoingStagingController({required this.bridgeClient});

  List<String> get selectedFiles => List.unmodifiable(_selectedFiles);
  Map<String, dynamic>? get stagedResult => _stagedResult;
  String? get stagingError => _stagingError;
  bool get isStaging => _isStaging;

  bool get hasValidStagedSelection =>
      _selectedFiles.isNotEmpty &&
      !_isStaging &&
      _stagingError == null &&
      _stagedResult != null;

  String get taskId => _stagedResult?['taskId']?.toString() ?? '';

  int get totalCount {
    if (_stagedResult != null) {
      if (_stagedResult!['fileCount'] is num) {
        return (_stagedResult!['fileCount'] as num).toInt();
      }
      if (_stagedResult!['count'] is num) {
        return (_stagedResult!['count'] as num).toInt();
      }
    }
    return _selectedFiles.length;
  }

  int get totalBytes {
    if (_stagedResult != null) {
      if (_stagedResult!['totalSize'] is num) {
        return (_stagedResult!['totalSize'] as num).toInt();
      }
      if (_stagedResult!['totalBytes'] is num) {
        return (_stagedResult!['totalBytes'] as num).toInt();
      }
    }
    return 0;
  }

  static String canonicalPath(String filePath) {
    final normalized = p.normalize(filePath);
    return Platform.isWindows ? normalized.toLowerCase() : normalized;
  }

  static bool containsCanonical(Iterable<String> paths, String path) {
    final target = canonicalPath(path);
    return paths.any((item) => canonicalPath(item) == target);
  }

  Future<bool> addPaths(
    List<String> rawPaths, {
    AppLanguage language = AppLanguage.english,
  }) async {
    final transfer = bridgeClient.transferState;
    final isTransferActive =
        transfer.active &&
        !const ['completed', 'failed', 'cancelled'].contains(transfer.phase);
    if (isTransferActive) {
      _stagingError = appText(language, 'dragDropUnavailable');
      notifyListeners();
      return false;
    }

    final proposed = List<String>.from(_selectedFiles);
    for (final raw in rawPaths) {
      if (raw.isEmpty) continue;
      try {
        // Async type()/list() hand the syscalls off instead of blocking this
        // isolate's event loop — typeSync()/listSync() on a large dropped
        // folder used to freeze the whole window until the scan finished.
        final type = await FileSystemEntity.type(raw);
        if (type == FileSystemEntityType.file) {
          final normalized = p.normalize(raw);
          if (!containsCanonical(proposed, normalized)) {
            proposed.add(normalized);
          }
        } else if (type == FileSystemEntityType.directory) {
          final dir = Directory(raw);
          await for (final entity in dir.list(
            recursive: true,
            followLinks: false,
          )) {
            if (entity is File) {
              final normalized = p.normalize(entity.path);
              if (!containsCanonical(proposed, normalized)) {
                proposed.add(normalized);
              }
            }
          }
        }
      } catch (e) {
        debugPrint('[OutgoingStagingController] Error inspecting $raw: $e');
      }
    }

    if (proposed.length == _selectedFiles.length) {
      // No new paths were added (either empty drop or all items were duplicates)
      return _selectedFiles.isNotEmpty;
    }

    final currentSeq = ++_requestSeq;
    _isStaging = true;
    _stagingError = null;
    notifyListeners();

    try {
      final result = await bridgeClient.stageFiles(proposed);
      if (currentSeq != _requestSeq) {
        return false;
      }
      _isStaging = false;
      if (result != null) {
        _selectedFiles
          ..clear()
          ..addAll(proposed);
        _stagedResult = result;
        _stagingError = null;
        notifyListeners();
        return true;
      } else {
        _stagingError = appText(language, 'stagingFailed');
        notifyListeners();
        return false;
      }
    } catch (e) {
      if (currentSeq != _requestSeq) {
        return false;
      }
      _isStaging = false;
      _stagingError = appText(language, 'stagingFailed');
      notifyListeners();
      return false;
    }
  }

  Future<void> removePath(
    String path, {
    AppLanguage language = AppLanguage.english,
  }) async {
    final targetCanonical = canonicalPath(path);
    final proposed = _selectedFiles
        .where((p) => canonicalPath(p) != targetCanonical)
        .toList();

    if (proposed.length == _selectedFiles.length) {
      return;
    }

    final currentSeq = ++_requestSeq;
    _isStaging = true;
    _stagingError = null;
    notifyListeners();

    try {
      if (proposed.isEmpty) {
        final result = await bridgeClient.stageFiles([]);
        if (currentSeq != _requestSeq) return;
        _isStaging = false;
        if (result != null) {
          _selectedFiles.clear();
          _stagedResult = null;
          _stagingError = null;
        } else {
          _stagingError = appText(language, 'stagingFailed');
        }
      } else {
        final result = await bridgeClient.stageFiles(proposed);
        if (currentSeq != _requestSeq) return;
        _isStaging = false;
        if (result != null) {
          _selectedFiles
            ..clear()
            ..addAll(proposed);
          _stagedResult = result;
          _stagingError = null;
        } else {
          _stagingError = appText(language, 'stagingFailed');
        }
      }
    } catch (e) {
      if (currentSeq != _requestSeq) return;
      _isStaging = false;
      _stagingError = appText(language, 'stagingFailed');
    }
    notifyListeners();
  }

  Future<bool> clear({AppLanguage language = AppLanguage.english}) async {
    if (_selectedFiles.isEmpty && _stagedResult == null) {
      return true;
    }

    final currentSeq = ++_requestSeq;
    _isStaging = true;
    _stagingError = null;
    notifyListeners();

    try {
      final result = await bridgeClient.stageFiles([]);
      if (currentSeq != _requestSeq) return false;
      _isStaging = false;
      if (result != null) {
        _selectedFiles.clear();
        _stagedResult = null;
        _stagingError = null;
        notifyListeners();
        return true;
      } else {
        _stagingError = appText(language, 'stagingFailed');
      }
    } catch (e) {
      if (currentSeq != _requestSeq) return false;
      _isStaging = false;
      _stagingError = appText(language, 'stagingFailed');
    }
    notifyListeners();
    return false;
  }
}
