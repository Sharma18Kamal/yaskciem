# Licensing

## Current implementation

- The desktop app starts a fourteen-day trial before showing the main window and displays the remaining days in the header.
- The app derives a stable device identifier from Windows `MachineGuid`, with a network-adapter identifier fallback. It uses a synthetic device-based trial ID and does not ask for a username, email address, or phone number.
- Trial status checks use that same identifier. Reinstalling the app for the same Windows account continues the original saved trial instead of starting a new fourteen-day period.
- Trial status is rechecked every 30 seconds while the app is open, so controls are disabled when the expiry is reached without waiting for a restart.
- A system clock rollback of more than five minutes expires the local trial and persists that expiration.
- When the trial expires or trial status cannot be verified, workspace and cloud controls are disabled. Existing local application data is not deleted.
- `RemoteLicenseService` requires HTTPS and supports validation, activation, and deactivation requests, but those remote flows are not yet integrated into the desktop UI.

The local trial file is stored at `%LOCALAPPDATA%\YASK-CIEM\trial-state.json`. The local record survives normal app reinstall under the same Windows account and Windows installation. It is not a centralized or tamper-proof hardware-wide license: a local administrator can modify or remove it, a Windows reinstall can change `MachineGuid`, and another Windows account has a separate LocalAppData store.

## Production enforcement status

The License Server currently exposes health, readiness, and provider-capability routes only. Server-approved trials, license activation, signed entitlement verification, and offline grace are not operational. Before production licensing, provide central trial and activation persistence, implement validation/activation/deactivation endpoints, sign entitlements server-side, and have the desktop app validate and enforce those entitlements. Keep the signing private key on the backend. Offline use should rely on signed, expiry-bound entitlements and a deliberately configured grace period, not trust local trial dates alone.

Before production licensing, provide a licensing backend with central trial and activation persistence, implement the validation/activation/deactivation and trial endpoints, sign entitlements server-side, and have the desktop app validate and enforce those entitlements. Keep the signing private key on the backend. Offline use should rely on signed, expiry-bound entitlements and a deliberately configured grace period, not trust local trial dates alone.
