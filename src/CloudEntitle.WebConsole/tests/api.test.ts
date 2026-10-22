import { afterEach, expect, it, vi } from "vitest";
import { ApiClient } from "../src/api.ts";

afterEach(() => vi.unstubAllGlobals());

it("uses only authenticated GETs, with no browser cache, cookies or redirect following", async () => {
  const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify({ data: [1], version: "v1" })));
  vi.stubGlobal("fetch", fetchMock);
  const client = new ApiClient(async () => "test-token");
  expect(await client.get("/api/v1/organizations")).toEqual([1]);
  expect(fetchMock).toHaveBeenCalledWith("/api/v1/organizations", expect.objectContaining({
    method: "GET", cache: "no-store", credentials: "omit", redirect: "error",
    headers: { Authorization: "Bearer test-token", Accept: "application/json" }
  }));
  await expect(client.get("https://external.invalid")).rejects.toThrow("Invalid API");
  await expect(client.get("/api/v1/../write")).rejects.toThrow("Invalid API");
  expect(fetchMock).toHaveBeenCalledTimes(1);
});

it("does not turn failures into successful empty datasets or expose response bodies", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response("secret internal error", { status: 403 })));
  const result = await new ApiClient(async () => "token").read("/api/v1/organizations", new AbortController().signal);
  expect(result).toEqual({ error: "Permission denied" });
});

it("rejects unsupported envelopes", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({ data: [], version: "v2" }))));
  await expect(new ApiClient(async () => "token").get("/api/v1/organizations")).rejects.toThrow("Unsupported");
});

it("preserves cancellation rather than rendering an unavailable response for a previous organization", async () => {
  const controller = new AbortController();
  controller.abort();
  vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new DOMException("Aborted", "AbortError")));
  await expect(new ApiClient(async () => "token").read("/api/v1/organizations", controller.signal)).rejects.toThrow("Aborted");
});
