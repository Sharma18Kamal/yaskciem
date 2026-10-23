# Read-only web operations console (P2.1)

The TypeScript console is served by the existing License Server at `/console/`.
It reads the v1 organization, workspace, scan summary, findings, governance report
and (where authorized) scan-job APIs. It has no database connection, operational
write client, ticket action, scan trigger, review decision or account mutation.

## Build and publish

Requires Node.js 24.22.1+ (Node.js 24 LTS recommended) and the existing .NET 9 server prerequisites.
CI and staging package builds select the latest available Node.js 24 patch.

```powershell
npm ci --prefix .\src\CloudEntitle.WebConsole
npm test --prefix .\src\CloudEntitle.WebConsole
npm run build --prefix .\src\CloudEntitle.WebConsole
dotnet publish .\src\CloudEntitle.LicenseServer\CloudEntitle.LicenseServer.csproj -c Release
```

The frontend builds into the server's ignored `wwwroot\console` directory.
Build before publishing so standard ASP.NET content discovery includes the assets.
For a single clean-checkout publish command, use:

```powershell
dotnet publish .\src\CloudEntitle.LicenseServer\CloudEntitle.LicenseServer.csproj -c Release -p:BuildWebConsole=true
```

This opt-in MSBuild property restores the locked npm dependencies, type-checks,
builds and includes the console content. Normal Desktop/server builds do not
require Node. CI runs the frontend tests and production build.

## Entra sign-in and server configuration

1. Use an Entra **SPA/public client** registration in the organization's directory.
   No browser client secret is needed or supported.
2. Add the exact SPA redirect URI
   `https://<api-host>/console/auth.html`. Allow popups for sign-in/sign-out.
   Do not enable the implicit token flow; MSAL uses authorization code with PKCE.
3. Grant this SPA delegated access to the existing API registration's
   `access_as_user` scope and provide consent as required by organization policy.
   The API must continue validating its own audience, issuer, `tid`, `oid` and scope.
4. Set these server settings through the existing configuration provider:

```text
WebConsole__Enabled=true
WebConsole__ClientId=<SPA registration client GUID, not a secret>
WebConsole__ApiScope=api://<API application ID>/access_as_user
WebConsole__StaleAfterHours=72
AzureAd__TenantId=<Entra directory GUID>
```

Use the actual exposed API scope URI if the API has a custom HTTPS application ID
URI. `AzureAd:ClientId` remains the **API** application's ID; do not replace it
with the SPA client ID. The console initially targets one explicitly configured
Entra directory; `common`/`organizations` authorities are not accepted by its
configuration validation. The existing API's authorization policies are unchanged.

Enable the console only after publishing its files. Missing files or invalid
enabled configuration fail startup explicitly; disabled is the default and
all `/console` routes then return 404. Serve at the origin root over HTTPS;
subpath hosting is not supported in this version. Configure any trusted reverse
proxy and TLS termination according to the existing server deployment policy.
Do not add permissive CORS: the console calls only the same origin.

`GET /console/config` exposes only the public SPA client ID, directory ID,
delegated scope and freshness threshold. It never exposes API/connector secrets.
Console responses use `no-store`, local bundled scripts/styles, restrictive CSP,
and popup-compatible opener policy. Existing API security headers remain intact.
MSAL keeps tokens in memory (no persisted access-token cache); refresh/reopening
the page requires sign-in. No application data or tokens are stored in localStorage.
Entra may maintain its own browser session.

## Permissions and data interpretation

- Organization discovery and workspace permissions come from existing membership
  APIs. The browser permission map only hides/gates reads; **the server remains
  the authority** for every request.
- Scan summaries/bindings: `ViewInventory`; finding records: `ViewFindings`;
  governance report: `ViewFindings` or `ManageRemediations`.
- Scan-job reads retain the API's existing `RunScan` requirement. A Viewer sees
  an unavailable job-state label rather than receiving a new permission.
- Custom-role denied reads, authentication failures, missing data and API outages
  remain explicitly unavailable, never successful empty-data fallbacks.
- Organization changes, refresh, filter changes and sign-out clear finding
  evidence. Organization changes/sign-out cancel outstanding reads and clear the
  previous organization's posture. No sign-in tokens appear in URLs or logs.
- Existing organization discovery/workspace GETs can accept a matching pending
  invitation using their established API behavior. The console adds no membership
  mutation endpoint and does not invoke lifecycle-changing review reads.
- Freshness is measured from snapshot completion. Exactly 72 hours is stale by
  default; partial/unavailable source coverage is labeled separately. Future or
  invalid completion timestamps are unavailable. Refresh updates the view; it is
  not a live-subscription dashboard.
- Coverage is scan/provider-level for multi-account scans. It must not be read as
  a per-account source attestation. Bound accounts without a loaded scan stay
  visible as unavailable.
- Findings use the latest available snapshot for **each** account, not just the
  organization's most recent scan. Evidence and status are captured snapshots,
  not live access checks. Malformed payloads remain visible with unavailable
  parsed evidence. Evidence is text, never interpreted as HTML.
- The scheduled-scan executor does not yet run the Desktop risk-evaluation
  pipeline. Its zero finding totals are **not a clean risk assessment**, and
  the console explicitly warns about this limitation.
- History shows per-scan finding/critical/high counts and bars. Snapshot-count
  changes are shown only for consecutive matching account scopes with complete,
  identical source sets. They are not drift events or proof of identical risk
  evaluation profiles. Mixed-scope totals are unavailable under narrower filters;
  the UI never attributes an entire multi-account scan total to one account.
- Review backlog uses the central report's unresolved/overdue/expired state.
  Unknown account attribution remains visible under All accounts, not under a
  specific bound account. Completed work is excluded from the backlog table.

## Initial scale bounds and verification

The v1 history APIs are limit-based, without cursor pagination. The console
requests the latest 200 summaries and 100 jobs, loads at most 20 distinct latest
finding snapshots per selection (four concurrent reads), and caps each finding
read at 1,000 records. Cap warnings are explicit; these are **loaded-view**
counts, not complete organization totals. Narrow account/provider filters to
inspect accounts outside the initial 20-snapshot view. Accounts older than the
200-summary window require API/history work before a complete historical view.
The console does not invent offset pagination or bypass the API to query SQL.

Run `npm test` in the frontend for permission gating, filtering, evidence safety,
72-hour boundaries, missing/partial data, scoped trends, cancellation and no-write
checks. The integration tests cover hosting/configuration/CSP and real
organization API read authorization in isolated PostgreSQL schemas. A real
Entra sign-in/consent smoke test is still required in the deployment directory;
automated tests do not contact Entra or cloud providers.
