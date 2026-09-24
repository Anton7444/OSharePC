#ifndef RUNNER_DRAG_DROP_BRIDGE_H_
#define RUNNER_DRAG_DROP_BRIDGE_H_

#include <flutter/binary_messenger.h>
#include <flutter/method_channel.h>
#include <flutter/standard_method_codec.h>
#include <windows.h>
#include <ole2.h>
#include <shellapi.h>

#include <memory>
#include <chrono>
#include <cstdint>
#include <functional>
#include <string>
#include <vector>

#include "desktop_drop_hot_zone.h"

class DragDropBridge : public IDropTarget {
 public:
  static DragDropBridge* Register(flutter::BinaryMessenger* messenger,
                                  HWND child_hwnd,
                                  bool is_desktop_drop_panel,
                                  DesktopDropHotZone* desktop_hot_zone,
                                  std::function<void(bool)>
                                      set_panel_hit_test_transparent);

  DragDropBridge(
      std::unique_ptr<flutter::MethodChannel<flutter::EncodableValue>> channel,
      HWND child_hwnd, bool is_desktop_drop_panel,
      DesktopDropHotZone* desktop_hot_zone,
      std::function<void(bool)> set_panel_hit_test_transparent);
  virtual ~DragDropBridge();

  HRESULT __stdcall QueryInterface(REFIID riid, void** ppv) override;
  ULONG __stdcall AddRef() override;
  ULONG __stdcall Release() override;

  HRESULT __stdcall DragEnter(IDataObject* pDataObj, DWORD grfKeyState,
                              POINTL pt, DWORD* pdwEffect) override;
  HRESULT __stdcall DragOver(DWORD grfKeyState, POINTL pt,
                             DWORD* pdwEffect) override;
  HRESULT __stdcall DragLeave() override;
  HRESULT __stdcall Drop(IDataObject* pDataObj, DWORD grfKeyState, POINTL pt,
                         DWORD* pdwEffect) override;

  void Revoke();
  bool IsRegistered() const { return registered_; }

 private:
  std::unique_ptr<flutter::MethodChannel<flutter::EncodableValue>> channel_;
  HWND child_hwnd_ = nullptr;
  HWND root_hwnd_ = nullptr;
  HWND drop_target_hwnd_ = nullptr;
  DesktopDropHotZone* desktop_hot_zone_ = nullptr;
  std::function<void(bool)> set_panel_hit_test_transparent_;
  // One initial FlutterWindow owner reference; successful RegisterDragDrop
  // adds one OLE reference, released by the matching RevokeDragDrop.
  LONG ref_count_ = 1;
  bool registered_ = false;
  bool enabled_ = false;
  bool has_files_ = false;
  bool is_desktop_drop_panel_ = false;

  void EnsureRegistered();
  void SetEnabled(bool enabled);
  void LogStartupState() const;
  void HandleDragPosition(const char* method, POINTL pt);
  void DispatchQueuedDrop();
  bool QueueDesktopDrop(std::vector<std::string> paths, POINTL point,
                        uint64_t session_id);
  void RecordOleTransition(const char* transition);
  static bool SupportsFileDrop(IDataObject* data_object);
  static std::vector<std::string> ExtractFilePaths(IDataObject* data_object);

  std::vector<std::string> pending_drop_paths_;
  POINT pending_drop_point_{};
  uint64_t pending_drop_session_id_ = 0;
  bool pending_drop_message_posted_ = false;
  uint64_t next_drag_session_id_ = 0;
  uint64_t active_drag_session_id_ = 0;
  std::chrono::steady_clock::time_point transition_window_start_{};
  int transition_count_ = 0;
  bool churn_warning_logged_ = false;
};

#endif  // RUNNER_DRAG_DROP_BRIDGE_H_
