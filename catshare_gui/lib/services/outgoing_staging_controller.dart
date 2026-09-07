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

  OutgoingStagingController({required this.bridgeClient});

  List<String> get selectedFiles => List.unmodifiable(_selectedFiles);
  Map<String, dynamic>? get stagedResult => _stagedResult;
  String? get stagingError => _stagingError;
  bool get isStaging => _isStaging;

  int get totalCount {
    if (_stagedResult != null && _stagedResult!['count'] is num) {
      return (_stagedResult!['count'] as num).toInt();
    }
    return _selectedFiles.length;
  }

  int get totalBytes {
    if (_stagedResult != null && _stagedResult!['totalBytes'] is num) {
      return (_stagedResult!['totalBytes'] as num).toInt();
    }
    return 0;
  }

  Future<bool> addPaths(
    List<String> rawPaths, {
    AppLanguage language = AppLanguage.english,
  }) async {
    final transfer = bridgeClient.transferState;
    final isTransferActive = transfer.active &&
        !const ['completed', 'failed', 'cancelled'].contains(transfer.phase);
    if (isTransferActive) {
      _stagingError = appText(language, 'dragDropUnavailable');
      notifyListeners();
      return false;
    }

    final toAdd = <String>[];
    for (final raw in rawPaths) {
      if (raw.isEmpty) continue;
      try {
        final type = FileSystemEntity.typeSync(raw);
        if (type == FileSystemEntityType.file) {
          final normalized = p.normalize(raw);
          if (!_selectedFiles.contains(normalized) &&
              !toAdd.contains(normalized)) {
            toAdd.add(normalized);
          }
        } else if (type == FileSystemEntityType.directory) {
          final dir = Directory(raw);
          for (final entity in dir.listSync(recursive: true, followLinks: false)) {
            if (entity is File) {
              final normalized = p.normalize(entity.path);
              if (!_selectedFiles.contains(normalized) &&
                  !toAdd.contains(normalized)) {
                toAdd.add(normalized);
              }
            }
          }
        }
      } catch (e) {
        debugPrint('[OutgoingStagingController] Error inspecting $raw: $e');
      }
    }

    if (toAdd.isEmpty) {
      return false;
    }

    _selectedFiles.addAll(toAdd);
    _isStaging = true;
    _stagingError = null;
    notifyListeners();

    try {
      final result = await bridgeClient.stageFiles(_selectedFiles);
      _isStaging = false;
      if (result != null) {
        _stagedResult = result;
        _stagingError = null;
        notifyListeners();
        return true;
      } else {
        _stagingError = appText(language, 'stagingFailed');
        for (final item in toAdd) {
          _selectedFiles.remove(item);
        }
        notifyListeners();
        return false;
      }
    } catch (e) {
      _isStaging = false;
      _stagingError = appText(language, 'stagingFailed');
      for (final item in toAdd) {
        _selectedFiles.remove(item);
      }
      notifyListeners();
      return false;
    }
  }

  Future<void> removePath(
    String path, {
    AppLanguage language = AppLanguage.english,
  }) async {
    _selectedFiles.remove(path);
    if (_selectedFiles.isEmpty) {
      _stagedResult = null;
      _stagingError = null;
      _isStaging = false;
      await bridgeClient.stageFiles([]);
      notifyListeners();
      return;
    }

    _isStaging = true;
    _stagingError = null;
    notifyListeners();

    try {
      final result = await bridgeClient.stageFiles(_selectedFiles);
      _isStaging = false;
      if (result != null) {
        _stagedResult = result;
        _stagingError = null;
      } else {
        _stagingError = appText(language, 'stagingFailed');
      }
    } catch (e) {
      _isStaging = false;
      _stagingError = appText(language, 'stagingFailed');
    }
    notifyListeners();
  }

  Future<void> clear() async {
    _selectedFiles.clear();
    _stagedResult = null;
    _stagingError = null;
    _isStaging = false;
    await bridgeClient.stageFiles([]);
    notifyListeners();
  }
}