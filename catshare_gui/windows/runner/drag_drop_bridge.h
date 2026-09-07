#ifndef RUNNER_DRAG_DROP_BRIDGE_H_
#define RUNNER_DRAG_DROP_BRIDGE_H_

#include <flutter/binary_messenger.h>
#include <flutter/method_channel.h>
#include <flutter/standard_method_codec.h>
#include <windows.h>
#include <ole2.h>
#include <shellapi.h>

#include <memory>
#include <string>
#include <vector>

class DragDropBridge : public IDropTarget {
 public:
  static void Register(flutter::BinaryMessenger* messenger, HWND window_handle);

  DragDropBridge(std::unique_ptr<flutter::MethodChannel<flutter::EncodableValue>> channel,
                 HWND window_handle);
  virtual ~DragDropBridge();

  // IDropTarget implementation
  HRESULT __stdcall QueryInterface(REFIID riid, void** ppv) override;
  ULONG __stdcall AddRef() override;
  ULONG __stdcall Release() override;

  HRESULT __stdcall DragEnter(IDataObject* pDataObj, DWORD grfKeyState, POINTL pt, DWORD* pdwEffect) override;
  HRESULT __stdcall DragOver(DWORD grfKeyState, POINTL pt, DWORD* pdwEffect) override;
  HRESULT __stdcall DragLeave() override;
  HRESULT __stdcall Drop(IDataObject* pDataObj, DWORD grfKeyState, POINTL pt, DWORD* pdwEffect) override;

  void EnsureRegistered();

 private:
  std::unique_ptr<flutter::MethodChannel<flutter::EncodableValue>> channel_;
  HWND window_handle_;
  LONG ref_count_ = 1;
  bool registered_ = false;
  bool has_files_ = false;

  void HandleDragPosition(const char* method, POINTL pt);
  static std::vector<std::string> ExtractFilePaths(IDataObject* pDataObj);
};

#endif  // RUNNER_DRAG_DROP_BRIDGE_H_