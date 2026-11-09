# Azure Blob immutable audit archive

The License Server supports `AuditArchive:Provider=AzureBlob` as an alternative
to `S3ObjectLock`. It uses native Azure Blob APIs, not an S3 compatibility layer.
The current implementation supports Azure public cloud endpoints.

The **archive endpoint** is `https://<storage-account>.blob.core.windows.net`.
The container name is a separate setting. This is **not** the License Server URL:
Desktop's `YASK_CIEM_API_BASE_URL` must still point to the deployed, authenticated
License Server HTTPS API. Configuring an archive does not start that API.

## Provisioning prerequisites

An authorized storage administrator must provision and review these settings.
The application and setup wizard never create containers, enable versioning,
or lock a default retention policy.

1. Use a compatible general-purpose v2 storage account without hierarchical
   namespace, NFS or SFTP. Keep anonymous Blob access and shared-key access
   disabled; require HTTPS and TLS 1.2 or later. Restrict network access to the
   License Server's approved network.
2. Enable Blob versioning. Create a **dedicated private container**, such as
   `ciem-audit`, with **version-level immutability support**. Existing containers
   require Azure's migration procedure; merely enabling versioning is insufficient.
3. Configure a **default time-based retention policy on that container** and,
   after testing and explicit administrator approval, **lock it**. The period
   must be at least `AuditRetention:MinimumRetentionDays` (default 2,555 days).
   Disable both protected-append-write options. Account defaults alone do not
   satisfy the connector's container-policy readiness check.
4. Enable a system-assigned managed identity on the License Server host, or use
   an explicitly selected user-assigned identity. Grant it control-plane reads
   for `Microsoft.Storage/storageAccounts/blobServices/read` and
   `Microsoft.Storage/storageAccounts/blobServices/containers/immutabilityPolicies/read`
   (Reader at storage-account scope provides these reads).
5. Grant container-scoped data-plane blob reads and writes plus
   `Microsoft.Storage/storageAccounts/blobServices/containers/blobs/immutableStorage/runAsSuperUser/action`
   so the worker can upload locked versions and extend their retention. A custom
   role should omit blob deletes and policy deletes. Do not grant account-key
   access or policy-management privileges for changing the container default.
   This immutable-storage permission is powerful; scope it to the dedicated
   container and review it explicitly. Storage Blob Data Contributor alone
   cannot extend blob immutability. The least-privileged built-in role that
   includes that operation is Storage Blob Data Owner, which is broader than
   the custom role recommended here.
6. Retain the independently administered archive PostgreSQL login, audit HMAC
   key ring, network separation and other License Server prerequisites. Blob
   Storage replaces the S3 destination only, not PostgreSQL or integrity checks.

**Warning:** locked retention cannot be shortened or removed while protected
data is retained. Uploads made by this worker also explicitly lock individual
versions. Even test uploads can create long-lived, billable data. Obtain approval
before testing against real storage. Use disposable short-retention test
infrastructure rather than production audit data.

## Server configuration

Merge [the Azure settings example](../../infra/postgres/azure-blob-archive.settings.example.json)
into the existing server configuration, replacing placeholders with actual
non-secret values. Use `Authentication=ManagedIdentity` in production.
`ManagedIdentityClientId` is optional and selects a user-assigned identity.
`Authentication=AzureCli` is allowed only when the server environment is
`Development`; it never falls back from managed identity.

Alternatively, run the existing wizard under the Windows service identity:

```powershell
.\scripts\configure-audit-archive.ps1 `
  -SettingsPath '.\publish\appsettings.Production.json' `
  -Provider AzureBlob
```

The wizard prompts for the endpoint, ARM storage account resource ID, container
and optional managed identity client ID. It stores the archive PostgreSQL
credential in Windows Credential Manager. It does not request or store Azure
storage keys, SAS tokens, or S3 credentials in Azure mode. Reconfiguring an
archive requires an administrator-reviewed data migration; old sealed segments
are not automatically copied from the previous destination.

## Readiness and verification

`/api/v1/readiness` remains unhealthy until the archive worker verifies private
container access, version-level immutability support, account versioning, a
locked container default policy with sufficient retention and no append
exceptions. ARM access failures are failures, not healthy defaults.

Each upload uses `If-None-Match: *` and locked version-level retention. An existing
key or concurrent upload is not overwritten: the worker pins the returned blob
version, checks metadata and length, downloads it to verify SHA256, and extends
locked retention when a retry requires a later date. It confirms retention before
committing PostgreSQL archive progress. Missing versions, altered content,
unlocked/insufficient retention and access failures leave progress uncommitted.
Readiness performs reads only; write/retention permission is demonstrated by
successful archiving, not by a readiness check alone.

The Blob SDK is pinned to 12.25.1 to remain compatible with the repository's
Azure.Identity 1.17.2 dependency. Blob SDK 12.30.1 introduces Azure.Core 1.60.0,
whose identity type exports conflict with that existing dependency.

References:
- [Version-level immutable storage](https://learn.microsoft.com/azure/storage/blobs/immutable-policy-configure-version-scope)
- [Azure Blob storage RBAC](https://learn.microsoft.com/azure/storage/blobs/assign-azure-role-data-access)
- [Set Blob Immutability Policy authorization](https://learn.microsoft.com/rest/api/storageservices/set-blob-immutability-policy)
