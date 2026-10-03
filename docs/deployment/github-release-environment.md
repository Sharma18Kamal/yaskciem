# GitHub Release Environment

The CD workflow uses a GitHub Actions environment named `release` for release-only signing work. The workflow can reference this environment, but its protection rules and secrets must be configured in the repository or organization settings.

Before dispatching a release workflow:

1. In GitHub, open **Settings > Environments** and create `release`.
2. Configure required reviewers and deployment branch/tag restrictions according to the organization release policy.
3. Add the signing PFX as the environment secret `YASK_CIEM_SIGNING_CERT_BASE64` and its password as `YASK_CIEM_SIGNING_CERT_PASSWORD`. Do not commit the PFX or password.
4. Confirm the signing certificate publisher exactly matches the MSIX manifest publisher (`CN=YASK-CIEM`) or update the package publisher and certificate as an intentional coordinated change.

After candidate validation, the protected workflow job decodes the PFX into the ephemeral runner's temporary directory, calls `scripts/package-msix.ps1`, removes the temporary certificate file, and uploads the signed MSIX and App Installer feed as a 14-day workflow artifact. The PFX and password values are not printed.

The generated feed uses the version-tagged GitHub Release URL for the MSIX and the stable `releases/latest/download/YASK-CIEM.appinstaller` URL for future updates. At this stage the assets are only in the workflow artifact, so the feed cannot install or update until TODO 3 publishes both files as assets on a GitHub Release at tag `v{release_version}`. The workflow does not create or publish that release yet.

If the environment or either secret is missing, packaging stops after candidate validation. Post-publication signature, URL, and version verification remains TODO 4.
