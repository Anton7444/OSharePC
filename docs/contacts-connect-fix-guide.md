# Contacts-mode connect fix — implementation guide (for an AI agent)

This guide tells you how to make Contacts-mode (聯絡人) sends connect fast and reliably. Today they take
2–3 s when lucky and up to ~40 s (or fail) when not. Read the whole guide before editing. Work through the
phases in order; each phase ends with a check you must pass before starting the next one.

The analysis behind it comes from decompiling OnePlus Share 17.2.36 (`com.oplus.oshare`). The receiver-side
facts below were checked at bytecode level. Facts marked **UNVERIFIED** depend on the system DCP service
(pantaconnect), which is not in that APK. Phase 0 exists to confirm them.

---

## 1. Background you must understand first

### 1.1 Terms

| Term | Meaning |
|---|---|
| **BB15 / DCP service** | GATT service `0000bb15-…`, hosted by the phone's *system* DCP service (pantaconnect). It is always present in Contacts mode, even when the OShare app is not running. |
| **9999 / OConnect service** | GATT service `00009999-…`, hosted by the OShare app's own `BluetoothGattServer`. It exists only while the app's server runs. |
| **Wake** | The first BB15 request from a peer makes DCP call the OShare provider (`NfcIosProvider`, method `contact_share`). The provider runs `startServer()`, which sets `DcpManager.p() = true`, starts `OShareServer` (which opens 9999) and registers the app's callback with DCP. |
| **`p()` ("DCP-routed mode")** | Receiver flag `DcpManager.f11958h`. While it is true, the app answers most requests **through DCP, i.e. only on BB15**. |
| **Selected device** | Receiver field `w.f9977v`: the peer that last did the 9996 read or a 9995/9990/… write. |

### 1.2 Receiver behaviour (OShare 17.2.36, `com.oplus.oshare.ble.impl.w`)

These rules decide whether a request gets an answer. A request that gets no answer leaves an ATT request
outstanding on the Windows link, which then stays unusable until Windows' **30 s ATT timeout**. That timeout
is where the 40 s cases come from.

| Request | How the receiver answers | Answered on BB15 when | Answered on 9999 when |
|---|---|---|---|
| Read 9996 (Wi-Fi info / "start") | `o1()` | `p()` true | `p()` false |
| **Read 9897** (OConnect key/state) | `f9974s.sendResponse` (own server, always) | **never** | always (while 9999 exists) |
| Write 9896 (state-1, account proof, state-3) | `o1()` (bridge `w.v0`) | `p()` true | `p()` false |
| Write 9995 (cancel), 9990, 9989, 9986 … | `o1()` | `p()` true | `p()` false |

`o1()` (`w.java`): `if (!DcpManager.p()) ownServer.sendResponse(...) else DcpManager.E(...)`, where
`DcpManager.E` calls `sendResponseByGattServer` on DCP.

Further rules:

- **R1 — `p()` turns true** when a BB15 request arrives while `p()` is false and the device is in Contacts
  visibility (provider: `contact_share && !p() && m1.g()==2` → `startServer()`). **Any** read or write you
  send on BB15 can re-enter DCP mode.
- **R2 — `p()` turns false** when the *selected device* disconnects (`onConnectionStateChange` →
  `DcpManager.m("disconnect_selected")`), or when the last recorded contact peer disconnects
  (`DcpManager.l(...)`). The 9999 server stays up afterwards.
- **R3 — reading 9996 opens a receive task** (`OShareTaskManager.f0(...)`). Until it is cancelled, the next
  9897 read reports `state != 0` (busy). Disconnecting usually does **not** cancel it (the disconnect
  handler returns early when no receive dialog is showing). It must be cancelled with a 9995 write of `02`.
- **R4 — a write needs a prior read from the same peer on the current connection.** It needs an entry in
  `w.D` with `m.a() == true`, which the 9996 read sets. Otherwise the receiver logs "can not get deviceinfo"
  and **does not respond**. After a reconnect, a bare 9995 write is never answered. The 9897 read does
  **not** satisfy this rule (it sets `m.c(true)` only in a special NFC case).
