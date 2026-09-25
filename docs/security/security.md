# Security

The application must follow secure-by-default principles for cloud entitlement analysis.

## Requirements

- Never store plaintext AWS secret keys, Azure client secrets, GCP private keys, or passwords
- Prefer Windows DPAPI / Windows Credential Manager for secure local credential storage
- Use HTTPS for all remote license-server communication
- Never log secret material or tokens
- Use Serilog with rolling log files in Logs/application-.log
- Validate inputs, encode output, enforce rate limiting on licensing APIs, and audit key actions
