# Option A — Make the OConnect Contacts path fast on every device (guide for an AI agent)

Context: the native BB15 path (`docs/native-bb15-path-guide.md`) was proven unworkable on Windows, because the
receiver can only P2P-join, and Windows Mobile Hotspot never accepts that. The OConnect path already delivers files.
Measured on the user's devices:

| Device | OShare | Send start → transfer | Where the time goes |
|---|---|---|---|
| Phone | 17.2.36 | ~2.4 s (cold ~2.7–2.9 s with the 9896 Write Command fix) | fine |
| Tablet | 16.10.61 | **~16.8 s** | **~15.7 s waiting for a pre-warm of the same tablet to finish** |

Goal of this guide: **every Contacts send, to any device, reaches `handshake-read` within ~3 s of the user pressing
Send, cold or warm, with no 15–40 s outliers.** Do not touch the transfer itself (WebSocket/ZIP), the account proof
or the Everyone-mode path.

The user's phone and tablet are connected over ADB with root. You may drive the tablet's screen (`adb shell input`,
`uiautomator dump`, `screencap`) and read its logs. Drive sends yourself through the bridge API. Do not ask the user
to tap things unless it is unavoidable.

---

## 0. Before you start

1. Make sure the working tree contains the **9896 Write Command fix** from branch `ccr-ca8f1e8e-408fu8` (commits
   `7d94e11`, `8310167`: `GattLink.WriteOConnectSingle` writes without response to receivers whose 9897 `version` is
   >= 17000000). If it is missing, merge that branch first.
2. The native-path code from the previous session may be uncommitted on the user's machine. Leave it alone, keep
   `contactsSendPath = OConnect`, and do not mix its changes into this work's commits.
3. Read `SenderEngine.Contacts.cs` and the Contacts part of `SenderEngine.SendToAsync` (`contactsPath` block, ~line
   440–500, and the handshake retry loop ~620–700) completely before changing anything.

---

## 1. How it works today, and why the tablet waits

- **Staging files** calls `PrewarmContactsDevices()`. The keeper loop (`KeepContactsWarmAsync`) runs `PrewarmOneAsync`
  per Contacts device. That method takes `_contactsGate` with `WaitAsync(0)`, sets `_prewarmDeviceKey`, and runs
  `ConnectContactsAsync(device, …, firstBeaconWait: 20 s)` under a **60 s** CTS. The resulting link is parked in
  `_warmLinks` (`StoreWarmLink`, which relaxes the connection parameters).
- **Send** (`SendToAsync`, `contactsPath`) calls `TakeContactsGateAsync(ct, device.LanPdid)`:
  - If a pre-warm **of the same device** is running, it waits up to **25 s** for the gate.
  - After that, it cancels the pre-warm and waits up to another **15 s**.
  - Then it calls `TakeWarmLink` (taken only if younger than 30 s), else `ConnectContactsAsync(…, 75 s)`.
- So the send's latency equals **whatever the pre-warm is currently doing**: beacon wait, wake dance, a stale-link
  wait of up to 32 s, an avoided-address back-off, and so on. The 15.7 s is the remainder of a pre-warm, not of the
  send itself.
- After every Contacts send, `PrewarmContactsDevices()` runs again (1 s settle), which can collide with the receiver
  right after a transfer.

---

## 2. Phase 0 — Measure, do not guess (mandatory)

1. Add `SendTimeline` marks inside the pre-warm, the same marks the send uses: `beacon`, `device-opened`, `link-up`,
   `wake-read`, `wake-service-up`, `wake-task-cleared-*`, `stale-link-dropped`. Most already exist inside
   `GattLink` and `ConnectContactsAsync`; make sure `PrewarmOneAsync`'s timeline actually records them and is printed
   on success **and** on failure.
2. Add a mark `send-waited-for-prewarm` with the wait duration in `TakeContactsGateAsync`.
3. Add a scripted test driver (a dev-only bridge endpoint, or a PowerShell script in `scripts/`) that:
   - stages a small file;
   - sends to a chosen Contacts device through the bridge API;
   - waits for completion;
   - records the timeline and the `sender.log` slice.
   Run it N times in each scenario:
   - **cold**: force-stop OShare on the receiver first (`adb shell am force-stop com.coloros.oshare`, or the
     package the device actually uses — check with `adb shell pm list packages | grep -i share`);
   - **warm**: send again within 10 s;
   - **after idle**: wait 2 min between sends;
   - **stage-then-send immediately** (Send within 1 s of staging — this is what produced the 15.7 s);
   - **back-to-back to two devices** (phone, then tablet).
4. Pull the receiver's side for the slow runs: `adb logcat -d -s BleServer OShareServer DcpManager` or the OShare
   log file on the device. Note when the receiver saw the connect, the 9996 read, the restart and the 9897 read.
