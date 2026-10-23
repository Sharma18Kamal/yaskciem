# Licensing

## Free standalone desktop edition (1.0.0.63 and later)

The standalone desktop is free to use and has no trial expiry or activation
requirement. The header identifies it as **Free standalone edition**. Navigation,
cloud connection and disconnect controls are not gated by a license timer.
Cloud permissions, organization sign-in and authorization checks are unchanged.
Shared hosted-service features still require their separately configured backend.

The desktop no longer registers a trial service, reads Windows MachineGuid or a
network-adapter identifier for licensing, or starts a periodic trial check.
An expired, missing or damaged legacy trial file therefore cannot lock the new
desktop. Existing `%LOCALAPPDATA%\YASK-CIEM\trial-state.json` files are left
untouched, not reset, migrated or deleted. Scan data and settings are preserved.
Older installed versions retain their original trial behavior.

## Retained library and future services

The licensing library and its tests remain in the solution for possible future
editions, but are not referenced by the desktop project. `RemoteLicenseService`
is not an integrated desktop activation path. A paid edition or hosted service
would require a separate approved entitlement model and acceptance testing; do
not advertise subscription enforcement, signed entitlements or offline grace as
operational features.

Microsoft Store listing terms, legal publisher details and pricing must be
configured by the owner. This code change does not publish the app, change a
Store listing or grant access to any cloud account.
