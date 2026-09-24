#ifndef RUNNER_DESKTOP_DROP_HOT_ZONE_H_
#define RUNNER_DESKTOP_DROP_HOT_ZONE_H_

#include <windows.h>

#include <functional>

class DesktopDropHotZone {
 public:
  DesktopDropHotZone();
  ~DesktopDropHotZone();

  DesktopDropHotZone(const DesktopDropHotZone&) = delete;
  DesktopDropHotZone& operator=(const DesktopDropHotZone&) = delete;

  // |owner| is the Flutter panel window. An owned popup always stays above
  // its owner in z-order, so the preview can never cover the drop target.
  bool Create(HWND owner);
  HWND GetHandle() const { return window_handle_; }
  RECT GetWindowRectValue() const;

  bool Activate();
  void SetEnabled(bool enabled);
  bool BeginDrag();
  bool RestoreIdle();
  bool HideForStaged();
  bool PostQueuedDropMessage();
  void SetQueuedDropHandler(std::function<void()> handler);

 private:
  static LRESULT CALLBACK WindowProc(HWND window, UINT message,
                                     WPARAM wparam, LPARAM lparam) noexcept;

  bool PositionHotZone(int logical_width, int logical_height, bool show,
                       const char* stage);
  void LogWindowRect(const char* label) const;

  HWND window_handle_ = nullptr;
  bool ready_ = false;
  bool enabled_ = false;
  bool visible_ = false;
  bool drag_active_ = false;
  std::function<void()> queued_drop_handler_;
};

#endif  // RUNNER_DESKTOP_DROP_HOT_ZONE_H_
