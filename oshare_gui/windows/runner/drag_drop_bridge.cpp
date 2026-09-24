#include "drag_drop_bridge.h"

#include <sstream>
#include <variant>

namespace {

void Log(const std::string& message) {
  const std::string line = message + "\n";
  OutputDebugStringA(line.c_str());
}

std::string WideToUtf8(const std::wstring& value) {
  if (value.empty()) return {};
  const int size = WideCharToMultiByte(CP_UTF8, 0, value.data(),
                                       static_cast<int>(value.size()), nullptr,
                                       0, nullptr, nullptr);
  if (size <= 0) return {};
  std::string result(size, '\0');
  WideCharToMultiByte(CP_UTF8, 0, value.data(),
                      static_cast<int>(value.size()), &result[0], size,
                      nullptr, nullptr);
  return result;
}

void LogRegistration(const char* label, HWND hwnd, HRESULT result) {
  std::ostringstream message;
  message << "[DragDropBridge] RegisterDragDrop " << label << " HWND="
          << static_cast<void*>(hwnd) << " HRESULT=0x" << std::hex
          << static_cast<unsigned long>(result) << std::dec
          << " registered=" << (SUCCEEDED(result) ? "true" : "false")
          << " succeeded=" << (SUCCEEDED(result) ? "true" : "false")
          << " thread_id=" << GetCurrentThreadId();
  Log(message.str());
}

void LogRegistrationCall(const char* label, HWND hwnd) {
  std::ostringstream message;
  message << "[DragDropBridge] RegisterDragDrop call " << label << " HWND="
          << static_cast<void*>(hwnd) << " thread_id=" << GetCurrentThreadId();
  Log(message.str());
}

void LogCallback(const char* callback, HWND hwnd, IDataObject* data_object,
                 bool include_data_object) {
  std::ostringstream message;
  message << "[DragDropBridge] " << callback << " begin HWND="
          << static_cast<void*>(hwnd) << " thread_id=" << GetCurrentThreadId();
  if (include_data_object) {
    message << " IDataObject="
            << (data_object == nullptr ? "null" : "non-null");
  }
  Log(message.str());
}

}  // namespace

DragDropBridge* DragDropBridge::Register(
    flutter::BinaryMessenger* messenger, HWND child_hwnd,
    bool is_desktop_drop_panel, DesktopDropHotZone* desktop_hot_zone,
    std::function<void(bool)> set_panel_hit_test_transparent) {
  auto channel = std::make_unique<
      flutter::MethodChannel<flutter::EncodableValue>>(
      messenger, "oshare/drag_drop", &flutter::StandardMethodCodec::GetInstance());
  return new DragDropBridge(std::move(channel), child_hwnd,
                            is_desktop_drop_panel, desktop_hot_zone,
                            std::move(set_panel_hit_test_transparent));
}

