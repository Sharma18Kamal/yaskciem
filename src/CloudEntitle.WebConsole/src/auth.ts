import { BrowserCacheLocation, InteractionRequiredAuthError, PublicClientApplication } from "@azure/msal-browser";
import type { ConsoleConfiguration } from "./contracts.ts";

export async function createSession(config: ConsoleConfiguration) {
  const client = new PublicClientApplication({
    auth: {
      clientId: config.clientId,
      authority: `https://login.microsoftonline.com/${config.tenantId}`,
      redirectUri: new URL("/console/auth.html", location.origin).href,
      navigateToLoginRequestUrl: false
    },
    cache: { cacheLocation: BrowserCacheLocation.MemoryStorage }
  });
  await client.initialize();
  const request = { scopes: [config.scope] };
  return {
    async signIn() {
      const result = await client.loginPopup({ ...request, prompt: "select_account" });
      client.setActiveAccount(result.account);
    },
    async token(): Promise<string> {
      const account = client.getActiveAccount();
      if (!account) throw new Error("Sign in to continue.");
      try { return (await client.acquireTokenSilent({ ...request, account })).accessToken; }
      catch (error) {
        if (error instanceof InteractionRequiredAuthError) throw new Error("Session expired; sign out and sign in again.");
        throw new Error("Unable to acquire API access. Check Entra consent and sign in again.");
      }
    },
    async signOut() {
      const account = client.getActiveAccount();
      client.setActiveAccount(null);
      await client.logoutPopup({ account, postLogoutRedirectUri: new URL("/console/auth.html", location.origin).href });
    }
  };
}
