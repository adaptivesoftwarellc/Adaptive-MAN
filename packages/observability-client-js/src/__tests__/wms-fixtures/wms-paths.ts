// Representative WMSSite paths collected from the WMSSite integration audit
// (docs/audits/wmssite.md, §E route inventory) — real routes the FE navigates to
// via useRoutes() in src/routes/sections.jsx.
//
// Unlike SCH_UI, WMSSite has NO pre-existing route normalizer: it onboards clean
// (JavaScript, MSAL, no prior telemetry) and the audit recommends its
// src/utils/routeUtils.js delegate path-stripping to this SDK's normalizeRoute.
// So these fixtures verify the SDK normalizer is correct on WMSSite's real route
// set — with explicit assertions on the PHI-bearing dynamic segments that must
// never leave the browser — rather than pinning a legacy normalizer's quirks.

// Static WMSSite routes (no dynamic segments). normalizeRoute must leave these
// byte-identical.
export const WMS_STATIC_PATHS: readonly string[] = [
  "/",
  "/login",
  "/patients",
  "/patients/intakes",
  "/eligibility-queue",
  "/insurance/eligibility-request",
  "/insurance/prior-authorization",
  "/products",
  "/blog",
  "/import",
  "/worklist",
  "/master-schedule",
  "/history",
  "/changepassword",
  "/register",
  "/settings/intake",
  "/settings/prior-authorization",
  "/settings/users",
  "/settings/enter-payors",
  "/settings/eligibility-config",
  "/reports",
  "/reports/intakes",
  "/reports/regional-intakes",
  "/reports/visits",
  "/reports/questionnaire-submissions",
  "/reports/postal-code-heatmap",
];

// PHI-bearing dynamic routes (audit §E). These carry patient/wound identifiers
// that MUST be stripped before any event is emitted. Numeric segments collapse
// to `:id`; `idCount` pins how many dynamic segments each route contains so a
// regression that lets one through is caught loudly.
export const ID_BEARING_PATHS: ReadonlyArray<{ input: string; expected: string; idCount: number }> = [
  { input: "/patients/12345", expected: "/patients/:id", idCount: 1 },
  { input: "/patients/12345/intakes", expected: "/patients/:id/intakes", idCount: 1 },
  { input: "/ivr/submit/98765", expected: "/ivr/submit/:id", idCount: 1 },
  // Wound assessment — both patient and wound ids must strip (audit flags this
  // route explicitly as the highest-PHI surface in WMSSite).
  { input: "/ivr/submit/98765/4242", expected: "/ivr/submit/:id/:id", idCount: 2 },
  { input: "/skin-log/501", expected: "/skin-log/:id", idCount: 1 },
  { input: "/eligibility-queue/777/edit", expected: "/eligibility-queue/:id/edit", idCount: 1 },
];

// UUID-bearing routes. The SDK collapses a whole UUID segment to `:id` via its
// per-segment UUID check — no partial leakage of the hex tail.
export const UUID_BEARING_PATHS: ReadonlyArray<{ input: string; expected: string }> = [
  {
    input: "/patients/3fa85f64-5717-4562-b3fc-2c963f66afa6",
    expected: "/patients/:id",
  },
  {
    input: "/ivr/submit/3fa85f64-5717-4562-b3fc-2c963f66afa6/9c1e7b20-0000-4000-8000-000000000000",
    expected: "/ivr/submit/:id/:id",
  },
];
