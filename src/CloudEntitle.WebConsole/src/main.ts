import "./style.css";
import { ApiClient } from "./api.ts";
import { createSession } from "./auth.ts";
import { Actions } from "./contracts.ts";
import type { ConsoleConfiguration, Filter, Finding, FindingRecord, GovernanceReport, Organization, ReadResult, Scan, ScanJob, Workspace } from "./contracts.ts";
import { accountKey, accountsFor, comparableTotals, coverageFor, coverageState, field, freshness, latestScan, matches, parseFinding, providerName, severity, snapshotDelta } from "./model.ts";

function element<T extends HTMLElement>(id: string): T {
  const node = document.getElementById(id);
  if (!node) throw new Error(`Missing console element: ${id}`);
  return node as T;
}
const organization = element<HTMLSelectElement>("organization");
const provider = element<HTMLSelectElement>("provider");
const account = element<HTMLSelectElement>("account");
const signIn = element<HTMLButtonElement>("sign-in");
const signOut = element<HTMLButtonElement>("sign-out");
const refresh = element<HTMLButtonElement>("refresh");
const message = element("message");
let controller = new AbortController();
let findingController = new AbortController();
let api: ApiClient;
let staleHours = 72;
let workspace: Workspace | undefined;
let scans: ReadResult<Scan[]> = { error: "Not loaded" };
let jobs: ReadResult<ScanJob[]> = { error: "Not loaded" };
let reviews: ReadResult<GovernanceReport> = { error: "Not loaded" };
const filter = (): Filter => ({ provider: provider.value, account: account.value });
const date = (value: string): string => Number.isFinite(Date.parse(value)) ? new Date(value).toLocaleString() : "Unavailable";
const fail = (error: unknown): void => { message.textContent = error instanceof Error ? error.message : "Console unavailable."; };

function text(tag: string, value: string, className?: string): HTMLElement {
  const node = document.createElement(tag);
  node.textContent = value;
  if (className) node.className = className;
  return node;
}

function table(id: string, headers: string[], rows: (string | HTMLElement)[][], empty: string): void {
  const target = element(id);
  target.replaceChildren();
  if (!rows.length) { target.append(text("p", empty, "notice")); return; }
  const grid = document.createElement("table");
  const head = grid.createTHead().insertRow();
  headers.forEach(header => {
    const th = document.createElement("th");
    th.scope = "col";
    th.textContent = header;
    head.append(th);
  });
  const body = grid.createTBody();
  rows.forEach(row => {
    const tr = body.insertRow();
    row.forEach(value => {
      const cell = tr.insertCell();
      if (typeof value === "string") cell.textContent = value; else cell.append(value);
    });
  });
  const scroll = text("div", "", "table-scroll");
  scroll.tabIndex = 0;
  scroll.setAttribute("role", "region");
  scroll.setAttribute("aria-label", headers.join(", "));
  scroll.append(grid);
  target.append(scroll);
}

function options(select: HTMLSelectElement, label: string, entries: [string, string][], preserve = ""): void {
  select.replaceChildren(new Option(label, ""), ...entries.map(([value, name]) => new Option(name, value)));
  select.value = entries.some(([value]) => value === preserve) ? preserve : "";
}

function clear(): void {
  controller.abort();
  findingController.abort();
  workspace = undefined;
  scans = { error: "Not loaded" };
  jobs = { error: "Not loaded" };
  reviews = { error: "Not loaded" };
  element("workspace").hidden = true;
  for (const id of ["posture", "coverage", "findings", "reviews", "trends", "evidence"]) element(id).replaceChildren();
  element("evidence-section").hidden = true;
  provider.disabled = account.disabled = refresh.disabled = true;
}

function configureFilters(): void {
  const accounts = accountsFor(workspace?.bindings ?? [], scans.data ?? []);
  options(provider, "All providers", [...new Set(accounts.map(a => a.provider))]
    .map(p => [String(p), providerName(p)]), provider.value);
  configureAccounts();
  provider.disabled = account.disabled = false;
}

function configureAccounts(): void {
  options(account, "All accounts", accountsFor(workspace?.bindings ?? [], scans.data ?? [])
    .filter(a => provider.value === "" || String(a.provider) === provider.value)
    .map(a => [accountKey(a.provider, a.externalAccountId), `${providerName(a.provider)} / ${a.displayName || a.externalAccountId} (${a.externalAccountId})`]), account.value);
}

