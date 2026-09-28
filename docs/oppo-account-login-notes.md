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

## Where the ssoid is actually used today: the PC→phone accept-skip (`GattLink.cs`)

The concrete reason this repo wants a real ssoid at all is **not** phone→PC discovery
(that direction already has OShare's own accountless "quick save" option) — it's
**PC→phone**: today, sending files from this PC to a phone always makes the phone show
a manual "accept" prompt, even between devices on the same real OPPO account.

`GattLink.OConnectLanSendAsync` already implements most of the stock 互传/OPlus-Connect
BLE protocol's account-challenge exchange (state1/state3 over characteristic `0x9896`,
an `account_id` notify/response round-trip over `0x9898`), but the response it built
was wrong: it just decrypted the phone's challenge and re-encrypted the *same* value
back. Decompiling a OnePlus Share Android APK's own handler for this exact exchange
(`com.oplus.oshare.ble.impl.w#j`) shows the phone does not compare against what it
sent — it compares the decrypted response against `AccountManger.y()`, i.e. **its own
logged-in account's `SHA-256(ssoid)`** (`AccountManger.S`). Echoing the challenge back
can never match that, so the phone always falls through to the manual-accept path
(`com.oplus.oshare.ble.impl.w#A`).

Fixed in `GattLink.cs` + new `OppoAccount/OppoSsoidHash.cs`: when a real `ssoid` is
configured (`SettingsStore.Current.OppoSsoid`, settable via `/api/settings` or the
Settings page's "OPPO account ssoid (advanced)" field), the response now sends
`SHA-256(ssoid)` (hex, matching `AccountManger.S`) instead of echoing the challenge.
This only activates when a real ssoid is present *and* the `0x9898` notification
subscription actually succeeded this session (`GattLink`'s existing `pv=1` fallback,
chosen specifically because some builds expose no CCCD there, stays the default
otherwise — announcing `pv=5` without being able to receive the phone's challenge would
strand the transfer, so a timeout there now falls back to the manual-accept path
instead of throwing).

**Not yet verified against a real phone** — the logic matches the decompiled source
exactly, but no live PC→phone transfer has been run against it yet.

This uses OPPO's private, undocumented API and an app-signing secret extracted from
their shipped client. It's likely against their ToS and could change or break without
notice on any app update. Only use with your own account; keep any UI entry point for
this behind a clearly-labeled experimental/advanced setting.
