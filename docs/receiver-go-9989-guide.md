# Option B — Receiver-hosted group via 9989 (guide for an AI agent)

Idea: the native path failed because the receiver cannot P2P-join a Windows Mobile Hotspot. So reverse the roles.
The PC asks the receiver to **create its own Wi-Fi Direct group** (9989, "request create GO"). The PC joins that
group as an ordinary Wi-Fi station (Windows can do that: `OShareWifiDirectConnection` / `WifiJoiner` already join
an Android group when receiving). The files then go over the native MINA protocol with **the receiver as TCP server
and the PC as client**.

Everything below comes from OnePlus Share 17.2.36 (jadx; obfuscated names cited). **Phase 0 is a go/no-go gate. Do
not write transfer code before it passes**, because two receiver behaviours found in the APK may block this option
entirely.

The user's phone (17.2.36) and tablet (16.10.61) are on ADB with root. You may drive the tablet screen and read
logcat. The PC's internet is on Ethernet, so its Wi-Fi adapter is free to join the group.

---

## 1. Protocol facts

### 1.1 BLE part (BB15, after the usual 9996 read)

- **Prerequisite:** read 9996 on BB15 first. Reuse `NativeBleHandshake`'s wake → 9995=02 → re-enumerate → 9996
  read. A write is only accepted from the device that did the read on the current connection.
- **Write 9989 on BB15** (`w$e.onCharacteristicWriteRequest`, branch `k9.c.f19004e`). The payload is `&`-joined:
  ```
  [0] version   (int; send 11 — the receiver stores it in w.C, and C >= 3 selects the long reply format)
  [1] anything  (Android sends its BT MAC without colons: j.e2 builds "11&<btmac>&1")
  [2] "1"       = we support a random port (w.F) → the reply carries a port
  ```
  The write is answered through `o1()`, so on BB15 while in DCP mode.
- **Receiver behaviour:**
  - **BLOCKER 1:** `if (t0.s(ctx)) { log "5G device should not go"; respond; return; }`. `t0.s` =
    "is5GBandAvailable" (`sapP2pConcurrentSupported` is 2 or 7, or the fallback check). **On receivers with 5 GHz
    SAP/P2P concurrency, the 9989 request is refused** and no group is created. Modern phones and tablets probably
    report this.
  - Otherwise: `w.f9979x.b(w)` → `OShareServer$j.b()` creates the group (`r9.b`, `TransferAccessoryManager.w0()`),
    waits up to 2 s for the group frequency, then calls `w.f1(mac, freq, ssid, psk)`.
- **The reply is a notification** (`w.f1` → `y0()`):
  - **BLOCKER 2:** `y0()` uses `this.f9974s.notifyCharacteristicChanged(...)`. That is OShare's **own** GATT server,
    the **9999** service's 9989 characteristic (`this.f9975t.getCharacteristic(...)`), sent to the selected device.
    It is **not** sent through DCP/BB15. So the PC must have **subscribed (CCCD) to 9989 on the 9999 service**, and
    that requires 9999 to be up and to expose a CCCD on 9989 to Windows. Earlier logs show 9898 on 9999 has no CCCD
    visible to Windows, so this may fail the same way.
  - The CCCD write itself goes to `onDescriptorWriteRequest`, which answers with `f9974s.sendResponse`, i.e. it is
    always answered on 9999.
  - Reply format for `C >= 3` (`w.I0`), plain text, **not encrypted**:
    ```
    <goMacNoColons>3&<freqMHz>&<ssid>&<passphrase>[&<port>]
    ```
    It is sent in **at most three notifications of 22 characters each** (`f1` slices `i*22`). Anything beyond 66
    characters is silently dropped, so long SSIDs may truncate. The Android sender concatenates notifications until
    it has at least 45 characters (`j.onCharacteristicChanged`, `f19004e`).
  - For `C < 3` the reply is `<mac>3&<freq>` only (`H0`) — useless here.

### 1.2 Wi-Fi part

- The SSID is an Android P2P group name (`DIRECT-xx-…`) with a WPA2 passphrase. Join it as a legacy station with a
  temporary WLAN profile. Reuse `WifiJoiner.ConnectAsync(ssid, psk, …)` and its profile cleanup, and
  `OShareWifiDirectConnection` as a reference. On Ethernet, the PC keeps its internet.
- The GO IP is the joined network's gateway (Android groups usually use `192.168.49.1`). Read it from the adapter's
  DHCP gateway; do not hard-code it.

### 1.3 TCP part — roles reversed

