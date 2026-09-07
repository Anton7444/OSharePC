#include "drag_drop_bridge.h"

#include <iostream>

namespace {

std::string WideToUtf8(const std::wstring& wstr) {
  if (wstr.empty()) return {};
  int size = WideCharToMultiByte(CP_UTF8, 0, wstr.data(), static_cast<int>(wstr.size()),
                                nullptr, 0, nullptr, nullptr);
  if (size <= 0) return {};
  std::string result(size, 0);
  WideCharToMultiByte(CP_UTF8, 0, wstr.data(), static_cast<int>(wstr.size()),
                      &result[0], size, nullptr, nullptr);
  return result;
}

static DragDropBridge* g_instance = nullptr;

}  // namespace

void DragDropBridge::Register(flutter::BinaryMessenger* messenger, HWND window_handle) {
  auto channel = std::make_unique<flutter::MethodChannel<flutter::EncodableValue>>(
      messenger, "catshare/drag_drop", &flutter::StandardMethodCodec::GetInstance());
  auto bridge = new DragDropBridge(std::move(channel), window_handle);
  g_instance = bridge;
}

DragDropBridge::DragDropBridge(
    std::unique_ptr<flutter::MethodChannel<flutter::EncodableValue>> channel,
    HWND window_handle)
    : channel_(std::move(channel)), window_handle_(window_handle), ref_count_(1) {
  EnsureRegistered();
}

DragDropBridge::~DragDropBridge() {
  if (registered_ && window_handle_ != nullptr) {
    RevokeDragDrop(window_handle_);
    registered_ = false;
  }
}

void DragDropBridge::EnsureRegistered() {
  if (window_handle_ == nullptr) return;
  HRESULT hr = RegisterDragDrop(window_handle_, this);
  if (SUCCEEDED(hr)) {
    registered_ = true;
    std::cout << "[DragDropBridge] RegisterDragDrop succeeded on HWND " << window_handle_ << std::endl;
  } else if (hr == DRAGDROP_E_ALREADYREGISTERED) {
    registered_ = true;
    std::cout << "[DragDropBridge] Already registered on HWND " << window_handle_ << std::endl;
  } else {
    std::cout << "[DragDropBridge] RegisterDragDrop failed with hr: 0x" << std::hex << hr << std::dec << std::endl;
  }
}

HRESULT __stdcall DragDropBridge::QueryInterface(REFIID riid, void** ppv) {
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
  LONG count = InterlockedDecrement(&ref_count_);
  if (count == 0) {
    delete this;
    return 0;
  }
  return count;
}

std::vector<std::string> DragDropBridge::ExtractFilePaths(IDataObject* pDataObj) {
  std::vector<std::string> paths;
  if (!pDataObj) return paths;

  FORMATETC fmt = {CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
  if (pDataObj->QueryGetData(&fmt) != S_OK) {
    return paths;
  }

  STGMEDIUM stg;
  if (pDataObj->GetData(&fmt, &stg) != S_OK) {
    return paths;
  }

  HDROP hDrop = static_cast<HDROP>(GlobalLock(stg.hGlobal));
  if (hDrop != nullptr) {
    UINT fileCount = DragQueryFileW(hDrop, 0xFFFFFFFF, nullptr, 0);
    paths.reserve(fileCount);
    for (UINT i = 0; i < fileCount; ++i) {
      UINT cch = DragQueryFileW(hDrop, i, nullptr, 0);
      if (cch > 0) {
        std::vector<wchar_t> buffer(cch + 1, L'\0');
        DragQueryFileW(hDrop, i, buffer.data(), cch + 1);
        std::wstring wpath(buffer.data());
        paths.push_back(WideToUtf8(wpath));
      }
    }
    GlobalUnlock(stg.hGlobal);
  }
  ReleaseStgMedium(&stg);
  return paths;
}

void DragDropBridge::HandleDragPosition(const char* method, POINTL pt) {
  POINT client_pt = {pt.x, pt.y};
  if (window_handle_ != nullptr) {
    ScreenToClient(window_handle_, &client_pt);
  }
  flutter::EncodableMap args;
  args[flutter::EncodableValue("x")] = flutter::EncodableValue(static_cast<double>(client_pt.x));
  args[flutter::EncodableValue("y")] = flutter::EncodableValue(static_cast<double>(client_pt.y));
  channel_->InvokeMethod(method, std::make_unique<flutter::EncodableValue>(args));
}

HRESULT __stdcall DragDropBridge::DragEnter(IDataObject* pDataObj, DWORD grfKeyState, POINTL pt, DWORD* pdwEffect) {
  FORMATETC fmt = {CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
  has_files_ = (pDataObj && pDataObj->QueryGetData(&fmt) == S_OK);

  if (has_files_) {
    if (pdwEffect) {
      if (*pdwEffect & DROPEFFECT_COPY) {
        *pdwEffect = DROPEFFECT_COPY;
      } else if (*pdwEffect & DROPEFFECT_LINK) {
        *pdwEffect = DROPEFFECT_LINK;
      }
    }
    HandleDragPosition("entered", pt);
  } else {
    if (pdwEffect) {
      *pdwEffect = DROPEFFECT_NONE;
    }
  }
  return S_OK;
}

HRESULT __stdcall DragDropBridge::DragOver(DWORD grfKeyState, POINTL pt, DWORD* pdwEffect) {
  if (has_files_) {
    if (pdwEffect) {
      if (*pdwEffect & DROPEFFECT_COPY) {
        *pdwEffect = DROPEFFECT_COPY;
      } else if (*pdwEffect & DROPEFFECT_LINK) {
        *pdwEffect = DROPEFFECT_LINK;
      }
    }
    HandleDragPosition("updated", pt);
  } else {
    if (pdwEffect) {
      *pdwEffect = DROPEFFECT_NONE;
    }
  }
  return S_OK;
}

HRESULT __stdcall DragDropBridge::DragLeave() {
  has_files_ = false;
  channel_->InvokeMethod("exited", nullptr);
  return S_OK;
}

HRESULT __stdcall DragDropBridge::Drop(IDataObject* pDataObj, DWORD grfKeyState, POINTL pt, DWORD* pdwEffect) {
  if (!has_files_) {
    if (pdwEffect) *pdwEffect = DROPEFFECT_NONE;
    return S_OK;
  }

  if (pdwEffect) {
    if (*pdwEffect & DROPEFFECT_COPY) {
      *pdwEffect = DROPEFFECT_COPY;
    } else if (*pdwEffect & DROPEFFECT_LINK) {
      *pdwEffect = DROPEFFECT_LINK;
    }
  }

  POINT client_pt = {pt.x, pt.y};
  if (window_handle_ != nullptr) {
    ScreenToClient(window_handle_, &client_pt);
  }

  std::vector<std::string> paths = ExtractFilePaths(pDataObj);
  std::cout << "[DragDropBridge] Drop received with " << paths.size() << " files" << std::endl;

  flutter::EncodableList encodable_paths;
  encodable_paths.reserve(paths.size());
  for (const auto& p : paths) {
    encodable_paths.push_back(flutter::EncodableValue(p));
  }

  flutter::EncodableMap args;
  args[flutter::EncodableValue("paths")] = flutter::EncodableValue(encodable_paths);
  args[flutter::EncodableValue("x")] = flutter::EncodableValue(static_cast<double>(client_pt.x));
  args[flutter::EncodableValue("y")] = flutter::EncodableValue(static_cast<double>(client_pt.y));

  channel_->InvokeMethod("dropped", std::make_unique<flutter::EncodableValue>(args));

  has_files_ = false;
  return S_OK;
}