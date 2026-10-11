# YASK-CIEM 1.0.0.56 Native MSI Installation

This is a Windows x64 evaluation release signed with the YASK-CIEM development certificate. It is intended for trusted testing, not public production distribution. The certificate expires on September 25, 2027.

## Before installing

- Install the .NET 9 Desktop Runtime x64.
- Download `YASK-CIEM-1.0.0.56.msi`, `YASK-CIEM-Setup-1.0.0.56.exe`, and the public `yask-ciem-dev.cer` from this release.
- Verify the public certificate before trusting it:

  ```powershell
  $cert = Get-PfxCertificate .\yask-ciem-dev.cer
  $cert.Subject
  $cert.Thumbprint
  $cert.NotAfter
  ```

  Confirm the subject is `CN=YASK-CIEM` and the thumbprint is `537C64CF83CF2C6C1EBC89553B7B4EDD13E7F104`. Stop if either value differs.

If Windows does not trust the verified development certificate, and only if this is an approved test machine, trust the public certificate for the current Windows user:

```powershell
Import-Certificate -FilePath .\yask-ciem-dev.cer -CertStoreLocation Cert:\CurrentUser\Root
Import-Certificate -FilePath .\yask-ciem-dev.cer -CertStoreLocation Cert:\CurrentUser\TrustedPeople
```

The `.cer` contains no private signing key. Never request or distribute the matching `.pfx` file or its password.

## Install

- **Setup EXE:** Run `YASK-CIEM-Setup-1.0.0.56.exe` and approve the Windows administrator prompt.
- **MSI:** Alternatively, double-click `YASK-CIEM-1.0.0.56.msi` and follow the Windows Installer prompts.

Launch YASK-CIEM from the Start menu. The app uses a local evaluation trial; online license activation is not available in this build.

## Optional cloud sign-in

Install only the provider CLI tools you need, authenticate with organization-approved read-only accounts, then connect the provider in YASK-CIEM:

- Azure CLI: `az login`
- AWS CLI v2: `aws configure sso`, then `aws sso login --profile <profile-name>`
- Google Cloud CLI: `gcloud auth login`

Scans use the configured CLI sessions and are read-only.

## Uninstall

Uninstall **YASK-CIEM** from **Windows Settings > Apps > Installed apps**. Uninstalling does not automatically delete local application data.
