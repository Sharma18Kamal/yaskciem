import { defineConfig } from "vite";

export default defineConfig({
  base: "/console/",
  build: {
    outDir: "../CloudEntitle.LicenseServer/wwwroot/console",
    emptyOutDir: true,
    sourcemap: false
  }
});
