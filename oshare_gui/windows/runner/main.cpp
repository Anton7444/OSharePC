#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>
#include <windows.h>

#include <algorithm>
#include <iostream>

#include "flutter_window.h"
#include "utils.h"

int APIENTRY wWinMain(_In_ HINSTANCE instance, _In_opt_ HINSTANCE prev,
                      _In_ wchar_t *command_line, _In_ int show_command) {
  std::vector<std::string> command_line_arguments =
      GetCommandLineArguments();
  const bool is_drop_panel =
      std::find(command_line_arguments.begin(), command_line_arguments.end(),
                "--desktop-drop-panel") != command_line_arguments.end();
  if (is_drop_panel) {
    // Flutter's Dart runtime does not reliably expose runner arguments through
    // Platform.executableArguments. Mirror the native mode into the process
    // environment so Dart cannot fall back into the main-app launch path.
    SetEnvironmentVariableW(L"OSHAREPC_DESKTOP_DROP_PANEL", L"1");
  }

  HANDLE hMutex = nullptr;
  if (!is_drop_panel) {
    hMutex = CreateMutex(nullptr, TRUE, L"Local\\OShareGui-SingleInstance");
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
      // The drop panel is another Flutter window, so duplicate launches must
      // target the main app by its dedicated title.
      HWND hWnd = FindWindow(nullptr, L"OsharePC");
      if (hWnd) {
        ShowWindow(hWnd, SW_RESTORE);
        SetForegroundWindow(hWnd);
      }
      if (hMutex) {
        CloseHandle(hMutex);
      }
      return EXIT_SUCCESS;
    }
  }

  // Attach to console when present (e.g., 'flutter run') or create a
  // new console when running with a debugger.
  if (!::AttachConsole(ATTACH_PARENT_PROCESS) && ::IsDebuggerPresent()) {
    CreateAndAttachConsole();
  }

  // Initialize OLE and COM, so that OLE drag-and-drop and COM are available
  // for use in the library and plugins.
  HRESULT ole_hr = ::OleInitialize(nullptr);
  const bool ole_initialized = SUCCEEDED(ole_hr);
  if (!ole_initialized) {
    std::cerr << "[Main] OleInitialize failed with HRESULT: 0x"
              << std::hex << ole_hr << std::dec << std::endl;
  }

  flutter::DartProject project(L"data");

  const bool start_hidden = std::find(command_line_arguments.begin(),
                                      command_line_arguments.end(),
                                      "--startup") != command_line_arguments.end();

  project.set_dart_entrypoint_arguments(std::move(command_line_arguments));

  FlutterWindow window(project, start_hidden || is_drop_panel);
  Win32Window::Point origin(is_drop_panel ? 0 : 10, is_drop_panel ? 0 : 10);
  // Create the drop panel at its largest logical size. Dart later shrinks it
  // to the invisible hit target, but starting small makes the Flutter surface
  // larger than the native HWND during DPI/startup races and clips the right
  // side of the panel in packaged builds.
  Win32Window::Size size(is_drop_panel ? 720 : 1280,
                         is_drop_panel ? 360 : 720);
  const wchar_t* title =
      is_drop_panel ? L"OsharePC Drop Target" : L"OsharePC";
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