5. Write a table (scenario × device × median/p90 of `send start → handshake-read`, plus the biggest timeline gap and
   its cause) into this guide under "Phase 0 results". **The fixes in Phases 1–4 are prioritised by that table.**
   If a phase targets a bucket that Phase 0 shows to be negligible, skip that phase and say so.

---

## 3. Phase 1 — One connect per device, shared by pre-warm and send

Replace "the send waits for the pre-warm's gate, then maybe cancels it" with **a single in-flight connect operation
per device that both consumers await**.

1. Add the shared operation in `SenderEngine.Contacts.cs`:
   ```csharp
   /// <summary>The connect currently running for a device (pre-warm or send), so a send that starts while a
   /// pre-warm is connecting continues that same attempt instead of waiting for it to finish and then starting
   /// over (the 15.7 s tablet wait).</summary>
   private readonly Dictionary<string, ContactsConnectOp> _connectOps = new();

   private sealed class ContactsConnectOp
   {
       public required Task<GattLink?> Task;
       public required CancellationTokenSource Cts;
       public volatile string Stage = "start";   // beacon | connect | wake | service | clear | done
       public bool ClaimedBySend;                // a send wants the link: do not park it
   }
   ```
2. `GetOrStartConnect(device, purpose)`:
   - returns the existing op if one runs for this `LanPdid`;
   - otherwise starts `ConnectContactsAsync` with its own CTS and registers the op;
   - removes it from the dictionary when it completes.
   `ConnectContactsAsync` updates `op.Stage` at each step (pass the op or a `Action<string>` stage callback).
3. **Pre-warm** uses `GetOrStartConnect(device, Prewarm)`. When the op finishes:
   - `ClaimedBySend` set → hand the link to the send (do not park it, and do not `RelaxConnectionParameters`);
   - otherwise park it as today.
4. **Send**:
   1. A fresh warm link → use it (unchanged).
   2. Else `op = GetOrStartConnect(device, Send)`, set `op.ClaimedBySend = true`, and await `op.Task` under the
      send's own token. Do **not** cancel a running op on the same device; it is doing exactly what the send needs.
   3. If the op fails or returns null, the send starts **one** new op itself (`forceWake: false`) and keeps the
      existing retry behaviour.
5. The radio rule stays: never two connects to *different* devices at the same time. Keep `_contactsGate` for that,
   but hold it per op (taken when an op starts, released when it ends). A send to device X cancels a pre-warm op of
   device Y immediately, as today.
6. Remove the 25 s / 15 s waits in `TakeContactsGateAsync` that become dead code. Keep a hard upper bound on the
   send's total wait (75 s, as today) via its own token.

**Check:** the stage-then-send-immediately scenario no longer has a `send-waited-for-prewarm` gap. The send's
duration ≈ the remaining part of a single connect.

---

## 4. Phase 2 — Shrink the connect itself (driven by the Phase 0 table)

Do only the items whose bucket Phase 0 shows to be large.

### 2a. Beacon wait
- If the device was connected successfully in the last few minutes, try its **last good address** immediately, in
  parallel with waiting for a fresh beacon. Receivers keep one address for minutes. Take whichever yields a link
  first and cancel the other attempt cleanly. Never start two connects to the *same* address.
- Remember the last good address per `LanPdid` (`address`, `addressType`, `connectedAt`). Invalidate it on an
  "Unreachable" failure.

### 2b. Wake dance on the tablet (16.10.61)
- Phase 0 of the previous guide: the tablet answers its 9896 write-with-response after a wake, and its wake-task
  cancel answers on 9999. Make the per-device behaviour **deterministic from the 9897 `version`** (16.x vs 17.x)
  instead of the phone/tablet heuristic (`WakeClearViaDcp`). Cache the version per `LanPdid` after the first
  handshake.
- Wherever Phase 0 shows a fixed `Task.Delay` or a polling interval on the critical path (300 ms after the cancel,
  400 ms after the busy-clear, 250 ms 9999 polling), replace it with an event or a shorter bound. Measure before and
  after.

### 2c. Stale links after a send
- After a successful send the link is kept as the warm link (good). Make sure the post-send
  `PrewarmContactsDevices()` **does not open a second link** to that same device while the kept link is alive and
  fresh. A second connect to the same address is what produced the "refuses new connections for up to 16 s"
  behaviour.
- If the kept link dropped, wait for `WaitForLinkDownAsync` before reconnecting, as `ConnectContactsAsync` already
  does for avoided addresses.

---

## 5. Phase 3 — Start earlier, stay warm longer (safely)

