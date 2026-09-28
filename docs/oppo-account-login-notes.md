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

This uses OPPO's private, undocumented API and an app-signing secret extracted from
their shipped client. It's likely against their ToS and could change or break without
notice on any app update. Only use with your own account; keep any UI entry point for
this behind a clearly-labeled experimental/advanced setting.
