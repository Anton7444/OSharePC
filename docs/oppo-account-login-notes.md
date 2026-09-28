# OPPO/HeyTap account QR-login — implementation notes

`OppoAccount/` implements the parts of OPPO's account-center QR-login flow that have
been confirmed working end-to-end against the live production API, reverse-engineered
from the real iOS "OPPO Share" app:

- `OppoAccountEnvelope.cs` — request/response body encryption (RSA-OAEP/SHA-1 wrapped
  AES-128-CTR key/IV).
- `OppoAccountSigning.cs` — the `X-Sign` request-signing scheme (HMAC-SHA1).
- `OppoAccountClient.cs` — HTTP client exposing:
  - `GenerateQrCodeAsync` — generate a fresh login QR, no phone/app needed.
  - `CheckQrCodeAsync` — poll QR status (`INITIAL` → `SCANNED` → `CONFIRMED`).
  - `AuthnCheckAsync` — exchange a confirmed `qid` for a `processToken` (single-use).
  - `AuthnValidateAsync` — validate that `processToken`.
  - `VerificationListAsync` — list available 2FA methods (password/SMS/email/etc.)
    for the current login attempt.

## What's NOT implemented yet

Everything past "list the 2FA methods" is still being reverse-engineered:

- Submitting an OTP/password to actually pass the 2FA round (`/api/verification/check`
  — schema is a best guess, unconfirmed against the real server).
- The final step, which happens entirely in the phone app's **native** code (not JS):
  a `vip.onFinish` bridge call hands a `ticket` to native code, which then makes an
  undiscovered `completion/v1/redirect-judge` call that presumably yields the actual
  session/ssoid token. This has not been captured yet — see the private
  `OPPO_ACCOUNT_API_FINDINGS.md` working notes (not in this repo) for the full
  investigation log.

## The BLE "same account" beacon (`OppoAccount/OppoAccountBleAdvertiser.cs`)

Separately from the cloud login above, `OppoAccountBleHash.cs` +
`OppoAccountBleAdvertiser.cs` implement the actual mechanism OPPO/OnePlus phones use to
flag a discovered peer as "same account" (skipping the manual accept tap) — reverse
engineered from a decompiled OnePlus Share Android APK, not the iOS app. See
`k9/c.java`, `com/oplus/oshare/ble/impl/o.java` (method `q`), `com/oplus/oshare/utils/
AccountManger.java` (`D`) and `com/oplus/oshare/utils/b0.java` (`j`/`n`) in that
decompile for the source this was ported from.

The scheme needs a real, logged-in account's **ssoid** (not obtainable yet from this
repo's own login flow — see above — but confirmed extractable from the real app's own
iOS Keychain once you've logged in there once; `POST /api/oppo-account/ble-advertise/
start` takes it as a plain string until this repo's own login flow reaches that far
independently). Given that ssoid, the advertiser broadcasts two small BLE service-data
blocks (UUIDs `0x3333`/`0x6667`) containing this PC's own persisted 6-byte device id and
a 3-character `accountId` value any peer holding the same ssoid can independently
recompute and match. It does not touch or spoof any other device — it only makes this
PC broadcast something a genuinely-same-account phone will recognize as such.

Untested against a real phone so far (no live device availed during this pass) — the
crypto has been cross-checked bit-for-bit against an independent Python re-implementation
of the same algorithm, but the actual BLE wire format (two separate legacy advertising
PDUs standing in for what real devices split across ADV_IND + SCAN_RSP) has not been
verified with a live scanner.

This uses OPPO's private, undocumented API and an app-signing secret extracted from
their shipped client. It's likely against their ToS and could change or break without
notice on any app update. Only use with your own account; keep any UI entry point for
this behind a clearly-labeled experimental/advanced setting.