DragDropBridge::DragDropBridge(
    std::unique_ptr<flutter::MethodChannel<flutter::EncodableValue>> channel,
    HWND child_hwnd, bool is_desktop_drop_panel,
    DesktopDropHotZone* desktop_hot_zone,
    std::function<void(bool)> set_panel_hit_test_transparent)
    : channel_(std::move(channel)),
      child_hwnd_(child_hwnd),
      root_hwnd_(is_desktop_drop_panel && child_hwnd != nullptr
                     ? GetAncestor(child_hwnd, GA_ROOT)
                     : nullptr),
      drop_target_hwnd_(is_desktop_drop_panel && desktop_hot_zone != nullptr
                            ? desktop_hot_zone->GetHandle()
                            : child_hwnd),
      desktop_hot_zone_(desktop_hot_zone),
      set_panel_hit_test_transparent_(
          std::move(set_panel_hit_test_transparent)),
      is_desktop_drop_panel_(is_desktop_drop_panel) {
  if (is_desktop_drop_panel_ && desktop_hot_zone_ != nullptr) {
    desktop_hot_zone_->SetQueuedDropHandler(
        [this]() { DispatchQueuedDrop(); });
  }
  channel_->SetMethodCallHandler(
      [this](const flutter::MethodCall<flutter::EncodableValue>& call,
             std::unique_ptr<flutter::MethodResult<flutter::EncodableValue>>
                 result) {
        if (call.method_name() == "setEnabled") {
          const auto* enabled = std::get_if<bool>(call.arguments());
          SetEnabled(enabled != nullptr && *enabled);
          result->Success();
          return;
        }

        if (call.method_name() == "activateDesktopDropPanel") {
          if (!is_desktop_drop_panel_ || desktop_hot_zone_ == nullptr) {
            result->NotImplemented();
            return;
          }
          if (!desktop_hot_zone_->Activate()) {
            result->Error("activate_desktop_drop_panel_failed",
                          "Windows could not position the desktop hot zone.");
            return;
          }
          result->Success();
          return;
        }

        if (call.method_name() == "restoreDesktopDropPanel") {
          if (!is_desktop_drop_panel_ || desktop_hot_zone_ == nullptr) {
            result->NotImplemented();
            return;
          }
          if (!desktop_hot_zone_->RestoreIdle()) {
            result->Error("restore_desktop_drop_panel_failed",
                          "Windows could not restore the idle desktop hot zone.");
            return;
          }
          result->Success();
          return;
        }

        if (call.method_name() == "setDesktopDropPanelHitTestTransparent") {
          if (!is_desktop_drop_panel_ ||
              !set_panel_hit_test_transparent_) {
            result->NotImplemented();
            return;
          }
          const auto* transparent = std::get_if<bool>(call.arguments());
          if (transparent == nullptr) {
            result->Error("invalid_hit_test_state", "Expected a boolean.");
            return;
          }
          // A late Dart hide request must not undo passthrough for a newer
          // drag session that entered while the hide was being scheduled.
          if (!*transparent && active_drag_session_id_ != 0) {
            Log("[DragDropBridge] ignored stale hit-test restore during drag");
          } else {
            set_panel_hit_test_transparent_(*transparent);
          }
          result->Success();
          return;
        }

        result->NotImplemented();
      });

  EnsureRegistered();
  LogStartupState();
}

DragDropBridge::~DragDropBridge() { Revoke(); }

void DragDropBridge::EnsureRegistered() {
  if (drop_target_hwnd_ == nullptr || !IsWindow(drop_target_hwnd_)) {
    Log("[DragDropBridge] RegisterDragDrop skipped (no valid target HWND)");
    return;
  }

  LogRegistrationCall(is_desktop_drop_panel_ ? "desktop hot-zone"
                                             : "Flutter child",
                       drop_target_hwnd_);
  const HRESULT result = RegisterDragDrop(drop_target_hwnd_, this);
  registered_ = SUCCEEDED(result);
  LogRegistration(is_desktop_drop_panel_ ? "desktop hot-zone" : "Flutter child",
                  drop_target_hwnd_, result);
}

void DragDropBridge::SetEnabled(bool enabled) {
  enabled_ = enabled && registered_;
  if (!enabled_) has_files_ = false;
  if (is_desktop_drop_panel_ && desktop_hot_zone_ != nullptr) {
    desktop_hot_zone_->SetEnabled(enabled_);
  }

  std::ostringstream message;
  message << "[DragDropBridge] "
          << (is_desktop_drop_panel_ ? "desktop-panel" : "main-window")
          << " enabled="
          << (enabled_ ? "true" : "false");
  Log(message.str());
}

void DragDropBridge::LogStartupState() const {
  std::ostringstream message;
  message << "[DragDropBridge] startup child HWND="
          << static_cast<void*>(child_hwnd_) << " root HWND="
          << static_cast<void*>(root_hwnd_) << " drop-target HWND="
          << static_cast<void*>(drop_target_hwnd_);
  Log(message.str());
  if (is_desktop_drop_panel_) {
    Log("[DragDropBridge] desktop-panel enabled=false");
    Log("[DragDropBridge] Flutter root/child regions untouched; surface=720x360");
  } else {
    Log("[DragDropBridge] main-window enabled=false");
  }
  if (is_desktop_drop_panel_ && desktop_hot_zone_ != nullptr) {
    const RECT rect = desktop_hot_zone_->GetWindowRectValue();
    std::ostringstream zone_message;
    zone_message << "[DragDropBridge] hot-zone HWND="
                 << static_cast<void*>(desktop_hot_zone_->GetHandle())
                 << " rect=[" << rect.left << "," << rect.top << " - "
                 << rect.right << "," << rect.bottom << "]";
    Log(zone_message.str());
  }
}

