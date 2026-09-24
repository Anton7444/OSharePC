#include "flutter_window.h"

#include <optional>

#include "desktop_drop_hot_zone.h"
#include "drag_drop_bridge.h"
#include "flutter/generated_plugin_registrant.h"

FlutterWindow::FlutterWindow(const flutter::DartProject& project,
                             bool start_hidden,
                             bool is_desktop_drop_panel)
    : project_(project),
      start_hidden_(start_hidden),
      is_desktop_drop_panel_(is_desktop_drop_panel) {}

FlutterWindow::~FlutterWindow() { ReleaseDragDropBridge(); }

void FlutterWindow::ReleaseDragDropBridge() {
  // Clear the owner pointer before touching COM/OLE. RevokeDragDrop may release
  // its reference synchronously, while this window still owns the one initial
  // reference. The one owner Release below happens after the matching revoke.
  auto* bridge = drag_drop_bridge_;
  drag_drop_bridge_ = nullptr;
  if (bridge == nullptr) return;
  bridge->Revoke();
  bridge->Release();
}

bool FlutterWindow::OnCreate() {
  if (!Win32Window::OnCreate()) return false;

  RECT frame = GetClientArea();
  // Keep the Flutter rendering surface fixed at the full picker dimensions.
  flutter_controller_ = std::make_unique<flutter::FlutterViewController>(
      frame.right - frame.left, frame.bottom - frame.top, project_);
  if (!flutter_controller_->engine() || !flutter_controller_->view()) {
    return false;
  }

  RegisterPlugins(flutter_controller_->engine());
  const HWND child_hwnd = flutter_controller_->view()->GetNativeWindow();
  SetChildContent(child_hwnd);

  if (is_desktop_drop_panel_) {
    desktop_drop_hot_zone_ = std::make_unique<DesktopDropHotZone>();
    // Own the hot-zone by the panel window. Dart shows the 720x360 preview on
    // DragEnter; without ownership it lands above the hot-zone, Explorer's
    // WindowFromPoint (cross-process, so HTTRANSPARENT is ignored) resolves
    // to the preview, which has no drop target, and the cursor turns into a
    // "no drop" icon.
    if (!desktop_drop_hot_zone_->Create(GetHandle())) {
      desktop_drop_hot_zone_.reset();
      return false;
    }
  }

  drag_drop_bridge_ = DragDropBridge::Register(
      flutter_controller_->engine()->messenger(), child_hwnd,
      is_desktop_drop_panel_, desktop_drop_hot_zone_.get(),
      [this](bool transparent) {
        SetDesktopDropPanelHitTestTransparent(transparent);
      });

  flutter_controller_->engine()->SetNextFrameCallback([&]() {
    if (!start_hidden_) this->Show();
  });

  // A first frame may finish before the callback is registered. This schedules
  // the Flutter surface without showing a desktop panel that starts hidden.
  flutter_controller_->ForceRedraw();
  return true;
}

void FlutterWindow::OnDestroy() {
  ReleaseDragDropBridge();
  // Revoke the OLE target before destroying its HWND.
  desktop_drop_hot_zone_.reset();
  if (flutter_controller_) flutter_controller_ = nullptr;
  Win32Window::OnDestroy();
}

LRESULT FlutterWindow::MessageHandler(HWND hwnd, UINT const message,
                                      WPARAM const wparam,
                                      LPARAM const lparam) noexcept {
  if (is_desktop_drop_panel_ && desktop_drop_drag_hit_test_transparent_ &&
      message == WM_NCHITTEST) {
    // The native hot-zone and Flutter window share this UI thread. Returning
    // HTTRANSPARENT makes Windows continue hit-testing to the registered
    // hot-zone underneath while the drag preview is visible.
    return HTTRANSPARENT;
  }

  if (flutter_controller_) {
    std::optional<LRESULT> result =
        flutter_controller_->HandleTopLevelWindowProc(hwnd, message, wparam,
                                                      lparam);
    if (result) return *result;
  }

  if (message == WM_FONTCHANGE && flutter_controller_) {
    flutter_controller_->engine()->ReloadSystemFonts();
  }
  return Win32Window::MessageHandler(hwnd, message, wparam, lparam);
}

void FlutterWindow::SetDesktopDropPanelHitTestTransparent(bool transparent) {
  if (!is_desktop_drop_panel_ ||
      desktop_drop_drag_hit_test_transparent_ == transparent) {
    return;
  }
  desktop_drop_drag_hit_test_transparent_ = transparent;

  // While a file is being dragged, the Flutter preview is visual-only.
  // Disabling the top-level Flutter HWND keeps it visible but makes APIs such
  // as WindowFromPoint skip it, so OLE continues to resolve the cursor to the
  // registered native hot-zone underneath. Re-enable it immediately after the
  // drag finishes so the staged device picker is interactive again.
  const HWND root_hwnd = GetHandle();
  if (root_hwnd != nullptr && IsWindow(root_hwnd)) {
    EnableWindow(root_hwnd, transparent ? FALSE : TRUE);
  }

  OutputDebugStringA(transparent
                         ? "[FlutterWindow] desktop panel hit-test passthrough=true; root disabled\n"
                         : "[FlutterWindow] desktop panel hit-test passthrough=false; root enabled\n");
}