- **R5 —** the write handler also rejects a write from a device other than the one that read ("write device
  not same read device"), again without a response.

### 1.3 Why the Android sender is fast (`com.oplus.oshare.ble.impl.j`)

The Android sender connects, raises the connection priority, requests MTU 512 and does one discovery.
`s1()` then picks 9999 if present, **otherwise BB15**, and does everything on that one service. Its first
request is the 9996 read, and that read *is* the handshake. It never waits for 9999, never reconnects and
never cancels its own task, so it never sends a request the receiver will not answer.

### 1.4 Why the PC is slow and luck-dependent

The PC uses the iOS-style OConnect handshake (9897 read, then 9896 writes). That needs 9999, so it wakes the
receiver with a 9996 read on BB15, which sets `p()` true and opens a task (R3). After that, the outcome
depends on whether the wake link happened to drop:

- **The link stays up** (`p()` still true): the 9896 state-1 write on 9999 is never answered → 30 s stall.
  This is the "state-1 write failed, reconnect" path in `SenderEngine.cs` (~line 669).
- **The link drops** (`p()` false): a 9995 cancel on BB15 is never answered, and a 9995 cancel on 9999 sent
  without a prior read on the new link is never answered either (R4).

The current heuristic `WakeClearViaDcp` (`SenderEngine.Contacts.cs:126`: "phones → DCP, tablet → 9999, flip
on timeout") is really guessing the value of `p()`.

**The fix: track `p()` explicitly on the PC side and send every request where the receiver will answer it.**

---

## 2. Code map (PC side)

| File | What is there |
|---|---|
| `GattLink.cs` | Connect (`ConnectAsync` ~282), enumeration (`EnumerateAsync` ~624), OConnect handshake (`OConnectLanSendAsync` ~884, busy-clear ~1000, `WriteOConnectSingle` ~1257), `IsConnected` (147), `Dispose`/`DisposeInBackground` (~1312/1363), exceptions (~1390). Fields `_dcpWifiChar`, `_dcpCancelChar` (99), `OConnectWifiChar`, `OConnectCancelChar` (152). |
| `GattLink.Contacts.cs` | `EnumerateDcpAsync`, `WakeOnLinkAsync` (65), `ClearWakeTaskAsync` (124), `WaitForOConnectServiceAsync` (158), `WaitForLinkDownAsync`. |
| `SenderEngine.Contacts.cs` | Gate, warm links, `WakeClearViaDcp`/`WakeClearTimedOut` (120–139), keep-warm loop, **receive probe `ProbeContactsReceiveStatesAsync` (~248)**, `ConnectContactsAsync` (417). |
| `SenderEngine.cs` | Contacts handshake retry loop (~620–700). |
| `PhoneScanner.cs` | Advertisement watcher (`CreateAndStartWatcher` ~741), Contacts beacons (`WaitForContactsBeaconAsync` ~667). |
| `SendTimeline.cs` | Per-step timing marks. Use it for every new step. |

Code conventions: match the existing style. That means XML `<summary>` comments that explain *why*,
`Log.Info`/`Log.Warn` with a `CONTACTS:`/`BLE:` prefix, every WinRT call bounded by a timeout that also
cancels the underlying operation (`AsTask(cts.Token)`), and `SendTimeline.Mark(...)` at each step.

---

## 3. Hard rules (do not break these)

1. **Never send a request that the current mode will not answer** (see the table in 1.2). Every unanswered
   request costs 30 s.
2. **Never touch BB15 while in 9999 mode** unless you mean to wake (R1). That includes "harmless" reads,
   probes and a fresh `EnumerateDcpAsync` followed by a read or write. Plain service or characteristic
   *discovery* is fine; only ATT reads and writes reach the app.
3. A 9995 write in 9999 mode must be preceded by a 9996 read **on 9999, on the same connection** (R4).
4. Keep the existing safety nets (`AvoidAddress`, `StaleLinkException` handling, bounded waits) until
   Phase 1 has been proven on real devices; trim them only in Phase 2.
5. Do not change the LAN/WebSocket transfer, the account proof (pv=5) or the Everyone-mode path.
6. No model identifiers in commits or code. Commit to the designated branch only.

---

## Phase 0 — Verify the unknowns (no behaviour change)

Goal: confirm the facts the design depends on, using logs.

1. In `GattLink.Contacts.cs` `EnumerateDcpAsync`: drop the 10-minute `Log.Every` throttle on the
   `DCP 0xBB15 characteristics: [...]` line for now, so the line is logged on every Contacts connect.
   Also log, for each characteristic, its UUID and `CharacteristicProperties`.
2. Log the 9999 characteristic list the same way in `EnumerateAsync`.
3. Add `SendTimeline` marks: `wake-read-ok` / `wake-read-failed`, `wake-link-dropped` (when `IsConnected`
   turns false after a wake), and `handshake-write-timeout`.
4. Add a temporary, settings-gated experiment switch (for example env var `OSHARE_CONTACTS_EXPERIMENT=A|B|C`,
   read once at startup):
   - **A:** after a successful wake on the same link, read 9897 on 9999 and write 9896 on **BB15**.
   - **B:** after a successful wake on the same link, read 9897 on 9999 and write 9896 on **9999**. Expected
     to time out; this confirms the theory.
   - **C:** after the wake, cancel on BB15, disconnect, wait until the link is down, reconnect and do
     everything on 9999.
5. Ask the user to run cold (force-stop OShare on the receiver), warm, and screen-off sends to one phone and
   one tablet, and to send back `%LOCALAPPDATA%\OSharePC\sender.log`.

**Decision:**
- If BB15 lists `9896` **and** experiment A succeeds → use **Strategy S1** (split routing) in Phase 1.
- Otherwise → use **Strategy S2** (wake, cancel, release, reconnect).
- If B does *not* time out, stop and report back: the routing model is wrong.

You cannot run Windows BLE in this environment. Implement Phase 0 so it builds
(`dotnet build OShareSender.csproj -c Release -r win-x64`, if a Windows toolchain is available), then hand
over to the user for the device test. Do not guess the results.

---

## Phase 1 — Deterministic routing (the main fix)

### 1.1 Add explicit receiver-mode state to `GattLink`

In `GattLink.Contacts.cs`:

```csharp
/// <summary>Where the receiver answers requests from this link right now (see docs/contacts-connect-fix-guide.md §1.2).
/// Dcp: a BB15 request on THIS connection woke it (DcpManager.p() is true and we are its selected device), so
/// 9996/9995/9896/9990 are answered only on BB15 and 9897 only on 9999. Own: everything is answered on 9999.</summary>
public enum ReceiverRoute { Own, Dcp }
public ReceiverRoute Route { get; private set; } = ReceiverRoute.Own;
/// <summary>True once this connection did a 9996 read the receiver answered (needed before any write, R4).</summary>
public bool HasReadOnThisConnection { get; private set; }
```

- Set `Route = Dcp` and `HasReadOnThisConnection = true` when the 9996 read **on BB15** returns `Success`.
- Set `HasReadOnThisConnection = true` when a 9996 read on 9999 returns `Success` while in `Own` mode.
- The link object is per connection; a new `GattLink` starts as `Own` with `HasReadOnThisConnection = false`.
  The `SenderEngine` must not assume a reconnect keeps DCP mode. After the old link dropped, the receiver's
  `p()` is false (R2).
- Wire the existing `_connectionStatusHandler`: when the status turns `Disconnected`, set `Route = Own` and
  `HasReadOnThisConnection = false`, and mark `wake-link-dropped`. The object is about to die anyway, but
  this keeps any in-flight logic honest.
- Also track, at the `SenderEngine` level per device (`LanPdid`), `bool WakeTaskOpen`: set when a wake read
  succeeded; cleared when a 9995 cancel was answered or the 9897 read reported `state == 0`. This replaces
  `_wakeTaskPending` crossing links.

### 1.2 Route-aware request helpers in `GattLink`

Add one helper per logical request and make **all** existing call sites use them:

```csharp
private GattCharacteristic? CancelCharForRoute()  => Route == ReceiverRoute.Dcp ? _dcpCancelChar : OConnectCancelChar;
private GattCharacteristic? WifiCharForRoute()    => Route == ReceiverRoute.Dcp ? _dcpWifiChar   : OConnectWifiChar;
private GattCharacteristic? HandshakeWriteChar()  => Route == ReceiverRoute.Dcp ? _dcpHandshakeWriteChar /* S1 only */ : OConnectWriteChar;
// The 9897 read is ALWAYS OConnectReadChar (9999) — the receiver answers it only on its own server.
```

- In `EnumerateDcpAsync`, also capture BB15 `9896` into a new `_dcpHandshakeWriteChar` (S1 only).
- `WriteOConnectSingle` must write to `HandshakeWriteChar()`. If that returns null in DCP mode (S2 or BB15
  without 9896), throw a clear `InvalidOperationException`. Never fall back to the 9999 char in DCP mode.
- If a request would need a char that is missing for the current route, throw before sending. Do not send
  the request somewhere else.

### 1.3 Rewrite the wake (`WakeOnLinkAsync`)

New contract: `Task<WakeOutcome> WakeOnLinkAsync(CancellationToken ct)` (drop the `clearViaDcp` parameter).

1. Ensure the BB15 chars are present (`EnumerateDcpAsync`, cached first). If 9996 is missing → `Failed`.
2. Read BB15 9996 (Uncached, 4 s, cancel the underlying op on timeout).
   - `Success` → `Route = Dcp`, `HasReadOnThisConnection = true`, log the value.
   - Exception or timeout with `!IsConnected` → `LinkDropped`. The receiver's `p()` will be false after the
     drop, and a task may be open: the caller must set `WakeTaskOpen = true`.
   - Timeout while still connected → `NotResponding` (unchanged).
3. **Immediately** cancel the task on the same link: write `02` to **BB15 9995** (DCP mode, same peer that
   read → always answered). Bound it at 2 s. On success clear `WakeTaskOpen`.
4. Wait for 9999 (needed for the 9897 read). Use `BluetoothLEDevice.GattServicesChanged` plus one uncached
   `GetGattServicesForUuidAsync(9999)` per event, with a 4 s overall deadline and one uncached attempt at the
   start. This replaces the 250 ms polling loop (Phase 2 detail; a simple version is acceptable here).
5. Return `ServiceUp`, or `LinkDropped` if the link went down meanwhile.

**Strategy S1 (BB15 has 9896):** stop here. The handshake runs on this same link with split routing: 9897 on
9999, 9896 on BB15. No reconnect.

**Strategy S2 (no BB15 9896):** after step 3, release the link deliberately:
- `Dispose()` the link (it already clears `MaintainConnection` and disposes the services), then
  `WaitForLinkDownAsync(address, type, 3 s)`. The receiver now sees its selected device disconnect, so
  `p()` becomes false (R2).
- Reconnect to the same address (`GattLink.ConnectAsync`, fast) → `Route = Own`. 9999 should already be up.
  Do the whole handshake on 9999.
- If the reconnect finds no 9999 → `Failed` (do not re-wake in a loop; let the attempt loop decide).

### 1.4 Rewrite `ClearWakeTaskAsync`

New behaviour depends on the route, never on device type:

- **Dcp route:** write `02` to BB15 9995 (2 s bound). This is only valid if `HasReadOnThisConnection`;
  otherwise read BB15 9996 first.
- **Own route:** read **9999** 9996 first (R4; 2.5 s bound), then write `02` to 9999 9995 (2 s), then wait
  300 ms.
- Never touch BB15 in the Own route (R1).

Better still: do not call it on reconnect at all. Go straight to the handshake. If the 9897 read says busy
(`state != 0`), the existing busy-clear in `OConnectLanSendAsync` (~line 1000: 9996 read, 9995=02, 9897
re-read) is already the correct Own-route sequence. Make sure it uses `WifiCharForRoute()` /
`CancelCharForRoute()` so it also works in the Dcp route (S1).

### 1.5 Update `ConnectContactsAsync` (`SenderEngine.Contacts.cs:417`)

- Delete `PhoneBeaconType`, `_wakeClearFlipped`, `WakeClearViaDcp`, `WakeClearTimedOut` and every argument
  that passes them.
- Keep the attempt/beacon loop. Per attempt:
  1. Connect (fast, `allowMissingTarget: true`).
  2. If 9999 is present and `!forceWake` → return the link (Own route). If `WakeTaskOpen` is set for this
     device, leave it: the handshake's busy-clear handles it.
  3. Otherwise `WakeOnLinkAsync`:
     - `ServiceUp` → return the link (S1: `Route == Dcp`; S2: `WakeOnLinkAsync` already returned the
       reconnected Own link, so adapt its signature to return the new `GattLink` in S2).
     - `LinkDropped` → set `WakeTaskOpen`, wait for the link to go down (1.5 s), reconnect **once** to the same
       address. Expect 9999 (Own route) and return it. Do **not** wake again on that reconnect.
     - `NotResponding` / `Failed` → keep the current `AvoidAddress` and `consecutiveNotResponding` logic.
- `LinkNotRespondingException.DuringWakeClear` handling: stop flipping. Log it as a bug, because with correct
  routing it should never happen, then keep avoiding the address.

### 1.6 Update the handshake retry loop (`SenderEngine.cs` ~620–700)

- The "state-1 write failed → reconnect without re-waking" branch stays as a safety net. Add a `Log.Warn`
  carrying the link's `Route` so any recurrence is diagnosable. With correct routing it should no longer
  trigger.
- A retry always creates a new link, which starts in the Own route. That is correct (R2).

### 1.7 Fix the receive probe (`ProbeContactsReceiveStatesAsync`, ~248)

It currently calls `WakeOnLinkAsync` as a probe. That wakes the receiver (`p()` true), opens a task, and
leaves the receiver in DCP mode bound to the PC until the link drops. Change it to:
- If 9999 is present → "receiving", and send **no** requests.
- Otherwise do the new wake (it cancels its own task on BB15). Then dispose the link and wait for it to go
  down, so the receiver leaves DCP mode before any real send.
- Keep it skipped while a send or pre-warm owns the radio (unchanged).

### 1.8 Warm links (`StoreWarmLink` / `TakeWarmLink`)

- A parked link keeps its `Route`. In S1 a Dcp-route link is fine to park; its handshake works with split
  routing.
- In S2, links are Own-route by construction.
- `TakeWarmLink` must not reset `Route`.

**Phase 1 checks:**
- The build passes.
- `grep -n "WakeClearViaDcp\|_wakeClearFlipped\|PhoneBeaconType" *.cs` returns nothing.
- Every BB15 read or write call site is reachable only when `Route == Dcp` or inside the wake itself.
- Device test by the user, 10 sends each: cold, warm and screen off, on a phone and a tablet. Expect no
  "timed out" / "not responding" lines, no 30 s gaps in `SendTimeline`, and connect time (send start →
  `handshake-read`) under ~4 s cold and ~1.5 s warm.

---

## Phase 2 — Remove waiting

1. **Service appearance:** finish the `GattServicesChanged`-driven wait in `WaitForOConnectServiceAsync`.
   No fixed-interval uncached polling. At most one uncached discovery per event, and one at timeout. Cancel
   the underlying operation on timeout (existing pattern).
2. **Fixed delays:** remove the `Task.Delay(300)` after the cancel in `ClearWakeTaskAsync` and shorten the
   400 ms after the busy-clear. Keep only what device tests show is needed. Measure with `SendTimeline`
   before and after.
3. **Discovery:** use `BluetoothCacheMode.Cached` first for BB15 and 9999 and fall back to `Uncached` only
   on a miss (`EnumerateDcpAsync` already does this; apply it to the 9999 enumeration on the Contacts path).
4. **Avoid windows:** with deterministic routing, reduce `AvoidAddress(…, 35 s)` after a `NotResponding` to
   what is needed for Windows to drop the link (`WaitForLinkDownAsync` decides). Keep it for genuine
   unanswered requests.

**Check:** the median warm connect is at least 30 % faster than at the end of Phase 1, with no new failures.

---

## Phase 3 — Faster beacon discovery

1. In `PhoneScanner.CreateAndStartWatcher`, check at runtime whether the newer low-latency scan parameters
   API exists (`BluetoothLEAdvertisementScanParameters.LowLatency()` / watcher scan parameters, Windows 11
   24H2+). **UNVERIFIED:** confirm the exact API surface in the Windows SDK this project targets before
   using it, and guard it with `ApiInformation.IsTypePresent`. Never break older Windows.
2. Use low latency only while there is staged content, a pre-warm window or an active send (the
   `KeepWarmWindow` triggers). Fall back to the current settings otherwise, to protect Wi-Fi coexistence.
3. Log the beacon cadence (`DescribeContactsBeacons`) before and after, for comparison.

**Check:** the "max gap" in `DescribeContactsBeacons` drops during sends, and there is no regression on
older Windows (the API is absent → old path).

---

## Phase 4 (optional, long-term) — Native Android path on BB15 only

Only consider this after Phases 1–3 are done and measured. It means doing exactly what an Android sender
does: read 9996 on BB15 (that is the handshake), then the 9990 start command / 9989 request-GO writes on
BB15. The receiver creates a Wi-Fi Direct group, the PC joins it (`OShareWifiDirect.cs`), and the transfer
uses the native protocol. That needs BB15 notification support on Windows (CCCD) and a native sender
implementation. Write a separate design doc first; do not start it inside this task.

---

## 4. Deliverables and process

- One commit per phase, with a clear message. Phase 0 must be merged and device-tested before Phase 1
  starts, because Phase 1's strategy (S1/S2) depends on its results.
- Update the XML comments you touch so they describe the routing model, not the old phone/tablet
  heuristic, and link to this guide.
- Update this guide's "UNVERIFIED" notes with what Phase 0 found.
- If any device result contradicts §1.2, stop, report the log lines, and do not work around it with more
  retries.

---

## Phase 0 results (device logs: phone 17.2.36, tablet 16.10.61)

- **BB15 lists no 9896 and no 9897** (`9998 9996 9995 9981 9990 9989 9986 9997 9985 9892 9891 9890`). Experiment A
  therefore cannot work (S1 is impossible); the §1.2 routing model is **confirmed** by B.
- **B (wake, then 9896 write-with-response on 9999, phone 17.x):** 9897 is read fine, the 9896 write is never answered
  (4 s local timeout; the link only dies ~30 s later at the ATT timeout). Cold send ~42 s.
- **The BB15 9996 wake read never returns `Success`**: it ends in `OperationCanceledException` after ~0.2-0.4 s (the
  receiver restarts its GATT server). The BB15 9995 cancel that follows is answered. Do not use "9996 read succeeded"
  as the signal for DCP mode.
- **C / D (release the wake link, reconnect): impossible.** Windows still holds the LE link 20 s after `Dispose()`, even
  with no pending request and after dropping every WinRT reference and forcing a GC (6 of 6 cold sends). Something
  outside this process keeps it up, so the receiver's selected device never disconnects and `p()` stays true. S2 does not work.
- **Tablet 16.10.61 differs:** after the wake, its 9896 write-with-response on 9999 is answered normally. It needs no workaround.
- **E (wake + BB15 cancel, then 9896 sent as an ATT Write Command, no response) works on the phone:** cold send
  2.7-2.9 s, transfer completed, warm send ~0.7 s. The receiver's handler processes the command; only its reply was
  misrouted. This is what the code now does: a write-without-response when the link did a wake read and the peer's
  9897 `version` is >= 17000000 (`GattLink.WriteOConnectSingle`).
- Trade-off: a write command gives no delivery confirmation; the receiver connecting back to our server is the
  confirmation (already how the flow ends).
- Still open: rare `ObjectDisposedException` on a parked link (retried successfully); Phases 2-3 are unchanged;
  Phase 1's S1/S2 designs are obsolete.
