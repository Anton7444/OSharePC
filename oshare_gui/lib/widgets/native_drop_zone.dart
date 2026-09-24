import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'dart:async';

class DragDropService {
  static final DragDropService instance = DragDropService._();
  DragDropService._();

  static const MethodChannel _channel = MethodChannel('oshare/drag_drop');

  final List<void Function(bool isDragging)> _dragStateListeners = [];
  final List<void Function(List<String> paths)> _dropListeners = [];
  bool _initialized = false;
  bool _isDragging = false;
  int _enabledZoneCount = 0;

  bool get isDragging => _isDragging;

  Future<void> activateDesktopDropPanel() async {
    await _channel.invokeMethod<void>('activateDesktopDropPanel');
  }

  Future<void> restoreDesktopDropPanel() async {
    await _channel.invokeMethod<void>('restoreDesktopDropPanel');
  }

  Future<void> setDesktopDropPanelHitTestTransparent(bool transparent) async {
    await _channel.invokeMethod<void>(
      'setDesktopDropPanelHitTestTransparent',
      transparent,
    );
  }

  void init() {
    if (_initialized) return;
    _initialized = true;
    _channel.setMethodCallHandler((call) async {
      switch (call.method) {
        case 'entered':
        case 'updated':
          debugPrint('[DragDropService] Native drag event: ${call.method}');
          _isDragging = true;
          for (final l in List.of(_dragStateListeners)) {
            l(true);
          }
          break;
        case 'exited':
          debugPrint('[DragDropService] Native drag event: exited');
          _isDragging = false;
          for (final l in List.of(_dragStateListeners)) {
            l(false);
          }
          break;
        case 'dropped':
          debugPrint('[DragDropService] Native drag event: dropped');
          _isDragging = false;
          for (final l in List.of(_dragStateListeners)) {
            l(false);
          }
          final args = call.arguments;
          List<String> paths = [];
          if (args is Map && args['paths'] is List) {
            paths = (args['paths'] as List).map((e) => e.toString()).toList();
          }
          debugPrint('[DragDropService] Dropped ${paths.length} items');
          for (final l in List.of(_dropListeners)) {
            l(paths);
          }
          break;
      }
    });
  }

  void addDragStateListener(void Function(bool) listener) =>
      _dragStateListeners.add(listener);
  void removeDragStateListener(void Function(bool) listener) =>
      _dragStateListeners.remove(listener);

  void addDropListener(void Function(List<String>) listener) =>
      _dropListeners.add(listener);
  void removeDropListener(void Function(List<String>) listener) =>
      _dropListeners.remove(listener);

  void attachZone(
    void Function(bool isDragging) dragStateListener,
    void Function(List<String> paths) dropListener,
  ) {
    _dragStateListeners.add(dragStateListener);
    _dropListeners.add(dropListener);
    _enabledZoneCount++;
    if (_enabledZoneCount == 1) {
      unawaited(_setNativeEnabled(true));
    }
  }

  void detachZone(
    void Function(bool isDragging) dragStateListener,
    void Function(List<String> paths) dropListener,
  ) {
    _dragStateListeners.remove(dragStateListener);
    _dropListeners.remove(dropListener);
    if (_enabledZoneCount == 0) return;
    _enabledZoneCount--;
    if (_enabledZoneCount == 0) {
      unawaited(_setNativeEnabled(false));
    }
  }

  Future<void> _setNativeEnabled(bool enabled) async {
    try {
      await _channel.invokeMethod<void>('setEnabled', enabled);
    } catch (error) {
      debugPrint('[DragDropService] Failed to set native drop state: $error');
    }
  }
}

class NativeDropZone extends StatefulWidget {
  final Widget child;
  final bool enabled;
  final ValueChanged<bool>? onDragStateChanged;
  final ValueChanged<List<String>>? onDropped;

  const NativeDropZone({
    super.key,
    required this.child,
    this.enabled = true,
    this.onDragStateChanged,
    this.onDropped,
  });

  @override
  State<NativeDropZone> createState() => _NativeDropZoneState();
}

class _NativeDropZoneState extends State<NativeDropZone> {
  bool _attached = false;

  @override
  void initState() {
    super.initState();
    DragDropService.instance.init();
    if (widget.enabled) {
      _attach();
    }
  }

  @override
  void didUpdateWidget(NativeDropZone oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.enabled && !oldWidget.enabled) {
      _attach();
    } else if (!widget.enabled && oldWidget.enabled) {
      _detach();
      widget.onDragStateChanged?.call(false);
    }
  }

  void _attach() {
    if (_attached) return;
    _attached = true;
    DragDropService.instance.attachZone(_onDragState, _onDrop);
  }

  void _detach() {
    if (!_attached) return;
    _attached = false;
    DragDropService.instance.detachZone(_onDragState, _onDrop);
  }

  void _onDragState(bool isDragging) {
    if (!mounted || !widget.enabled) return;
    widget.onDragStateChanged?.call(isDragging);
  }

  void _onDrop(List<String> paths) {
    if (!mounted || !widget.enabled) return;
    widget.onDropped?.call(paths);
  }

  @override
  void dispose() {
    _detach();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return widget.child;
  }
}