In this flow the receiver is GO, so on the receiver `r9.b$c.d(isGo = true, …)` creates `mb.n` (the **server**) and
`MessageManager.p(server)`. The receiver listens on the port from the reply (or 8959 when there is none) for
commands and on **port+1** for data. The PC is the **client** (Android's `mb.j` with `R(isSend = true)`):

1. Open **two** TCP connections to `goIp:port`. On one of them, send `cmd 0, arg null, flag 1` first; that is the
   async/command session the server will use (`mb.n$c.messageReceived`). Send our commands on it with `flag 1`
   (client async).
2. Open data connection(s) to `goIp:port+1`. The PC is the sender, so it **writes** file headers + bytes there
   (Android `mb.j.Q` → `mb.b`). One connection is enough for v1.
3. Then the same command dialog as `docs/native-bb15-path-guide.md` §1.5:
   - receiver sends `cmd 11`;
   - we send `cmd 1` (offer, same fields, same account-proof rules);
   - receiver answers `cmd 2/3/4/6`;
   - we stream the files on port+1;
   - we send `cmd 5` + `cmd 13`;
   - receiver sends `cmd 5`;
   - plus heartbeats both ways.
   **VERIFY the role assumption** (which side sends `cmd 11`, which side sends `cmd 1`) on device. Who initiates the
   offer depends on the task roles, not the socket roles. Read the receiver-side logs (`P2pClient`, `FileServer`)
   during the first run.

The existing `NativeFrame.cs` codec is reusable unchanged. `NativeTransferServer` needs a **client-mode** sibling
(`NativeTransferClient.cs`), or a refactor into a role-agnostic session plus connect/listen front-ends.

---

## 2. Phase 0 — Go/no-go checks (no transfer code)

Do these with a tiny dev command (bridge endpoint or `--experiment` switch) plus ADB logcat on the receiver
(`adb logcat -s BleServer OShareServer OSdkUtils P2pClient`). Test the phone and the tablet.

1. **5 GHz gate:** after the 9996 read, write `11&<12hex>&1` to BB15 9989. On the receiver, look for
   `is5GBandAvailable, sapP2pConcurrentSupported = N` and `5G device should not go`.
   - If it appears on **both** devices → **Option B is blocked**. Stop and report, with the log lines.
   - If it appears on one device only → continue for the other and record which.
2. **9999 9989 CCCD:** after the wake, enumerate 9999 (cached first, uncached on a miss). Check that 9989 exists
   there with `Notify`/`Indicate` properties and a CCCD descriptor. Write the CCCD (notify) **before** the 9989
   write and log the result. No CCCD visible → blocked (same as 9898). Record the properties.
3. **Reply:** with the subscription in place, perform the 9989 write. Log every notification received on 9999 9989
   (mask the passphrase) and the concatenated result. Expect `mac3&freq&ssid&psk&port`.
4. **Join:** join the SSID with the passphrase via `WifiJoiner`. Log the association time and the gateway IP.
   Check from the receiver's logs that it saw the client.
5. **TCP reachability:** connect to `gateway:port` and `gateway:port+1` and log success. Then close everything and
   write 9995=02 on BB15 so the receiver drops its task.

Write the results into this guide under "Phase 0 results", with GO / NO-GO per device. **Only continue with a GO.**

---

## 3. Phase 1 — Client-mode transfer + loopback self-test

- Implement `NativeTransferClient` (connect ×2, hello `cmd 0 flag 1`, data connection, heartbeat answer and
  initiation on the command session, command dispatch, file writer on port+1, cancel `cmd 7` → wait for `cmd 8`).
- Extend `NativeSelfTest` with a fake **server-side receiver** (listens; expects the hello; sends `cmd 11`; answers
  `cmd 1` with `cmd 2`; reads and verifies the files on port+1; sends `cmd 5`). Cover reject and cancel too.
- Check: the build passes and `--selftest` passes repeatedly.

## 4. Phase 2 — Orchestration

`SenderEngine.ReceiverGo.cs`, selected by `contactsSendPath = ReceiverGo`, default off. Sequence:
1. Beacon connect → 9996 read → subscribe to 9999 9989 → write BB15 9989 → collect the reply (timeout 8 s).
2. Join the Wi-Fi (timeout ~10 s).
3. TCP client → offer → accept → stream → complete.
4. Clean up in `finally`:
   - close the TCP connections;
   - disconnect and delete the temporary WLAN profile (`WifiJoiner.RestoreAdapter`);
   - write 9995=02 if not completed;
   - dispose the BLE link.

**Fallback:** any failure **before the join completes** → run the normal OConnect path within the same send.
Timeline marks: `rgo-9996-ok`, `rgo-cccd-ok`, `rgo-9989-written`, `rgo-reply`, `rgo-joined`, `rgo-tcp`, `rgo-ack`,
`rgo-done`.

## 5. Phase 3 — Device validation

Drive sends yourself (bridge API + ADB for the receiver's accept card, `uiautomator` to find the button). Cover:
1 small file, a 1 GB file, 20 files, reject, cancel, cold and warm. Compare `send start → first byte` with the
OConnect path. Keep `ReceiverGo` the default only for devices that passed Phase 0, and only if it is at least as
reliable over 20 sends.

## 6. Hard rules

1. On BB15, only the 9996 read, the 9995 write and the 9989 write. The only 9999 operation is the CCCD write on
   9989 (always answered by the receiver's own server). Never send 9999 reads or writes that `o1()` would route
   elsewhere (`docs/contacts-connect-fix-guide.md` §1.2).
2. Every GATT and WinRT call is bounded and cancels the underlying operation on timeout.
3. Never leave the PC joined to the receiver's group or the temporary profile behind. Clean up in `finally` and at
   startup (`WifiJoiner.CleanupStaleProfiles`).
4. Never log the passphrase or keys.
5. If Phase 0 says NO-GO, stop. Do not work around BLOCKER 1 or 2 with retries.

---

## Phase 0 verdict (2026-10-24): **NO-GO on both of the user's devices — BLOCKER 1 fires**

- Phone (17.2.36): its own log prints `OShare_OSdkUtils: getSapP2pConcurrentSupported, result = 7` twelve
  times → `s0.s()`/`t0.s()` sees 7 → is5GBandAvailable → the 9989 handler logs "5G device should not go" and
  refuses before creating any group.
- Tablet (16.10.61): `getSapP2pConcurrentSupported, result = -1` → the fallback in `lb.s0.r()` calls
  `WifiManager.is5GHzBandSupported()`; the tablet is associated to the home AP on 5240 MHz, so the fallback is
  true → same refusal. Its log also tags the PC as `is5G=true` on every BLE connect.

Per this guide's rule 5 (§6): NO-GO → stop. No 9989 code was written. Revisit only with a receiver that reports
sapP2pConcurrentSupported ∉ {2, 7} AND fails the 5 GHz fallback — i.e. a 2.4 GHz-only device.
