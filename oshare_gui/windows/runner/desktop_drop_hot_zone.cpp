#include "desktop_drop_hot_zone.h"

#include <flutter_windows.h>

#include <algorithm>
#include <cmath>
#include <sstream>
#include <utility>

namespace {

constexpr wchar_t kHotZoneClassName[] = L"OSHAREPC_DESKTOP_DROP_HOT_ZONE";
constexpr int kIdleWidth = 460;
constexpr int kIdleHeight = 460;
constexpr int kDragWidth = 620;
constexpr int kDragHeight = 460;
constexpr UINT kDispatchQueuedDropMessage = WM_APP + 0x120;
constexpr UINT_PTR kMouseStateTimerId = 1;
constexpr UINT kMouseStatePollIntervalMs = 80;

void Log(const std::string& message) {
  const std::string line = message + "\n";
  OutputDebugStringA(line.c_str());
}

bool GetPrimaryWorkArea(RECT* work_area, UINT* dpi) {
  if (work_area == nullptr || dpi == nullptr) return false;

  const POINT primary_origin{0, 0};
  const HMONITOR monitor =
      MonitorFromPoint(primary_origin, MONITOR_DEFAULTTOPRIMARY);
  if (monitor == nullptr) return false;

  MONITORINFO monitor_info{};
  monitor_info.cbSize = sizeof(monitor_info);
  if (!GetMonitorInfoW(monitor, &monitor_info)) return false;

  *work_area = monitor_info.rcWork;
  *dpi = FlutterDesktopGetDpiForMonitor(monitor);
  if (*dpi == 0) *dpi = 96;
  return true;
}

}  // namespace

DesktopDropHotZone::DesktopDropHotZone() = default;

DesktopDropHotZone::~DesktopDropHotZone() {
  if (window_handle_ != nullptr && IsWindow(window_handle_)) {
    KillTimer(window_handle_, kMouseStateTimerId);
    ShowWindow(window_handle_, SW_HIDE);
    DestroyWindow(window_handle_);
  }
  window_handle_ = nullptr;
}

bool DesktopDropHotZone::Create(HWND owner) {
  if (window_handle_ != nullptr && IsWindow(window_handle_)) return true;
  HINSTANCE instance = GetModuleHandleW(nullptr);
  WNDCLASSEXW window_class{};
  window_class.cbSize = sizeof(window_class);
  window_class.hInstance = instance;
  window_class.lpfnWndProc = WindowProc;
  window_class.lpszClassName = kHotZoneClassName;
  window_class.hCursor = LoadCursorW(nullptr, IDC_ARROW);
  const ATOM atom = RegisterClassExW(&window_class);
  if (atom == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
    Log("[DesktopDropHotZone] RegisterClassEx failed");
    return false;
  }

  window_handle_ = CreateWindowExW(
      WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED,
      kHotZoneClassName, L"", WS_POPUP, 0, 0, 1, 1, owner, nullptr, instance,
      this);
  if (window_handle_ == nullptr) {
    Log("[DesktopDropHotZone] CreateWindowEx failed");
    return false;
  }

  if (!SetLayeredWindowAttributes(window_handle_, 0, 1, LWA_ALPHA)) {
    Log("[DesktopDropHotZone] SetLayeredWindowAttributes failed");
    DestroyWindow(window_handle_);
    window_handle_ = nullptr;
    return false;
  }

  if (!PositionHotZone(kIdleWidth, kIdleHeight, false, "idle 460x460")) {
    DestroyWindow(window_handle_);
    window_handle_ = nullptr;
    return false;
  }

  SetTimer(window_handle_, kMouseStateTimerId, kMouseStatePollIntervalMs,
           nullptr);
  UpdateClickThrough();

  std::ostringstream message;
  message << "[DesktopDropHotZone] created HWND="
          << static_cast<void*>(window_handle_)
          << " owner=" << static_cast<void*>(owner)
          << " styles=WS_POPUP|WS_EX_TOPMOST|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE|WS_EX_LAYERED alpha=1";
  message << " thread_id=" << GetCurrentThreadId();
  Log(message.str());
  LogWindowRect("startup");
  return true;
}

