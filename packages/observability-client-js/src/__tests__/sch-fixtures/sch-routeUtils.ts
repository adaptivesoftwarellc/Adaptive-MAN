// Vendored verbatim from SCH_UI commit cf7f65d (2026-04-29) at
// sch-ui/src/utils/routeUtils.ts. Read-only reference used by the SCH parity
// test in schParity.test.ts — do not import from production code.
//
// Phase 4.2: this is the rule set SCH_UI's PostHog integration uses today. The
// adaptive-observability JS SDK's route.ts normalizer is deliberately stricter
// (segment-by-segment, won't turn `posthog-500-test` into `posthog-:id-test`) so
// the parity test verifies functional equivalence on real SCH paths, not byte
// identity.

export function normalizeRoute(pathname: string): string {
  return (
    pathname
      .replace(/\/\d+/g, "/:id")
      .replace(/\/[a-f0-9-]{36}/gi, "/:uuid")
      .replace(/\/[A-Za-z0-9_-]{20,}/g, "/:token")
      .replace(/\/$/, "") || "/"
  );
}

export function routeToFeatureArea(pathname: string): string {
  const segment = pathname.split("/")[1] ?? "";
  const map: Record<string, string> = {
    patients: "patients",
    orders: "orders",
    claims: "billing",
    "billing-transfer": "billing",
    batches: "billing",
    reports: "reports",
    tasklist: "tasklist",
    "doc-holds": "doc_holds",
    worklist: "worklist",
    "coordinator-dashboard": "dashboard",
    "provider-portal": "provider_portal",
    admin: "admin",
    "home-health-agencies": "admin",
    "corporate-groups": "admin",
    "pending-swos": "swos",
    sign: "swos",
    "": "home",
  };
  return map[segment] ?? "unknown";
}

export function normalizeEndpoint(url: string | undefined): string {
  if (!url) return "unknown";
  let path = url;
  try {
    if (url.startsWith("http")) path = new URL(url).pathname;
  } catch {
    path = url;
  }
  const segments = path.split("/").filter(Boolean);
  if (segments[0] === "api" && segments[1]) return segments[1].toLowerCase();
  return segments[0]?.toLowerCase() ?? "unknown";
}