function renderCoverage(): void {
  const selected = accountsFor(workspace?.bindings ?? [], scans.data ?? [])
    .filter(a => matches(a.provider, a.externalAccountId, filter()));
  table("coverage", ["Provider / account", "Latest snapshot", "Freshness / completeness", "Sources & evidence", "Latest job (bounded history)"], selected.map(a => {
    const scan = latestScan(scans.data ?? [], a);
    const coverage = scan ? coverageFor(scan, a.provider) : [];
    const sourceDetails = text("div", "");
    coverage.forEach(c => {
      const entry = document.createElement("details");
      entry.append(text("summary", `${c.dataSource}: ${c.status} (${c.records} records)`),
        text("p", c.details || "No additional source evidence."));
      sourceDetails.append(entry);
    });
    if (!coverage.length) sourceDetails.append(text("p", "Source coverage unavailable."));
    const job = jobs.data?.filter(j => j.provider === a.provider && j.externalAccountId === a.externalAccountId)
      .sort((x, y) => Date.parse(y.queuedAtUtc) - Date.parse(x.queuedAtUtc))[0];
    return [
      `${providerName(a.provider)} / ${a.displayName || a.externalAccountId} (${a.externalAccountId})`,
      scan ? `${date(scan.completedAtUtc)} / ${scan.scanId}` : `Unavailable: ${scans.error ?? "no snapshot in loaded history"}`,
      scan ? `${freshness(scan.completedAtUtc, staleHours)} / ${coverageState(coverage)}` : "Unavailable",
      sourceDetails,
      jobs.error ? `Unavailable: ${jobs.error}` : job ? `${job.status} / ${date(job.queuedAtUtc)}` : "Unavailable: no job in loaded history"
    ];
  }), scans.error ? `Scan coverage unavailable: ${scans.error}` : "No bound accounts or scans match these filters.");
}

function renderReviews(): void {
  const items = reviews.data?.items.filter(i => matches(i.provider, i.account, filter())) ?? [];
  element("review-notice").textContent = reviews.error ? `Review backlog unavailable: ${reviews.error}`
    : `Report as of ${date(reviews.data!.asOfUtc)}. ${items.filter(i => !i.completed).length} unresolved / ${items.length} work items in scope. Unknown account attribution is shown only under All accounts.`;
  table("reviews", ["Campaign / item", "Provider / account / team", "Owner / reviewer", "Deadline", "State"],
    items.filter(i => !i.completed).map(i => [
      `${i.campaign} / ${i.itemId}`, `${providerName(i.provider)} / ${i.account || "Unknown"} / ${i.team || "Unknown"}`,
      `${i.owner || "Unassigned"} / ${i.reviewer || "Unassigned"}`, date(i.dueAtUtc),
      i.exceptionExpired ? "Exception expired / unresolved" : i.overdue ? "Overdue" : "Pending"
    ]), reviews.error ? "Unavailable; this does not mean the backlog is empty." : "No unresolved review work in the current scope.");
}

function renderTrends(): void {
  const selected = (scans.data ?? []).filter(s => s.accounts.some(a => matches(a.provider, a.externalAccountId, filter())));
  table("trends", ["Completed / scan", "Snapshot scope", "Quality", "Findings / critical / high", "Snapshot count change (not drift)"],
    selected.map(s => {
      const meter = document.createElement("meter");
      meter.min = 0;
      meter.max = Math.max(1, ...selected.filter(x => comparableTotals(x, filter())).map(x => x.findings));
      meter.value = s.findings;
      meter.setAttribute("aria-label", `${s.findings} findings`);
      const total = text("div", "");
      if (comparableTotals(s, filter())) total.append(text("span", `${s.findings} / ${s.criticalFindings} / ${s.highFindings}`), meter);
      else total.textContent = "Unavailable: scan totals span accounts outside this filter";
      return [
        `${date(s.completedAtUtc)} / ${s.scanId}`,
        s.accounts.map(a => `${providerName(a.provider)}:${a.externalAccountId}`).join(", "),
        `${freshness(s.completedAtUtc, staleHours)} / ${coverageState(s.coverage)}`, total,
        comparableTotals(s, filter()) ? snapshotDelta(s, scans.data ?? []) : "Unavailable: mixed scope"
      ];
    }), scans.error ? `History unavailable: ${scans.error}` : "No scan history in this scope.");
}

function showEvidence(finding: Finding): void {
  const target = element("evidence");
  target.replaceChildren(
    text("h3", field(finding, "title")),
    text("p", `${providerName(finding.provider)} / ${finding.externalAccountId} / ${finding.recordId}`),
    text("p", `Captured ${date(finding.completedAtUtc)} (${freshness(finding.completedAtUtc, staleHours)}). Scan ${finding.scanId}. Evidence is a stored snapshot, not a live access check.`)
  );
  for (const [label, name] of [["Description", "description"], ["Principal", "principal"], ["Resource", "resource"],
    ["Permission", "permission"], ["Evidence", "evidence"], ["Recommendation", "recommendation"], ["Evaluation state", "evaluationState"]]) {
    target.append(text("h4", label), text("pre", field(finding, name)));
  }
  const raw = document.createElement("details");
  raw.append(text("summary", "Stored finding payload (read-only)"), text("pre", finding.payloadJson));
  target.append(raw);
  element("evidence-section").hidden = false;
  target.scrollIntoView({ behavior: "smooth", block: "nearest" });
}

