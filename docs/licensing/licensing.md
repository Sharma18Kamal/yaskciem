# Licensing

## Current implementation

- `TrialService` creates a seven-day trial and stores only SHA-256 hashes of the normalized user and device identifiers.
- Trial status checks require the same device identifier used at registration.
- A system clock rollback of more than five minutes expires the local trial and persists that expiration.
- `RemoteLicenseService` requires HTTPS and supports validation, activation, and deactivation requests.

The local trial file is stored at `%LOCALAPPDATA%\YASK-CIEM\trial-state.json`. Local storage and clock checks are deterrents, not tamper-proof controls: a local administrator can modify or remove this file.

## Production enforcement status

The desktop app does not yet register or invoke the licensing services, present a license/trial enrollment UI, or gate application features on an entitlement. The License Server currently exposes health, readiness, and provider-capability routes only. Therefore server-approved trials, license activation, signed entitlement verification, and offline grace are not operational yet; the local trial service must not be treated as production anti-tamper enforcement.

Before production licensing, provide a licensing backend with central trial and activation persistence, implement the validation/activation/deactivation and trial endpoints, sign entitlements server-side, and have the desktop app validate and enforce those entitlements. Keep the signing private key on the backend. Offline use should rely on signed, expiry-bound entitlements and a deliberately configured grace period, not trust local trial dates alone.
