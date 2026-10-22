export interface ConsoleConfiguration {
  clientId: string;
  tenantId: string;
  scope: string;
  staleAfterHours: number;
}

export interface Organization { id: string; name: string }
export interface Binding { provider: number; externalTenantId: string; displayName: string }
export interface Workspace {
  organization: Organization;
  currentMember: { assignedRoleName: string };
  allowedActions: number[];
  bindings: Binding[];
}
export interface ScanAccount { provider: number; externalAccountId: string; displayName: string }
export interface Coverage { provider: string; dataSource: string; status: string; records: number; details: string }
export interface Scan {
  scanId: string;
  completedAtUtc: string;
  provider: string;
  accounts: ScanAccount[];
  findings: number;
  criticalFindings: number;
  highFindings: number;
  identities: number;
  resources: number;
  coverage: Coverage[];
}
export interface FindingRecord {
  provider: number;
  kind: string;
  recordId: string;
  externalAccountId: string;
  payloadJson: string;
}
export interface Finding extends FindingRecord {
  scanId: string;
  completedAtUtc: string;
  payload: Record<string, unknown> | null;
}
export interface WorkItem {
  campaign: string;
  campaignId: string;
  itemId: string;
  provider: number;
  account: string;
  team: string;
  owner: string | null;
  reviewer: string | null;
  dueAtUtc: string;
  completed: boolean;
  overdue: boolean;
  exceptionExpired: boolean;
}
export interface GovernanceReport { asOfUtc: string; items: WorkItem[] }
export interface ScanJob {
  provider: number;
  externalAccountId: string;
  status: string;
  queuedAtUtc: string;
  completedAtUtc: string | null;
}
export type ReadResult<T> = { data: T; error?: never } | { error: string; data?: never };
export interface Filter { provider: string; account: string }
export const Actions = { ViewInventory: 0, RunScan: 1, ViewFindings: 2, ManageRemediations: 7 } as const;
