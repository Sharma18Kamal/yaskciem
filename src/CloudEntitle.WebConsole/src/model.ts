import type { Binding, Coverage, Filter, Finding, FindingRecord, Scan, ScanAccount } from "./contracts.ts";

export const providerNames = ["Azure", "Aws", "Gcp", "Kubernetes"];
export const accountKey = (provider: number, account: string): string => JSON.stringify([provider, account]);
export const providerName = (provider: number): string => providerNames[provider] ?? `Unknown (${provider})`;

export function matches(provider: number, account: string, filter: Filter): boolean {
  return (filter.provider === "" || String(provider) === filter.provider)
    && (filter.account === "" || accountKey(provider, account) === filter.account);
}

export function accountsFor(bindings: Binding[], scans: Scan[]): ScanAccount[] {
  const accounts = new Map<string, ScanAccount>();
  for (const scan of scans)
    for (const account of scan.accounts) accounts.set(accountKey(account.provider, account.externalAccountId), account);
  for (const binding of bindings)
    accounts.set(accountKey(binding.provider, binding.externalTenantId), {
      provider: binding.provider, externalAccountId: binding.externalTenantId, displayName: binding.displayName
    });
  return [...accounts.values()].sort((a, b) => a.provider - b.provider || a.externalAccountId.localeCompare(b.externalAccountId));
}

export function latestScan(scans: Scan[], account: ScanAccount): Scan | undefined {
  return scans.filter(scan => scan.accounts.some(a =>
    a.provider === account.provider && a.externalAccountId === account.externalAccountId))
    .sort((a, b) => Date.parse(b.completedAtUtc) - Date.parse(a.completedAtUtc))[0];
}

export function coverageFor(scan: Scan, provider: number): Coverage[] {
  return scan.coverage.filter(c => c.provider.toLowerCase() === providerName(provider).toLowerCase());
}

export function freshness(completed: string, hours: number, now = Date.now()): string {
  const timestamp = Date.parse(completed);
  if (!Number.isFinite(timestamp) || timestamp > now) return "Unavailable (invalid scan time)";
  return now - timestamp >= hours * 3_600_000 ? "Stale" : "Fresh";
}

export function coverageState(coverage: Coverage[]): string {
  if (!coverage.length) return "Unavailable";
  const statuses = coverage.map(c => c.status.toLowerCase());
  if (statuses.some(s => ["unavailable", "failed", "error", "unknown", "unsupported", "notcollected", "not-collected"].includes(s)))
    return "Unavailable / incomplete";
  if (statuses.some(s => !["collected", "complete", "nodata"].includes(s))) return "Partial";
  return "Complete";
}

export function parseFinding(record: FindingRecord, scan: Scan): Finding {
  let payload: Record<string, unknown> | null = null;
  try {
    const parsed: unknown = JSON.parse(record.payloadJson);
    if (parsed !== null && typeof parsed === "object" && !Array.isArray(parsed)) {
      payload = Object.fromEntries(Object.entries(parsed).map(([key, value]) => [key.toLowerCase(), value]));
    }
  } catch {
    // Keep the record visible and label malformed evidence rather than omitting it.
  }
  return { ...record, scanId: scan.scanId, completedAtUtc: scan.completedAtUtc, payload };
}

export function field(finding: Finding, name: string): string {
  const value = finding.payload?.[name.toLowerCase()];
  return value === undefined || value === null || value === "" ? "Unavailable"
    : typeof value === "object" ? JSON.stringify(value, null, 2) : String(value);
}

export function severity(finding: Finding): string {
  const value = finding.payload?.severity;
  return typeof value === "number" ? ["Critical", "High", "Medium", "Low", "Informational"][value] ?? "Unknown"
    : typeof value === "string" ? value : "Unknown";
}

export function comparableTotals(scan: Scan, filter: Filter): boolean {
  return scan.accounts.length > 0 && scan.accounts.every(a => matches(a.provider, a.externalAccountId, filter));
}

export function snapshotDelta(scan: Scan, history: Scan[]): string {
  const scope = (item: Scan): string => JSON.stringify(item.accounts
    .map(a => accountKey(a.provider, a.externalAccountId)).sort());
  const sources = (item: Scan): string => JSON.stringify(item.coverage
    .map(c => [c.provider.toLowerCase(), c.dataSource]).sort());
  if (!scan.accounts.length || coverageState(scan.coverage) !== "Complete") return "Unavailable: incomplete coverage";
  const previous = history.filter(item => Date.parse(item.completedAtUtc) < Date.parse(scan.completedAtUtc)
    && scope(item) === scope(scan)).sort((a, b) => Date.parse(b.completedAtUtc) - Date.parse(a.completedAtUtc))[0];
  if (!previous) return "Unavailable: no previous snapshot in loaded history";
  if (coverageState(previous.coverage) !== "Complete" || sources(previous) !== sources(scan))
    return "Unavailable: coverage changed";
  const delta = scan.findings - previous.findings;
  return `${delta > 0 ? "+" : ""}${delta} findings vs ${new Date(previous.completedAtUtc).toLocaleString()}`;
}
