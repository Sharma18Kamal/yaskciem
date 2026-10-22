import { describe, expect, it } from "vitest";
import type { Scan } from "../src/contracts.ts";
import { accountKey, accountsFor, comparableTotals, coverageState, field, freshness, latestScan, matches, parseFinding, severity, snapshotDelta } from "../src/model.ts";

export const scan: Scan = {
  scanId: "scan-a", completedAtUtc: "2026-10-20T00:00:00Z", provider: "Aws",
  accounts: [{ provider: 1, externalAccountId: "shared-id", displayName: "AWS" }],
  findings: 3, criticalFindings: 1, highFindings: 2, identities: 4, resources: 5,
  coverage: [{ provider: "Aws", dataSource: "IAM", status: "Collected", records: 3, details: "Complete" }]
};

describe("posture contract projections", () => {
  it("marks the exact 72-hour boundary stale and never calls invalid/future dates fresh", () => {
    const now = Date.parse("2026-10-23T00:00:00Z");
    expect(freshness(scan.completedAtUtc, 72, now)).toBe("Stale");
    expect(freshness(scan.completedAtUtc, 72, now - 1)).toBe("Fresh");
    expect(freshness("bad", 72, now)).toContain("Unavailable");
    expect(freshness("2026-10-24T00:00:00Z", 72, now)).toContain("Unavailable");
  });
  it("distinguishes complete, partial, unsupported, unknown and absent coverage", () => {
    expect(coverageState(scan.coverage)).toBe("Complete");
    expect(coverageState([{ ...scan.coverage[0], status: "NoData" }])).toBe("Complete");
    expect(coverageState([{ ...scan.coverage[0], status: "Partial" }])).toBe("Partial");
    expect(coverageState([{ ...scan.coverage[0], status: "Unsupported" }])).toContain("Unavailable");
    expect(coverageState([{ ...scan.coverage[0], status: "Unknown" }])).toContain("Unavailable");
    expect(coverageState([])).toBe("Unavailable");
  });
  it("selects latest per provider/account, preserving accounts missing scans", () => {
    const older = { ...scan, scanId: "old", completedAtUtc: "2026-10-19T00:00:00Z" };
    const bindings = [
      { provider: 1, externalTenantId: "shared-id", displayName: "AWS binding" },
      { provider: 0, externalTenantId: "shared-id", displayName: "Azure binding" }
    ];
    const accounts = accountsFor(bindings, [scan, older]);
    expect(accounts).toHaveLength(2);
    expect(latestScan([older, scan], accounts[1])?.scanId).toBe("scan-a");
    expect(latestScan([older, scan], accounts[0])).toBeUndefined();
    expect(matches(0, "shared-id", { provider: "", account: accountKey(1, "shared-id") })).toBe(false);
  });
  it("does not attribute mixed-scope snapshot totals to a single selected account", () => {
    const mixed = { ...scan, accounts: [...scan.accounts, { provider: 0, externalAccountId: "azure", displayName: "Azure" }] };
    expect(comparableTotals(mixed, { provider: "1", account: "" })).toBe(false);
    expect(comparableTotals(mixed, { provider: "", account: "" })).toBe(true);
    expect(comparableTotals(scan, { provider: "", account: accountKey(1, "shared-id") })).toBe(true);
    expect(comparableTotals({ ...scan, accounts: [] }, { provider: "", account: "" })).toBe(false);
  });
  it("supports legacy PascalCase and structured evidence without evaluating markup", () => {
    const finding = parseFinding({ provider: 1, kind: "finding", recordId: "one", externalAccountId: "shared-id",
      payloadJson: JSON.stringify({ Title: "<img src=x onerror=alert(1)>", Severity: 0, Evidence: { summary: "Captured", details: { source: "IAM" } } })
    }, scan);
    expect(severity(finding)).toBe("Critical");
    expect(field(finding, "title")).toContain("<img");
    expect(field(finding, "evidence")).toContain("Captured");
    const invalid = parseFinding({ ...finding, payloadJson: "bad" }, scan);
    expect(invalid.payload).toBeNull();
    expect(field(invalid, "evidence")).toBe("Unavailable");
  });
  it("compares only consecutive matching scopes with complete matching sources", () => {
    const older = { ...scan, scanId: "previous", findings: 1, completedAtUtc: "2026-10-19T00:00:00Z" };
    expect(snapshotDelta(scan, [older, scan])).toContain("+2 findings");
    expect(snapshotDelta(scan, [])).toContain("no previous snapshot");
    expect(snapshotDelta({ ...scan, coverage: [] }, [older])).toContain("incomplete coverage");
    expect(snapshotDelta(scan, [{ ...older, coverage: [{ ...older.coverage[0], status: "Partial" }] }])).toContain("coverage changed");
    expect(snapshotDelta(scan, [{ ...older, coverage: [{ ...older.coverage[0], dataSource: "Other" }] }])).toContain("coverage changed");
    expect(snapshotDelta(scan, [{ ...older, accounts: [{ ...older.accounts[0], provider: 0 }] }])).toContain("no previous snapshot");
  });
});
