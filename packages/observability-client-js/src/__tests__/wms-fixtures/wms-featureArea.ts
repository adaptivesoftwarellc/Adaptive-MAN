// The WMSSite-specific feature-area map the audit recommends WMSSite ship in its
// src/utils/routeUtils.js (docs/audits/wmssite.md §A). The SDK's built-in
// FEATURE_AREA_RULES (see ../../route.ts) intentionally cover only cross-app
// areas (auth, dashboard, settings, admin, orders, reports); WMSSite's clinical
// surface (patients, eligibility, insurance, ivr, worklist, …) is app-specific.
//
// Pinned here so the parity test documents the exact gap WMSSite must fill — if
// the SDK later grows a runtime featureAreaMap option (the planned path), this
// map is what WMSSite would pass in.

const WMS_FEATURE_AREA_MAP: Record<string, string> = {
  patients: "patients",
  "eligibility-queue": "eligibility",
  insurance: "insurance",
  ivr: "ivr",
  products: "products",
  worklist: "worklist",
  "master-schedule": "scheduling",
  "skin-log": "skin_log",
  import: "import",
  history: "history",
  blog: "blog",
  reports: "reports",
  settings: "settings",
  changepassword: "auth",
  register: "auth",
  login: "auth",
  "": "home",
};

export function routeToFeatureArea(pathname: string): string {
  const segment = pathname.split("/")[1] ?? "";
  return WMS_FEATURE_AREA_MAP[segment] ?? "unknown";
}
