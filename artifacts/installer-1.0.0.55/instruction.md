# YASK-CIEM 1.0.0.55 Installation Instructions

This is a Windows x64 evaluation release signed with the YASK-CIEM development certificate. It is intended for trusted testing, not public production distribution. The certificate expires on September 25, 2027.

## Recommended: MSIX Setup

1. Download `YASK-CIEM-Setup-1.0.0.55.exe` and `yask-ciem-dev.cer` from this release.
2. Verify the public certificate before trusting it:

   ```powershell
   $cert = Get-PfxCertificate .\yask-ciem-dev.cer
   $cert.Subject
   $cert.Thumbprint
   $cert.NotAfter
   ```

   Confirm the subject is `CN=YASK-CIEM` and the thumbprint is `537C64CF83CF2C6C1EBC89553B7B4EDD13E7F104`. Stop if either value differs.

3. Run `YASK-CIEM-Setup-1.0.0.55.exe`. If prompted, type `TRUST` only if you obtained the installer and certificate through a channel you trust and verified the thumbprint above.
4. After setup reports success, launch **YASK-CIEM** from the Start menu.

The self-contained MSIX Setup EXE installs the public certificate into the current Windows user's trust stores as needed, installs the MSIX package, and removes trust entries it added if installation fails. It does not contain the signing private key.

## Install the MSIX Directly

If installing `YASK-CIEM-1.0.0.55.msix` without the Setup EXE, first verify the certificate as above. In PowerShell, trust the verified certificate for the current user, then install the package:

```powershell
Import-Certificate -FilePath .\yask-ciem-dev.cer -CertStoreLocation Cert:\CurrentUser\Root
Import-Certificate -FilePath .\yask-ciem-dev.cer -CertStoreLocation Cert:\CurrentUser\TrustedPeople
Add-AppxPackage -Path .\YASK-CIEM-1.0.0.55.msix
```

Only trust this development certificate on a machine where installing test software signed by it is acceptable. Remove the trust entries after testing if your organization's policy requires it.

## Native MSI

The native MSI and native Setup EXE are framework-dependent; install the .NET 9 Desktop Runtime x64 first. The native Setup EXE requires administrator approval. Windows must trust the verified publisher certificate before installation.

## Cloud Sign-In

Install only the provider CLIs you intend to use, sign in with organization-approved read-only accounts, and then use **Connect Cloud** in YASK-CIEM:

- Azure CLI: `az login`
- AWS CLI v2: `aws configure sso`, then `aws sso login --profile <profile-name>`
- Google Cloud CLI: `gcloud auth login`

YASK-CIEM reuses the CLI sessions and performs read-only discovery. It does not apply cloud permission changes. The current build uses a local trial; online license activation is not available.

## Uninstall

- MSIX: **Windows Settings > Apps > Installed apps > YASK-CIEM > Uninstall**.
- Native MSI: uninstall **YASK-CIEM** from **Windows Settings > Apps > Installed apps**.

Uninstalling the app does not automatically delete local application data. Remove YASK-CIEM data only if you intentionally want to delete scan history, reports, and local workflow records.

