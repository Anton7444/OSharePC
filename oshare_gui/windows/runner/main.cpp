#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>
#include <windows.h>

#include <algorithm>
#include <iostream>
#include <sstream>
#include <string>

#include "flutter_window.h"
#include "utils.h"

namespace {

void Log(const std::string& message) {
  const std::string line = message + "\n";
  OutputDebugStringA(line.c_str());
}

}  // namespace

int APIENTRY wWinMain(_In_ HINSTANCE instance, _In_opt_ HINSTANCE prev,
                      _In_ wchar_t *command_line, _In_ int show_command) {
  std::vector<std::string> command_line_arguments =
      GetCommandLineArguments();
  const bool is_drop_panel =
      std::find(command_line_arguments.begin(), command_line_arguments.end(),
                "--desktop-drop-panel") != command_line_arguments.end();
  // The receive popup is a short-lived corner card launched (and replaced)
  // by the main app, so it skips the single-instance mutex.
  const bool is_receive_popup =
      std::find(command_line_arguments.begin(), command_line_arguments.end(),
                "--receive-popup") != command_line_arguments.end();
  if (is_drop_panel) {
    // Flutter's Dart runtime does not reliably expose runner arguments through
    // Platform.executableArguments. Mirror the native mode into the process
    // environment so Dart cannot fall back into the main-app launch path.
    SetEnvironmentVariableW(L"OSHAREPC_DESKTOP_DROP_PANEL", L"1");
  }

  HANDLE hMutex = nullptr;
  SetLastError(ERROR_SUCCESS);
  if (!is_receive_popup) {
    hMutex = CreateMutex(
        nullptr, TRUE,
        is_drop_panel ? L"Local\\OShareGui-DesktopDropPanel-SingleInstance"
                      : L"Local\\OShareGui-SingleInstance");
  }
  if (hMutex && GetLastError() == ERROR_ALREADY_EXISTS) {
    if (!is_drop_panel) {
      // The drop panel is another Flutter window, so duplicate launches must
      // target the main app by its dedicated title.
      HWND hWnd = FindWindow(nullptr, L"OsharePC");
      if (hWnd) {
        ShowWindow(hWnd, SW_RESTORE);
        SetForegroundWindow(hWnd);
      }
    }
    CloseHandle(hMutex);
    return EXIT_SUCCESS;
  }

  // Attach to console when present (e.g., 'flutter run') or create a
  // new console when running with a debugger.
  if (!::AttachConsole(ATTACH_PARENT_PROCESS) && ::IsDebuggerPresent()) {
    CreateAndAttachConsole();
  }

  // Initialize OLE and COM, so that OLE drag-and-drop and COM are available
  // for use in the library and plugins.
  {
    std::ostringstream message;
    message << "[Main] OleInitialize call thread_id=" << GetCurrentThreadId();
    Log(message.str());
  }
  HRESULT ole_hr = ::OleInitialize(nullptr);
  const bool ole_initialized = SUCCEEDED(ole_hr);
  {
    std::ostringstream message;
    message << "[Main] OleInitialize result HRESULT=0x" << std::hex
            << static_cast<unsigned long>(ole_hr) << std::dec
            << " succeeded=" << (ole_initialized ? "true" : "false")
            << " thread_id=" << GetCurrentThreadId();
    Log(message.str());
  }
  if (!ole_initialized) {
    std::cerr << "[Main] OleInitialize failed with HRESULT: 0x"
              << std::hex << ole_hr << std::dec << std::endl;
  }

  flutter::DartProject project(L"data");

  const bool start_hidden = std::find(command_line_arguments.begin(),
                                      command_line_arguments.end(),
                                      "--startup") != command_line_arguments.end();

  project.set_dart_entrypoint_arguments(std::move(command_line_arguments));

  FlutterWindow window(project,
                       start_hidden || is_drop_panel || is_receive_popup,
                       is_drop_panel);
  const bool is_corner_window = is_drop_panel || is_receive_popup;
  Win32Window::Point origin(is_corner_window ? 0 : 10,
                            is_corner_window ? 0 : 10);
  // Keep the panel's Flutter surface at its stable full size. The dedicated
  // desktop hot-zone HWND handles idle OLE hit-testing instead of the surface.
  Win32Window::Size size(is_drop_panel ? 720 : is_receive_popup ? 376 : 1280,
                         is_drop_panel ? 360 : is_receive_popup ? 176 : 720);
  const wchar_t* title = is_drop_panel      ? L"OsharePC Drop Target"
                         : is_receive_popup ? L"OsharePC Receive"
                                            : L"OsharePC";
  if (!window.Create(title, origin, size)) {
    if (ole_initialized) {
      ::OleUninitialize();
    }
    return EXIT_FAILURE;
  }
  window.SetQuitOnClose(true);

  ::MSG msg;
  while (::GetMessage(&msg, nullptr, 0, 0)) {
    ::TranslateMessage(&msg);
    ::DispatchMessage(&msg);
  }

  if (ole_initialized) {
    ::OleUninitialize();
  }
  if (hMutex) {
    CloseHandle(hMutex);
  }
  return EXIT_SUCCESS;
}
