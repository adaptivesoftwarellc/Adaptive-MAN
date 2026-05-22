// Representative SCH_UI paths collected from sch-ui/src/components/layout/Layout.tsx
// and sch-ui/src/pages/Reports.tsx (real routes the FE currently navigates to).

// Paths where SCH_UI's normalizer and Adaptive-MAN's normalizer agree byte-for-byte.
export const PARITY_STATIC_PATHS: readonly string[] = [
  "/",
  "/patients",
  "/orders",
  "/claims",
  "/billing-transfer",
  "/batches",
  "/reports",
  "/reports/monthly",
  "/reports/orders",
  "/reports/doc-holds",
  "/reports/enter-health-claims",
  "/reports/provider",
  "/reports/coordinator",
  "/reports/episodic",
  "/reports/comprehensive",
  "/reports/canceled",
  "/tasklist",
  "/doc-holds",
  "/swo/pending",
  "/swo-issues",
  "/provider-portal",
  "/provider-portal/swos",
  "/provider-portal/orders",
  "/provider-portal/doc-holds",
  "/provider-portal/patients",
  "/admin/users",
  "/admin/roles",
  "/admin/permissions",
  "/admin/products",
  "/admin/hcpcs-codes",
  "/admin/product-rules",
  "/admin/insurance-mappings",
  "/admin/providers",
  "/admin/provider-hierarchy",
  "/admin/api-clients",
  "/corporate-groups",
];

// Paths where SCH_UI's regex `/[A-Za-z0-9_-]{20,}/` over-matches a long literal
// segment and incorrectly replaces it with `:token`. Adaptive-MAN's per-segment
// check correctly leaves them literal. The test pins both behaviors so a future
// edit to either side surfaces immediately.
export const SCH_OVERMATCHES_LITERAL: ReadonlyArray<{ input: string; schOutput: string }> = [
  { input: "/reports/pending-provider-response", schOutput: "/reports/:token" },
  { input: "/coordinator-dashboard", schOutput: "/:token" },
  { input: "/admin/tasklist-definitions", schOutput: "/admin/:token" },
  { input: "/home-health-agencies", schOutput: "/:token" },
];

// Numeric-id paths. Both normalizers collapse pure numeric segments; outputs
// match byte-for-byte since both pick `:id`.
export const ID_BEARING_PATHS: ReadonlyArray<{ input: string; idCount: number }> = [
  { input: "/patients/12345", idCount: 1 },
  { input: "/orders/98765/edit", idCount: 1 },
  { input: "/claims/4242/documents/77", idCount: 2 },
  { input: "/admin/users/501", idCount: 1 },
  { input: "/provider-portal/orders/123/notes", idCount: 1 },
  { input: "/doc-holds/9", idCount: 1 },
];

// UUID paths. Adaptive-MAN cleanly collapses the whole segment to `:id`.
// SCH_UI's normalizer applies `\d+ → :id` first, which corrupts the UUID's
// leading digit before its UUID regex can match, so SCH leaves the rest of the
// UUID as a literal — a real bug surfaced by this audit.
export const UUID_BEARING_PATHS: ReadonlyArray<{ input: string; expected: string }> = [
  {
    input: "/patients/3fa85f64-5717-4562-b3fc-2c963f66afa6",
    expected: "/patients/:id",
  },
  {
    input: "/admin/api-clients/3fa85f64-5717-4562-b3fc-2c963f66afa6/keys",
    expected: "/admin/api-clients/:id/keys",
  },
];
