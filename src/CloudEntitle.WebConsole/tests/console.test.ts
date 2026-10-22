// @vitest-environment jsdom
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, beforeEach, expect, it, vi } from "vitest";

vi.mock("../src/auth.ts", () => ({
  createSession: async () => ({
    signIn: async () => {},
    token: async () => "fixture-token",
    signOut: async () => {}
  })
}));

const org = "00000000-0000-0000-0000-000000000001";
let permissions = [0, 2];
let summaryFailure = false;
let findingFailure = false;
let history: typeof snapshot[] = [];
let recordCount = 1;
let summaryGate: Promise<void> | undefined;
let activeFindingReads = 0;
let peakFindingReads = 0;
let calls: { path: string; method: string }[] = [];
const completed = new Date(Date.now() - 73 * 3_600_000).toISOString();
const snapshot = {
  scanId: "scan-one", completedAtUtc: completed, provider: "Aws",
  accounts: [{ provider: 1, externalAccountId: "aws-one", displayName: "Production" }],
  findings: 1, criticalFindings: 1, highFindings: 0, identities: 1, resources: 1,
  coverage: [{ provider: "Aws", dataSource: "IAM", status: "Partial", records: 1, details: "Activity history unavailable" }]
};

beforeEach(async () => {
  vi.resetModules();
  permissions = [0, 2];
  summaryFailure = findingFailure = false;
  history = [snapshot];
  recordCount = 1;
  summaryGate = undefined;
  activeFindingReads = peakFindingReads = 0;
  calls = [];
  const html = readFileSync(resolve("index.html"), "utf8");
  document.documentElement.innerHTML = html.replace(/<script[\s\S]*?<\/script>/g, "");
  Element.prototype.scrollIntoView = vi.fn();
  vi.stubGlobal("fetch", vi.fn(async (input: string, init?: RequestInit) => {
    const path = String(input);
    calls.push({ path, method: init?.method ?? "GET" });
    if (path.includes("scan-summaries")) await summaryGate;
    if (path.includes("/findings?")) {
      activeFindingReads++;
      peakFindingReads = Math.max(peakFindingReads, activeFindingReads);
      await new Promise(resolve => setTimeout(resolve, 1));
      activeFindingReads--;
    }
    if (path === "/console/config") return Response.json({ clientId: org, tenantId: org, scope: "api://test/access_as_user", staleAfterHours: 72 });
    if (path.includes("scan-summaries") && summaryFailure) return new Response("", { status: 503 });
    if (path.includes("/findings?") && findingFailure) return new Response("", { status: 403 });
    const data: unknown = path.endsWith("/organizations") ? [{ id: org, name: "Test organization" }]
      : path.endsWith("/workspace") ? {
        organization: { id: org, name: "Test organization" }, currentMember: { assignedRoleName: "Auditor" },
        allowedActions: permissions, bindings: [
          { provider: 1, externalTenantId: "aws-one", displayName: "Production" },
          { provider: 0, externalTenantId: "azure-one", displayName: "Never scanned" }
        ]
      }
      : path.includes("/scan-summaries") ? history
      : path.includes("/governance/report") ? {
        asOfUtc: new Date().toISOString(), items: [{
          campaign: "Quarterly access review", campaignId: "campaign", itemId: "item",
          provider: 1, account: "aws-one", team: "Platform", owner: "owner", reviewer: "reviewer",
          dueAtUtc: completed, completed: false, overdue: true, exceptionExpired: false
        }]
      }
      : path.includes("/findings?") ? Array.from({ length: recordCount }, (_, index) => ({
        provider: 1, kind: "finding", recordId: `risk-${index}`, externalAccountId: "aws-one",
        payloadJson: JSON.stringify({ Title: "<img src=x onerror=alert(1)>", Severity: 0, Status: 0, Evidence: "Captured permission evidence", Recommendation: "Review privilege" })
      })) : [];
    return Response.json({ data, version: "v1" });
  }));
  await import("../src/main.ts");
  await vi.waitFor(() => expect((document.getElementById("sign-in") as HTMLButtonElement).disabled).toBe(false));
});

afterEach(() => { vi.unstubAllGlobals(); });

async function openOrganization() {
  document.getElementById("sign-in")!.click();
  const organization = document.getElementById("organization") as HTMLSelectElement;
  await vi.waitFor(() => expect(organization.options).toHaveLength(2));
  organization.value = org;
  organization.dispatchEvent(new Event("change"));
  await vi.waitFor(() => expect(document.getElementById("message")!.textContent).toContain("Read-only view loaded"));
  await vi.waitFor(() => expect(document.getElementById("findings")!.textContent).not.toContain("Loading"));
}

