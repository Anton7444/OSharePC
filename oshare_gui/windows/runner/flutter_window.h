#ifndef RUNNER_FLUTTER_WINDOW_H_
#define RUNNER_FLUTTER_WINDOW_H_

#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>

#include <memory>

#include "win32_window.h"

class DragDropBridge;
class DesktopDropHotZone;

// A window that does nothing but host a Flutter view.
class FlutterWindow : public Win32Window {
 public:
  // Creates a new FlutterWindow hosting a Flutter view running |project|.
  FlutterWindow(const flutter::DartProject& project,
                bool start_hidden,
                bool is_desktop_drop_panel);
  virtual ~FlutterWindow();

  void SetDesktopDropPanelHitTestTransparent(bool transparent);

 protected:
  // Win32Window:
  bool OnCreate() override;
  void OnDestroy() override;
  LRESULT MessageHandler(HWND window, UINT const message, WPARAM const wparam,
                         LPARAM const lparam) noexcept override;

 private:
  // The project to run.
  flutter::DartProject project_;

  // The Flutter instance hosted by this window.
  std::unique_ptr<flutter::FlutterViewController> flutter_controller_;

  // The separate hit-test HWND is used only by the desktop drop panel.
  std::unique_ptr<DesktopDropHotZone> desktop_drop_hot_zone_;

  // The native OLE drag & drop bridge.
  DragDropBridge* drag_drop_bridge_ = nullptr;

  void ReleaseDragDropBridge();

  // Startup launches stay hidden while the tray service is initialized.
  bool start_hidden_;

  // The separate desktop drop panel uses a native hot-zone HWND.
  bool is_desktop_drop_panel_;
  bool desktop_drop_drag_hit_test_transparent_ = false;
};

#endif  // RUNNER_FLUTTER_WINDOW_H_