1. **Earlier trigger:** also call `PrewarmContactsDevices()` when the user **selects or hovers a Contacts device in
   the GUI**, and when the GUI window gains focus with files staged. Add a bridge command for it (the GUI calls it
   from `send_tab.dart` on selection). This hides the whole connect behind the user's own clicks.
2. **Warm link lifetime** (`WarmLinkLifetime = 30 s`): parked links went dead silently at 47 s and 182 s, and the
   first request on a dead link costs a 30 s ATT timeout. Do **not** add GATT liveness probes; any unanswered
   probe causes that stall. Instead:
   - keep 30 s as the "use without checking" window;
   - for 30–90 s old links that still report `ConnectionStatus == Connected`, use them, but run the 9897 read with
     the existing 1.5 s bound and, on failure, fall back immediately to a fresh op (Phase 1) instead of the 3-try
     handshake loop.
   Measure the hit/miss rate in Phase 0 style before keeping this.
3. **Keep-warm window** (`KeepWarmWindow = 60 s`): extend it to the time files stay staged (until cleared or sent),
   capped at ~5 min, but still only **one connect per trigger**, as today. Never reconnect in a loop on your own.

---

## 6. Phase 4 — Verification and wrap-up

- Run the Phase 0 driver again on all scenarios, 20 sends each on the phone and the tablet. Acceptance:
  - p90 `send start → handshake-read` ≤ 3 s warm and ≤ 4 s cold;
  - no send > 10 s;
  - no new failures compared with Phase 0.
- `--selftest` and `--mockphone` pass. Add a unit-level test for the shared op (two consumers, one completes,
  cancellation of a different-device op).
- Update this guide with the before/after table, and update the XML comments you touched so they describe the
  shared-op model.
- Commit per phase on the designated branch, with no model identifiers.

## 7. Hard rules

1. Never send a GATT request the receiver will not answer (see `docs/contacts-connect-fix-guide.md` §1.2). Every
   unanswered request costs 30 s.
2. Never open two links to the same address at once.
3. Every WinRT call is bounded and cancels the underlying operation on timeout.
4. Don't widen scope: no transfer-protocol, UI-layout or Everyone-mode changes beyond the single "pre-warm on
   select" bridge call.
5. If a device result contradicts this guide, stop, record the evidence (timeline, `sender.log`, receiver logcat) in
   the guide, and adjust the plan. Do not stack retries on top.

---

## Phase 0 results (2026-10-24, tablet 一加平板 2 / OShare 16.10.61, build = ccr-ca8f1e8e-408fu8 + timing marks)

Driver: `scripts/contacts-speed-test.py` (bridge API + sender.log timeline parsing). 5 runs per scenario
unless noted; metric = the send's own TIMING marks.

| Scenario | median / p90 total | handshake-read | waited-for-prewarm | Verdict |
|---|---|---|---|---|
| warm | 1.22 / 1.47 s | 0.04–0.35 s | 2–294 ms | PASS |
| cold (force-stop OShare) | 2.80 / 2.95 s | 1.92–2.15 s | 3–6 ms | PASS |
| stage-then-send (<1 s) | 2.54 / 2.65 s | 1.76–1.85 s | 3–6 ms | PASS — the 15.7 s case from the old table is gone on this build (the 9896 write-command fix already removed it) |
| idle 125 s | 5.76 / 12.0 s (n=2) | 4.77 / 10.98 s | = the whole prewarm | **FAIL** (one send > 10 s) |

Cause of the idle bucket (sender.log, TIMING[prewarm]): the first connect to a **fresh beacon (0.5 s old,
same address that works 2 s later)** spends **7698 ms in `discovery-9999-Unreachable`** — the receiver's GATT
stack is mid-idle-restart and answers nothing; the failed connect itself revives it. The retry then needs
`beacon 2063ms + link-up 812ms + discovery 351ms`. No address rotation involved (same MAC all day), so 2a
(last-good-address) is a non-bucket here.

Priorities from the table:
- **Phase 2b' (new): cold-first wake.** When no successful handshake happened recently (idle), don't sit
  through the 8 s 9999 discovery against a half-asleep receiver — connect, enumerate DCP/BB15 (system-hosted,
  answers even mid-restart), do the 9996 wake dance on that link, and let 9999 come up. Expected idle ≈ 3–4 s.
- Phase 1 (shared op): the stage-then-send bucket measures 3–6 ms on this build — the same-device gate wait
  already inherits the prewarm's link. **Skipped as negligible per §2 rule 5** (evidence above).
- Phase 2a (last-good address): skipped — the receiver keeps one address (evidence above).
- Phase 2b delay-trim: the account-proof segment is ~0.5 s — not a large bucket. Skipped.
- Phase 3 keep-warm-while-staged + prewarm-on-select: implemented (small, safe).