RECT DesktopDropHotZone::GetWindowRectValue() const {
  RECT rect{};
  if (window_handle_ != nullptr && IsWindow(window_handle_)) {
    GetWindowRect(window_handle_, &rect);
  }
  return rect;
}

bool DesktopDropHotZone::Activate() {
  ready_ = true;
  Log("[DesktopDropHotZone] Flutter panel position ready");
  return !enabled_ || RestoreIdle();
}

void DesktopDropHotZone::SetEnabled(bool enabled) {
  enabled_ = enabled;
  std::ostringstream message;
  message << "[DesktopDropHotZone] enabled=" << (enabled_ ? "true" : "false");
  Log(message.str());

  if (enabled_ && ready_) {
    RestoreIdle();
  } else if (window_handle_ != nullptr) {
    ShowWindow(window_handle_, SW_HIDE);
    visible_ = false;
    drag_active_ = false;
  }
  UpdateClickThrough();
}

bool DesktopDropHotZone::BeginDrag() {
  if (!ready_ || !enabled_ || window_handle_ == nullptr ||
      !IsWindow(window_handle_)) {
    return false;
  }

  // Expand the same registered HWND to the visible compact drop panel.
  // Keeping the HWND stable preserves OLE registration, while the larger
  // active rectangle prevents the cursor from leaving the drop target as soon
  // as the Flutter preview appears.
  if (drag_active_) return true;
  drag_active_ = true;
  UpdateClickThrough();
  if (!PositionHotZone(kDragWidth, kDragHeight, true, "drag 620x460")) {
    drag_active_ = false;
    UpdateClickThrough();
    return false;
  }
  Log("[DesktopDropHotZone] drag active; expanded to 620x460");
  return true;
}

bool DesktopDropHotZone::HideForStaged() {
  if (window_handle_ != nullptr && IsWindow(window_handle_)) {
    ShowWindow(window_handle_, SW_HIDE);
  }
  visible_ = false;
  drag_active_ = false;
  UpdateClickThrough();
  Log("[DesktopDropHotZone] staged hot-zone hidden; Flutter visibility is Dart-controlled");
  return true;
}

bool DesktopDropHotZone::PostQueuedDropMessage() {
  if (window_handle_ == nullptr || !IsWindow(window_handle_)) return false;
  const BOOL posted = PostMessageW(window_handle_, kDispatchQueuedDropMessage,
                                   0, 0);
  if (!posted) {
    Log("[DesktopDropHotZone] PostMessage queued drop failed");
  }
  return posted != FALSE;
}

void DesktopDropHotZone::SetQueuedDropHandler(std::function<void()> handler) {
  queued_drop_handler_ = std::move(handler);
}

bool DesktopDropHotZone::RestoreIdle() {
  if (!ready_ || !enabled_) {
    if (window_handle_ != nullptr && IsWindow(window_handle_)) {
      ShowWindow(window_handle_, SW_HIDE);
    }
    visible_ = false;
    drag_active_ = false;
    UpdateClickThrough();
    return true;
  }
  // Always restore the idle geometry. During an active drag the same HWND is
  // expanded to 620x460, so visibility alone is not enough to know that the
  // idle bounds are already correct.
  drag_active_ = false;
  UpdateClickThrough();
  return PositionHotZone(kIdleWidth, kIdleHeight, true, "idle 460x460");
}

