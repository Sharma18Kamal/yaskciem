# Licensing

The application implements a secure licensing model with a 7-day trial and server-controlled validation.

## Goals

- Trial starts only when the server approves it
- Device and user identifiers are hashed before storage
- Local time tampering does not bypass the trial
- Offline grace period is allowed only for a controlled window
- License validation happens over HTTPS with signed tokens and server verification