function renderPosture(findings?: Finding[]): void {
  const target = element("posture");
  target.replaceChildren();
  const accounts = accountsFor(workspace?.bindings ?? [], scans.data ?? []).filter(a => matches(a.provider, a.externalAccountId, filter()));
  const stale = accounts.filter(a => {
    const scan = latestScan(scans.data ?? [], a);
    return scan && freshness(scan.completedAtUtc, staleHours) === "Stale";
  }).length;
  const missing = accounts.filter(a => !latestScan(scans.data ?? [], a)).length;
  for (const [label, value] of [
    ["Accounts in scope", scans.error ? "Unavailable" : String(accounts.length)], ["Stale snapshots", scans.error ? "Unavailable" : String(stale)],
    ["No snapshot in history", scans.error ? "Unavailable" : String(missing)],
    ["Loaded findings (not organization total)", findings ? String(findings.length) : "Unavailable / loading"]
  ]) {
    const card = text("div", "", "card");
    card.append(text("span", label), text("strong", value));
    target.append(card);
  }
}

async function loadFindings(): Promise<void> {
  findingController.abort();
  findingController = new AbortController();
  const signal = findingController.signal;
  const org = organization.value;
  const selectedFilter = filter();
  element("evidence-section").hidden = true;
  element("evidence").replaceChildren();
  renderPosture();
  renderCoverage();
  renderReviews();
  renderTrends();
  table("findings", [], [], "Loading finding snapshots...");
  element("finding-notice").textContent = "";
  if (!workspace?.allowedActions.includes(Actions.ViewFindings) || !scans.data) {
    const reason = !workspace?.allowedActions.includes(Actions.ViewFindings)
      ? "ViewFindings permission is required." : `Scan inventory unavailable: ${scans.error}`;
    element("finding-notice").textContent = reason;
    table("findings", [], [], "Findings unavailable; no zero-risk conclusion can be drawn.");
    return;
  }
  const allScans = scans.data;
  const accounts = accountsFor(workspace.bindings, allScans).filter(a => matches(a.provider, a.externalAccountId, selectedFilter));
  const selectedScans = [...new Map(accounts.flatMap(a => {
    const scan = latestScan(allScans, a);
    return scan ? [[scan.scanId, scan] as const] : [];
  })).values()];
  const findings: Finding[] = [];
  const notices: string[] = [];
  if (selectedScans.length > 20) notices.push("Partial view: only the first 20 latest account snapshots are loaded. Narrow the provider/account filter.");
  const snapshots = selectedScans.slice(0, 20);
  // Bound concurrent reads and payload sizes; current v1 endpoints have limits, not cursor pagination.
  for (let offset = 0; offset < snapshots.length; offset += 4) {
    try { await Promise.all(snapshots.slice(offset, offset + 4).map(async scan => {
      const result = await api.read<FindingRecord[]>(`/api/v1/organizations/${org}/findings?scanId=${encodeURIComponent(scan.scanId)}&limit=1000`, signal);
      if (result.error !== undefined) { notices.push(`Snapshot ${scan.scanId}: ${result.error}`); return; }
      if (result.data.length === 1000) notices.push(`Snapshot ${scan.scanId}: 1,000-record cap reached; findings may be truncated.`);
      for (const record of result.data) {
        const latest = latestScan(allScans, { provider: record.provider, externalAccountId: record.externalAccountId, displayName: "" });
        if (latest?.scanId === scan.scanId && matches(record.provider, record.externalAccountId, selectedFilter))
          findings.push(parseFinding(record, scan));
      }
    })); } catch (error) {
      if (signal.aborted) return;
      throw error;
    }
    if (signal.aborted) return;
  }
  if (signal.aborted) return;
  if (findings.some(f => f.payload === null)) notices.push("Some finding payloads are malformed; their evidence is unavailable.");
  element("finding-notice").textContent = [
    "Latest available snapshot per account, including partial/stale snapshots. Finding status is captured, not live.",
    "Scheduled scans currently collect inventory without running the Desktop risk-evaluation pipeline; zero findings is not a clean risk assessment.",
    ...notices
  ].join(" ");
  findings.sort((a, b) => field(a, "title").localeCompare(field(b, "title")));
  renderPosture(findings);
  table("findings", ["Finding", "Severity / status", "Provider / account", "Snapshot", "Evidence"], findings.map(f => {
    const button = text("button", "View evidence");
    button.addEventListener("click", () => showEvidence(f));
    const status = f.payload?.status;
    const statusText = typeof status === "number" ? ["Open", "Resolved", "Accepted", "Ignored"][status] ?? "Unknown" : field(f, "status");
    return [
      f.payload === null ? `Unavailable payload / ${f.recordId}` : field(f, "title"),
      `${severity(f)} / ${statusText}`, `${providerName(f.provider)} / ${f.externalAccountId}`,
      `${date(f.completedAtUtc)} / ${freshness(f.completedAtUtc, staleHours)}`, button
    ];
  }), notices.length || !snapshots.length
    ? "No findings loaded. Data may be unavailable; review coverage and loading notices."
    : "No findings returned in the loaded snapshots; incomplete coverage is not proof of no risk.");
}