void DragDropBridge::Revoke() {
  if (desktop_hot_zone_ != nullptr) {
    desktop_hot_zone_->SetQueuedDropHandler({});
  }
  pending_drop_paths_.clear();
  pending_drop_message_posted_ = false;
  active_drag_session_id_ = 0;

  const HWND target_hwnd = drop_target_hwnd_;
  const bool revoke_target = registered_ && target_hwnd != nullptr;

  // OLE can release its reference synchronously from RevokeDragDrop. Clear the
  // state first; FlutterWindow still owns the initial reference until its one
  // explicit Release after this method returns.
  registered_ = false;
  drop_target_hwnd_ = nullptr;
  child_hwnd_ = nullptr;
  root_hwnd_ = nullptr;

  if (revoke_target) {
    const HRESULT result = RevokeDragDrop(target_hwnd);
    std::ostringstream message;
    message << "[DragDropBridge] RevokeDragDrop HWND="
            << static_cast<void*>(target_hwnd) << " HRESULT=0x" << std::hex
            << static_cast<unsigned long>(result) << std::dec;
    Log(message.str());
  }
}

HRESULT __stdcall DragDropBridge::QueryInterface(REFIID riid, void** ppv) {
  if (ppv == nullptr) return E_POINTER;
  if (riid == IID_IUnknown || riid == IID_IDropTarget) {
    *ppv = static_cast<IDropTarget*>(this);
    AddRef();
    return S_OK;
  }
  *ppv = nullptr;
  return E_NOINTERFACE;
}

ULONG __stdcall DragDropBridge::AddRef() {
  return InterlockedIncrement(&ref_count_);
}

ULONG __stdcall DragDropBridge::Release() {
  const LONG count = InterlockedDecrement(&ref_count_);
  if (count == 0) {
    delete this;
    return 0;
  }
  return static_cast<ULONG>(count);
}

