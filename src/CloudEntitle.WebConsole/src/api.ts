import type { ReadResult } from "./contracts.ts";

export class ApiClient {
  constructor(private readonly token: () => Promise<string>) {}

  async get<T>(path: string, signal?: AbortSignal): Promise<T> {
    if (!path.startsWith("/api/v1/") || path.includes("..") || path.includes("\\"))
      throw new Error("Invalid API read path.");
    const response = await fetch(path, {
      method: "GET",
      headers: { Authorization: `Bearer ${await this.token()}`, Accept: "application/json" },
      cache: "no-store", credentials: "omit", redirect: "error", signal
    });
    if (!response.ok) {
      const reason = response.status === 403 ? "Permission denied"
        : response.status === 401 ? "Session expired; sign in again"
        : response.status === 404 ? "Organization or data unavailable"
        : `API unavailable (HTTP ${response.status})`;
      throw new Error(reason);
    }
    const envelope: { data: T; version: string } = await response.json();
    if (envelope.version !== "v1" || !("data" in envelope)) throw new Error("Unsupported API response.");
    return envelope.data;
  }

  async read<T>(path: string, signal: AbortSignal): Promise<ReadResult<T>> {
    try { return { data: await this.get<T>(path, signal) }; }
    catch (error) {
      if (signal.aborted) throw error;
      return { error: error instanceof Error ? error.message : "API read failed" };
    }
  }
}
