# Windows Sender Reliability and Lifecycle Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Remove confirmed concurrency, cancellation, lifecycle, shutdown, persistence, and stale-file hazards without changing protocols or UI contracts.

**Architecture:** Keep the existing bridge, engine, cache, and streaming ZIP architecture. Add narrow async gates and cancellation ownership, make stop reversible and dispose final, and validate file snapshots at the point of streaming.

**Tech Stack:** .NET 10 Windows backend, ASP.NET minimal APIs, WinRT GATT, Flutter/Dart ChangeNotifier and `package:http`.

**Spec:** User-provided reliability cleanup brief in `C:\Users\Anton\.codex\attachments\f4d2ca18-fa94-4063-9dc1-d7d84dab6aa6\Pasted text.txt`.

## Global Constraints

- Preserve BLE/Wi-Fi/HTTP/ZIP protocols, endpoint paths, packet formats, receiver compatibility, and UI workflow.
- Do not introduce third-party dependencies or blocking waits across async boundaries.
- Treat expected cancellation during stop, dispose, shutdown, and restaging as normal.

## Review Focus

- Simultaneous send requests: one accepted and one conflict; covered in bridge/engine state tests or code-level gate verification.
- Restaging during a large CRC job: old preparation is cancelled and cannot publish state; covered by CRC cancellation test.
- File mutation between preparation and streaming: stale CRC is rejected; covered by metadata validation test.
- Dispose during delayed Flutter work: no notification after dispose; covered by Dart bridge tests where available.
- Shutdown during active work: response completes before graceful asynchronous teardown; covered by backend lifecycle inspection and build tests.

### Task 1: Inspect and establish focused regression coverage

**Files:** `PreparedCrcCache.cs`, `OfficialStoredZipWriter.cs`, `SenderEngine.cs`, `CatShareBridgeServer.cs`, `SettingsStore.cs`, `catshare_gui/lib/services/bridge_client.dart`, existing test projects.

- [ ] Map current ownership and run baseline backend/Flutter checks before edits.
- [ ] Add only tests supported by the repository's existing test infrastructure; otherwise use build/static verification and document the gap.

### Task 2: Fix bridge polling, HTTP client, delayed callbacks, and shutdown fallback

**Files:** `catshare_gui/lib/services/bridge_client.dart`.

- [ ] Replace overlapping periodic async polling with a single self-scheduling/in-flight guarded cycle.
- [ ] Use one owned `http.Client`, close it on dispose, guard every async notification path, and cancel delayed timers.
- [ ] Request graceful backend shutdown, wait briefly for natural exit, then retain kill only as fallback.

### Task 3: Make sends atomic and engine lifecycle explicit

**Files:** `CatShareBridgeServer.cs`, `SenderEngine.cs`.

- [ ] Add an async-safe send gate/state transition at the bridge and engine boundary, returning conflict for a second request.
- [ ] Make `StopAsync` stop active services without disposing restartable resources; make `DisposeAsync` idempotent and final.
- [ ] Cancel and await owned operations, dispose CTS/semaphores/crypto, and reject starts after permanent disposal.

### Task 4: Harden async event handlers and graceful backend shutdown

**Files:** `ReceiveGattServer.cs`, `CatShareReceiveGattServer.cs`, `GattServiceFallbackAdvertiser.cs`, `CatShareBridgeServer.cs`, `Program.cs`.

- [ ] Put the complete body of every async-void event handler inside exception handling, including request acquisition and cancellation handling.
- [ ] Schedule shutdown after the HTTP response, stop accepting work, stop engine/services, stop the app, and return naturally without `Environment.Exit`.

### Task 5: Bound and cancel CRC preparation; prevent stale file metadata

**Files:** `PreparedCrcCache.cs`, `OfficialStoredZipWriter.cs`, `SenderEngine.cs`, `TransferTask.cs` if needed.

- [ ] Make staged preparation instance/current-set scoped with a replaceable CTS and remove obsolete entries.
- [ ] Carry CRC, length, and UTC write-time snapshot together; pass cancellation into file reads.
- [ ] Re-open with safe sharing, validate snapshot before ZIP headers and validate bytes/metadata after streaming.

### Task 6: Make settings writes atomic and version reporting authoritative

**Files:** `SettingsStore.cs`, `Program.cs`, `CatShareSender.csproj`, Flutter version display only if a compatible source already exists.

- [ ] Serialize concurrent saves through a semaphore and atomically replace a temporary settings file.
- [ ] Report assembly informational/version metadata instead of a manually duplicated timestamp where the build metadata permits.

### Task 7: Verify all changes

- [ ] Run backend restore/build/test and Flutter analyze/test where available.
- [ ] Review diff for protocol/endpoint/UI changes and explicitly report untestable BLE/hardware paths and remaining risks.