async function loadOrganization(): Promise<void> {
  clear();
  if (!organization.value) return;
  controller = new AbortController();
  const signal = controller.signal;
  const org = organization.value;
  const prefix = `/api/v1/organizations/${org}`;
  message.textContent = "Loading organization permissions and posture...";
  try {
    const current = await api.get<Workspace>(`${prefix}/workspace`, signal);
    if (signal.aborted) return;
    workspace = current;
    const inventory = current.allowedActions.includes(Actions.ViewInventory);
    const governance = current.allowedActions.includes(Actions.ViewFindings) || current.allowedActions.includes(Actions.ManageRemediations);
    const results = await Promise.all([
      inventory ? api.read<Scan[]>(`${prefix}/scan-summaries?limit=200`, signal) : Promise.resolve({ error: "ViewInventory permission required" }),
      current.allowedActions.includes(Actions.RunScan) ? api.read<ScanJob[]>(`${prefix}/scan-jobs?limit=100`, signal) : Promise.resolve({ error: "RunScan permission required by the scan-job API" }),
      governance ? api.read<GovernanceReport>(`${prefix}/governance/report`, signal) : Promise.resolve({ error: "Review read permission required" })
    ]);
    if (signal.aborted) return;
    [scans, jobs, reviews] = results;
    configureFilters();
    element("scope-note").textContent = `Role: ${current.currentMember.assignedRoleName}. Stale threshold: ${staleHours} hours. Snapshot history: latest 200 scans; job history: latest 100 jobs. ${scans.data?.length === 200 ? "Scan history cap reached; older accounts/scans may be missing." : ""} Source coverage in multi-account scans is scan/provider-level, not per-account attestation.`;
    element("workspace").hidden = false;
    refresh.disabled = false;
    message.textContent = `Read-only view loaded at ${new Date().toLocaleString()}. Refresh to check for changes.`;
    await loadFindings();
  } catch (error) {
    if (!signal.aborted) { clear(); fail(error); }
  }
}

async function applyFilters(): Promise<void> {
  try { await loadFindings(); } catch (error) { if (!findingController.signal.aborted) fail(error); }
}
organization.addEventListener("change", () => { void loadOrganization(); });
refresh.addEventListener("click", () => { void loadOrganization(); });
provider.addEventListener("change", () => { configureAccounts(); void applyFilters(); });
account.addEventListener("change", () => { void applyFilters(); });

async function initialize(): Promise<void> {
  signIn.disabled = true;
  const response = await fetch("/console/config", { cache: "no-store", credentials: "omit" });
  if (!response.ok) throw new Error("Console sign-in configuration unavailable. Contact the administrator.");
  const config: ConsoleConfiguration = await response.json();
  staleHours = config.staleAfterHours;
  const session = await createSession(config);
  api = new ApiClient(() => session.token());
  signIn.disabled = false;
  message.textContent = "Sign in to review organization posture. No operational write actions are available.";
  signIn.addEventListener("click", async () => {
    signIn.disabled = true;
    try {
      await session.signIn();
      const organizations = await api.get<Organization[]>("/api/v1/organizations");
      options(organization, organizations.length ? "Select organization" : "No organization memberships", organizations.map(o => [o.id, o.name]));
      organization.disabled = false;
      signIn.hidden = true;
      signOut.hidden = false;
      message.textContent = "Select an organization to review posture.";
    } catch {
      clear();
      message.textContent = "Sign-in or organization discovery failed. Check API access, consent and organization membership, then retry.";
      signOut.hidden = false;
    } finally { signIn.disabled = false; }
  });
  signOut.addEventListener("click", async () => {
    clear();
    options(organization, "Sign in to select", []);
    organization.disabled = true;
    signOut.disabled = true;
    try { await session.signOut(); message.textContent = "Signed out. Local posture data cleared."; }
    catch { message.textContent = "Local data cleared; Entra sign-out did not complete. Close this tab and your sign-in window."; }
    finally {
      signOut.disabled = false;
      signOut.hidden = true;
      signIn.hidden = false;
    }
  });
}

void initialize().catch(() => {
  message.textContent = "Console initialization failed. Check server WebConsole configuration, HTTPS and browser support.";
});
