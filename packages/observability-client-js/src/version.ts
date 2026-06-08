/**
 * Issue 10.4 — SDK version sent on every ingest request via `X-Observability-SDK-Version`.
 *
 * `__SDK_VERSION__` is inlined from package.json at build time by tsup's `define` (see
 * tsup.config.ts). It falls back to a dev sentinel when the constant isn't substituted — e.g.
 * under vitest, which doesn't run tsup. `typeof` guards against a ReferenceError in that case.
 */
declare const __SDK_VERSION__: string | undefined;

export const SDK_VERSION: string =
  typeof __SDK_VERSION__ !== "undefined" ? __SDK_VERSION__ : "0.0.0-dev";

export const SDK_VERSION_HEADER = "X-Observability-SDK-Version";

/** Platform-tagged value per docs/api-contract.md, e.g. "js/0.2.0". */
export function sdkVersionHeaderValue(): string {
  return `js/${SDK_VERSION}`;
}
