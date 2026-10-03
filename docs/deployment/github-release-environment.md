# GitHub Release Environment

The CD workflow uses a GitHub Actions environment named `release` for release-only signing work. The workflow can reference this environment, but its protection rules and secrets must be configured in the repository or organization settings.

Before dispatching a release workflow:

1. In GitHub, open **Settings > Environments** and create `release`.
2. Configure required reviewers and deployment branch/tag restrictions according to the organization release policy.
3. Add the signing PFX as the environment secret `YASK_CIEM_SIGNING_CERT_BASE64` and its password as `YASK_CIEM_SIGNING_CERT_PASSWORD`. Do not commit the PFX or password.
4. Confirm the signing certificate publisher exactly matches the MSIX manifest publisher (`CN=YASK-CIEM`) or update the package publisher and certificate as an intentional coordinated change.

The workflow's signing preflight checks that both secrets are available without printing their values. If the environment or either secret is missing, the release job stops after candidate validation. MSIX generation, publication, and post-publication verification are still separate workflow tasks.