bool DragDropBridge::SupportsFileDrop(IDataObject* data_object) {
  if (data_object == nullptr) return false;
  FORMATETC format = {CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
  return data_object->QueryGetData(&format) == S_OK;
}

std::vector<std::string> DragDropBridge::ExtractFilePaths(
    IDataObject* data_object) {
  std::vector<std::string> paths;
  if (data_object == nullptr) return paths;

  FORMATETC format = {CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
  if (data_object->QueryGetData(&format) != S_OK) return paths;

  STGMEDIUM storage{};
  if (data_object->GetData(&format, &storage) != S_OK) return paths;

  HDROP drop = static_cast<HDROP>(GlobalLock(storage.hGlobal));
  if (drop != nullptr) {
    const UINT file_count = DragQueryFileW(drop, 0xFFFFFFFF, nullptr, 0);
    paths.reserve(file_count);
    for (UINT index = 0; index < file_count; ++index) {
      const UINT length = DragQueryFileW(drop, index, nullptr, 0);
      if (length == 0) continue;
      std::vector<wchar_t> buffer(length + 1, L'\0');
      DragQueryFileW(drop, index, buffer.data(), length + 1);
      paths.push_back(WideToUtf8(std::wstring(buffer.data())));
    }
    GlobalUnlock(storage.hGlobal);
  }
  ReleaseStgMedium(&storage);
  return paths;
}

void DragDropBridge::HandleDragPosition(const char* method, POINTL point) {
  POINT client_point{point.x, point.y};
  if (child_hwnd_ != nullptr) ScreenToClient(child_hwnd_, &client_point);

  flutter::EncodableMap arguments;
  arguments[flutter::EncodableValue("x")] =
      flutter::EncodableValue(static_cast<double>(client_point.x));
  arguments[flutter::EncodableValue("y")] =
      flutter::EncodableValue(static_cast<double>(client_point.y));
  channel_->InvokeMethod(
      method, std::make_unique<flutter::EncodableValue>(arguments));
}

void DragDropBridge::RecordOleTransition(const char* transition) {
  if (!is_desktop_drop_panel_) return;
  (void)transition;

  const auto now = std::chrono::steady_clock::now();
  if (transition_window_start_ == std::chrono::steady_clock::time_point{} ||
      now - transition_window_start_ >= std::chrono::seconds(1)) {
    transition_window_start_ = now;
    transition_count_ = 0;
    churn_warning_logged_ = false;
  }

  ++transition_count_;
  if (transition_count_ > 10 && !churn_warning_logged_) {
    Log("[DesktopDropHotZone] WARNING: OLE target churn detected");
    churn_warning_logged_ = true;
  }
}

bool DragDropBridge::QueueDesktopDrop(std::vector<std::string> paths,
                                      POINTL point, uint64_t session_id) {
  if (!is_desktop_drop_panel_ || desktop_hot_zone_ == nullptr ||
      pending_drop_message_posted_) {
    return false;
  }

  pending_drop_paths_ = std::move(paths);
  pending_drop_point_ = POINT{point.x, point.y};
  pending_drop_session_id_ = session_id;
  pending_drop_message_posted_ = true;
  if (!desktop_hot_zone_->PostQueuedDropMessage()) {
    pending_drop_paths_.clear();
    pending_drop_message_posted_ = false;
    return false;
  }

  Log("[DragDropBridge] QueuedDrop #" + std::to_string(session_id));
  return true;
}

void DragDropBridge::DispatchQueuedDrop() {
  if (!is_desktop_drop_panel_ || !pending_drop_message_posted_) return;

  const uint64_t session_id = pending_drop_session_id_;
  std::vector<std::string> paths = std::move(pending_drop_paths_);
  const POINT point = pending_drop_point_;
  pending_drop_message_posted_ = false;
  pending_drop_session_id_ = 0;

  if (desktop_hot_zone_ != nullptr && !desktop_hot_zone_->HideForStaged()) {
    Log("[DesktopDropHotZone] failed to hide hot-zone for queued drop");
  }
  if (set_panel_hit_test_transparent_) {
    set_panel_hit_test_transparent_(false);
  }
  Log("[DragDropBridge] DispatchQueuedDrop #" + std::to_string(session_id));

  POINT client_point = point;
  if (child_hwnd_ != nullptr) ScreenToClient(child_hwnd_, &client_point);

  flutter::EncodableList encoded_paths;
  encoded_paths.reserve(paths.size());
  for (const auto& path : paths) {
    encoded_paths.push_back(flutter::EncodableValue(path));
  }

  flutter::EncodableMap arguments;
  arguments[flutter::EncodableValue("paths")] =
      flutter::EncodableValue(encoded_paths);
  arguments[flutter::EncodableValue("x")] =
      flutter::EncodableValue(static_cast<double>(client_point.x));
  arguments[flutter::EncodableValue("y")] =
      flutter::EncodableValue(static_cast<double>(client_point.y));
  channel_->InvokeMethod(
      "dropped", std::make_unique<flutter::EncodableValue>(arguments));
  active_drag_session_id_ = 0;
}

HRESULT __stdcall DragDropBridge::DragEnter(IDataObject* data_object,
                                            DWORD grf_key_state, POINTL point,
                                            DWORD* effect) {
  LogCallback("DragEnter", drop_target_hwnd_, data_object, true);
  if (is_desktop_drop_panel_) {
    active_drag_session_id_ = ++next_drag_session_id_;
    Log("[DragDropBridge] DragEnter #" +
        std::to_string(active_drag_session_id_));
    RecordOleTransition("DragEnter");
  }
  const bool supports_file_drop = SupportsFileDrop(data_object);
  if (is_desktop_drop_panel_) {
    Log("[DragDropBridge] DragEnter desktop panel");
    std::ostringstream message;
    message << "[DesktopDropHotZone] DragEnter CF_HDROP="
            << (supports_file_drop ? "true" : "false") << " cursor=(" << point.x
            << "," << point.y << ")";
    Log(message.str());
  }
  if (effect == nullptr) {
    has_files_ = false;
    return E_INVALIDARG;
  }
  has_files_ = supports_file_drop;

  if (!enabled_ || !has_files_ || !(*effect & DROPEFFECT_COPY)) {
    *effect = DROPEFFECT_NONE;
    has_files_ = false;
    return S_OK;
  }

  if (is_desktop_drop_panel_ &&
      (desktop_hot_zone_ == nullptr || !desktop_hot_zone_->BeginDrag())) {
    *effect = DROPEFFECT_NONE;
    has_files_ = false;
    return S_OK;
  }

  if (is_desktop_drop_panel_ && set_panel_hit_test_transparent_) {
    set_panel_hit_test_transparent_(true);
  }

  *effect = DROPEFFECT_COPY;
  if (!is_desktop_drop_panel_) {
    Log("[DragDropBridge] DragEnter: files detected, advertising DROPEFFECT_COPY");
  }
  HandleDragPosition("entered", point);
  return S_OK;
}

HRESULT __stdcall DragDropBridge::DragOver(DWORD grf_key_state, POINTL point,
                                           DWORD* effect) {
  LogCallback("DragOver", drop_target_hwnd_, nullptr, false);
  if (effect == nullptr) return E_INVALIDARG;
  if (!enabled_ || !has_files_ || !(*effect & DROPEFFECT_COPY)) {
    *effect = DROPEFFECT_NONE;
    has_files_ = false;
    return S_OK;
  }

  *effect = DROPEFFECT_COPY;
  if (!is_desktop_drop_panel_) HandleDragPosition("updated", point);
  return S_OK;
}

HRESULT __stdcall DragDropBridge::DragLeave() {
  LogCallback("DragLeave", drop_target_hwnd_, nullptr, false);
  has_files_ = false;
  if (is_desktop_drop_panel_) {
    Log("[DragDropBridge] DragLeave #" +
        std::to_string(active_drag_session_id_));
    RecordOleTransition("DragLeave");
    if (desktop_hot_zone_ != nullptr) desktop_hot_zone_->RestoreIdle();
    active_drag_session_id_ = 0;
  }
  channel_->InvokeMethod("exited", nullptr);
  return S_OK;
}

HRESULT __stdcall DragDropBridge::Drop(IDataObject* data_object,
                                       DWORD grf_key_state, POINTL point,
                                       DWORD* effect) {
  LogCallback("Drop", drop_target_hwnd_, data_object, true);
  if (effect == nullptr) return E_INVALIDARG;
  const uint64_t session_id = active_drag_session_id_;
  if (is_desktop_drop_panel_) {
    Log("[DragDropBridge] Drop #" + std::to_string(session_id));
  }
  if (!enabled_ || !has_files_ || !(*effect & DROPEFFECT_COPY)) {
    *effect = DROPEFFECT_NONE;
    has_files_ = false;
    if (is_desktop_drop_panel_) {
      if (desktop_hot_zone_ != nullptr) desktop_hot_zone_->RestoreIdle();
      if (set_panel_hit_test_transparent_) {
        channel_->InvokeMethod("exited", nullptr);
      }
      active_drag_session_id_ = 0;
    }
    return S_OK;
  }

  std::vector<std::string> paths = ExtractFilePaths(data_object);
  if (is_desktop_drop_panel_) {
    std::ostringstream message;
    message << "[DesktopDropHotZone] Drop path count=" << paths.size();
    Log(message.str());
    if (paths.empty()) {
      *effect = DROPEFFECT_NONE;
      has_files_ = false;
      if (desktop_hot_zone_ != nullptr) desktop_hot_zone_->RestoreIdle();
      channel_->InvokeMethod("exited", nullptr);
      active_drag_session_id_ = 0;
      return S_OK;
    }

    *effect = DROPEFFECT_COPY;
    if (!QueueDesktopDrop(std::move(paths), point, session_id)) {
      *effect = DROPEFFECT_NONE;
      has_files_ = false;
      if (desktop_hot_zone_ != nullptr) desktop_hot_zone_->RestoreIdle();
      channel_->InvokeMethod("exited", nullptr);
      active_drag_session_id_ = 0;
      Log("[DesktopDropHotZone] failed to queue drop");
      return S_OK;
    }
    has_files_ = false;
    return S_OK;
  } else {
    Log("[DragDropBridge] Drop accepted with " + std::to_string(paths.size()) +
        " files");
  }

  *effect = DROPEFFECT_COPY;
  POINT client_point{point.x, point.y};
  if (child_hwnd_ != nullptr) ScreenToClient(child_hwnd_, &client_point);

  flutter::EncodableList encoded_paths;
  encoded_paths.reserve(paths.size());
  for (const auto& path : paths) {
    encoded_paths.push_back(flutter::EncodableValue(path));
  }

  flutter::EncodableMap arguments;
  arguments[flutter::EncodableValue("paths")] =
      flutter::EncodableValue(encoded_paths);
  arguments[flutter::EncodableValue("x")] =
      flutter::EncodableValue(static_cast<double>(client_point.x));
  arguments[flutter::EncodableValue("y")] =
      flutter::EncodableValue(static_cast<double>(client_point.y));
  channel_->InvokeMethod(
      "dropped", std::make_unique<flutter::EncodableValue>(arguments));

  has_files_ = false;
  return S_OK;
}
