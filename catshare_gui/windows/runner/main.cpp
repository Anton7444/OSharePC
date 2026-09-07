#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>
#include <windows.h>

#include <algorithm>
#include <iostream>

#include "flutter_window.h"
#include "utils.h"

int APIENTRY wWinMain(_In_ HINSTANCE instance, _In_opt_ HINSTANCE prev,
                      _In_ wchar_t *command_line, _In_ int show_command) {
  HANDLE hMutex = CreateMutex(nullptr, TRUE, L"Local\\CatShareGui-SingleInstance");
  if (GetLastError() == ERROR_ALREADY_EXISTS) {
    HWND hWnd = FindWindow(L"FLUTTER_RUNNER_WIN32_WINDOW", nullptr);
    if (hWnd) {
      ShowWindow(hWnd, SW_RESTORE);
      SetForegroundWindow(hWnd);
    }
    return EXIT_SUCCESS;
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

  std::vector<std::string> command_line_arguments =
      GetCommandLineArguments();
  const bool start_hidden = std::find(command_line_arguments.begin(),
                                      command_line_arguments.end(),
                                      "--startup") != command_line_arguments.end();

  project.set_dart_entrypoint_arguments(std::move(command_line_arguments));

  FlutterWindow window(project, start_hidden);
  Win32Window::Point origin(10, 10);
  Win32Window::Size size(1280, 720);
  if (!window.Create(L"catshare_gui", origin, size)) {
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