it("renders stale/partial/missing coverage, safe evidence, backlog and history with no writes", async () => {
  await openOrganization();
  expect(document.getElementById("coverage")!.textContent).toContain("Stale / Partial");
  expect(document.getElementById("coverage")!.textContent).toContain("no snapshot");
  expect(document.getElementById("coverage")!.textContent).toContain("RunScan permission required");
  expect(document.getElementById("reviews")!.textContent).toContain("Overdue");
  expect(document.getElementById("trends")!.textContent).toContain("1 / 1 / 0");
  expect(document.querySelector("#findings img")).toBeNull();
  (document.querySelector("#findings button") as HTMLButtonElement).click();
  expect(document.getElementById("evidence")!.textContent).toContain("Captured permission evidence");
  expect(document.getElementById("evidence-section")!.hidden).toBe(false);
  expect(calls.every(c => c.method === "GET")).toBe(true);
});

it("gates reads by the API permission map and clears sensitive data at sign-out", async () => {
  permissions = [0];
  await openOrganization();
  expect(document.getElementById("finding-notice")!.textContent).toContain("ViewFindings permission");
  expect(calls.some(c => c.path.includes("/findings?") || c.path.includes("/governance/report") || c.path.includes("/scan-jobs"))).toBe(false);
  document.getElementById("sign-out")!.click();
  await vi.waitFor(() => expect(document.getElementById("workspace")!.hidden).toBe(true));
  expect(document.getElementById("coverage")!.textContent).toBe("");
  expect(document.getElementById("evidence")!.textContent).toBe("");
});

it("does not call unavailable inventory zero risk, and labels API failures", async () => {
  summaryFailure = true;
  await openOrganization();
  expect(document.getElementById("coverage")!.textContent).toContain("HTTP 503");
  expect(document.getElementById("posture")!.textContent).toContain("Unavailable");
  expect(document.getElementById("findings")!.textContent).toContain("no zero-risk conclusion");
});

it("clears finding evidence when filters change and respects provider/account scope", async () => {
  await openOrganization();
  (document.querySelector("#findings button") as HTMLButtonElement).click();
  const provider = document.getElementById("provider") as HTMLSelectElement;
  provider.value = "0";
  provider.dispatchEvent(new Event("change"));
  await vi.waitFor(() => expect(document.getElementById("evidence-section")!.hidden).toBe(true));
  expect(document.getElementById("coverage")!.textContent).not.toContain("aws-one");
  expect(document.getElementById("reviews")!.textContent).toContain("No unresolved");
  expect(document.querySelector("#findings button")).toBeNull();
});

it("surfaces denied finding reads rather than claiming there are no findings", async () => {
  findingFailure = true;
  await openOrganization();
  expect(document.getElementById("finding-notice")!.textContent).toContain("Permission denied");
  expect(document.getElementById("findings")!.textContent).toContain("Data may be unavailable");
});

it("labels the exact record cap and limits fan-out to 20 snapshots and four concurrent reads", async () => {
  history = Array.from({ length: 21 }, (_, index) => ({
    ...snapshot, scanId: `scan-${index}`,
    accounts: [{ provider: 1, externalAccountId: `account-${index}`, displayName: `Account ${index}` }]
  }));
  await openOrganization();
  expect(calls.filter(c => c.path.includes("/findings?"))).toHaveLength(20);
  expect(peakFindingReads).toBe(4);
  expect(document.getElementById("finding-notice")!.textContent).toContain("first 20 latest account snapshots");

  history = [snapshot];
  recordCount = 1000;
  document.getElementById("refresh")!.click();
  await vi.waitFor(() => expect(document.getElementById("finding-notice")!.textContent).toContain("1,000-record cap reached"));
});

it("does not repopulate an organization after signing out during a refresh", async () => {
  await openOrganization();
  let release: () => void = () => {};
  summaryGate = new Promise<void>(resolve => { release = resolve; });
  document.getElementById("refresh")!.click();
  await vi.waitFor(() => expect(calls.filter(c => c.path.includes("scan-summaries"))).toHaveLength(2));
  document.getElementById("sign-out")!.click();
  release();
  await vi.waitFor(() => expect(document.getElementById("message")!.textContent).toContain("Signed out"));
  expect(document.getElementById("workspace")!.hidden).toBe(true);
  expect(document.getElementById("coverage")!.textContent).toBe("");
  expect(document.getElementById("findings")!.textContent).toBe("");
});
