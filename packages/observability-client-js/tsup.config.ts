import { readFileSync } from "node:fs";
import { defineConfig } from "tsup";

const pkg = JSON.parse(readFileSync(new URL("./package.json", import.meta.url), "utf8")) as { version: string };

export default defineConfig({
  // Inline the package version so the SDK can report it via X-Observability-SDK-Version (Issue 10.4).
  define: { __SDK_VERSION__: JSON.stringify(pkg.version) },
  entry: {
    index: "src/index.ts",
    axios: "src/axios.ts",
    react: "src/react.tsx",
    replay: "src/replay.ts",
  },
  format: ["esm", "cjs"],
  dts: true,
  sourcemap: true,
  clean: true,
  splitting: false,
  treeshake: true,
});