bool DesktopDropHotZone::PositionHotZone(int logical_width,
                                         int logical_height, bool show,
                                         const char* stage) {
  if (window_handle_ == nullptr || !IsWindow(window_handle_)) return false;

  RECT work_area{};
  UINT dpi = 96;
  if (!GetPrimaryWorkArea(&work_area, &dpi)) {
    Log("[DesktopDropHotZone] failed to read primary monitor work area");
    return false;
  }

  const double scale = static_cast<double>(dpi) / 96.0;
  const int work_width = work_area.right - work_area.left;
  const int work_height = work_area.bottom - work_area.top;
  const int width = std::min(
      work_width, static_cast<int>(std::lround(logical_width * scale)));
  const int height = std::min(
      work_height, static_cast<int>(std::lround(logical_height * scale)));
  const int x = work_area.right - width;
  const int y = work_area.bottom - height;
  const UINT flags = SWP_NOACTIVATE | (show ? SWP_SHOWWINDOW : 0);
  if (!SetWindowPos(window_handle_, HWND_TOPMOST, x, y, width, height, flags)) {
    Log("[DesktopDropHotZone] SetWindowPos failed");
    return false;
  }
  visible_ = show;

  std::ostringstream message;
  message << "[DesktopDropHotZone] " << stage << " rect=[" << x << "," << y
          << " - " << x + width << "," << y + height << "] dpi=" << dpi
          << " work=[" << work_area.left << "," << work_area.top << " - "
          << work_area.right << "," << work_area.bottom << "]";
  Log(message.str());
  return true;
}

void DesktopDropHotZone::UpdateClickThrough() {
  if (window_handle_ == nullptr || !IsWindow(window_handle_)) return;

  const bool button_down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
  // Stay hit-testable (catchable by OLE) whenever a drag is already
  // confirmed, or the mouse button is currently held down and a drag might
  // be starting. Otherwise become click-through so idle clicks in this
  // corner reach whatever is actually underneath instead of this invisible
  // zone.
  const bool want_click_through = enabled_ && !drag_active_ && !button_down;
  if (want_click_through == click_through_) return;

  const LONG_PTR ex_style = GetWindowLongPtrW(window_handle_, GWL_EXSTYLE);
  const LONG_PTR new_style = want_click_through
                                 ? (ex_style | WS_EX_TRANSPARENT)
                                 : (ex_style & ~WS_EX_TRANSPARENT);
  if (new_style != ex_style) {
    SetWindowLongPtrW(window_handle_, GWL_EXSTYLE, new_style);
  }
  click_through_ = want_click_through;
}

void DesktopDropHotZone::LogWindowRect(const char* label) const {
  const RECT rect = GetWindowRectValue();
  std::ostringstream message;
  message << "[DesktopDropHotZone] " << label << " HWND="
          << static_cast<void*>(window_handle_) << " rect=[" << rect.left << ","
          << rect.top << " - " << rect.right << "," << rect.bottom << "]";
  Log(message.str());
}

LRESULT CALLBACK DesktopDropHotZone::WindowProc(HWND window, UINT message,
                                                WPARAM wparam,
                                                LPARAM lparam) noexcept {
  if (message == WM_NCCREATE) {
    const auto* create = reinterpret_cast<CREATESTRUCTW*>(lparam);
    SetWindowLongPtrW(window, GWLP_USERDATA,
                      reinterpret_cast<LONG_PTR>(create->lpCreateParams));
  }
  if (message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
  if (message == WM_NCHITTEST) return HTCLIENT;
  if (message == WM_TIMER && wparam == kMouseStateTimerId) {
    auto* hot_zone = reinterpret_cast<DesktopDropHotZone*>(
        GetWindowLongPtrW(window, GWLP_USERDATA));
    if (hot_zone != nullptr) hot_zone->UpdateClickThrough();
    return 0;
  }
  if (message == kDispatchQueuedDropMessage) {
    auto* hot_zone = reinterpret_cast<DesktopDropHotZone*>(
        GetWindowLongPtrW(window, GWLP_USERDATA));
    if (hot_zone != nullptr && hot_zone->queued_drop_handler_) {
      hot_zone->queued_drop_handler_();
    }
    return 0;
  }
  return DefWindowProcW(window, message, wparam, lparam);
}
