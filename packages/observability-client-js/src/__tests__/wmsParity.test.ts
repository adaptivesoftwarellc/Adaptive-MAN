import { describe, it, expect } from "vitest";
import { normalizeRoute, getFeatureArea } from "../route";
import { routeToFeatureArea as wmsFeatureArea } from "./wms-fixtures/wms-featureArea";
import {
  WMS_STATIC_PATHS,
  ID_BEARING_PATHS,
  UUID_BEARING_PATHS,
} from "./wms-fixtures/wms-paths";

// Issue 7.1 — verify the Adaptive-MAN JS SDK normalizer behaves correctly on
// WMSSite's real route set ahead of the WMSSite onboarding (docs/audits/wmssite.md).
//
// WMSSite onboards clean — it has no prior telemetry and no legacy route
// normalizer of its own (unlike SCH_UI, which shipped a buggy PostHog-era one).
// The audit recommends WMSSite's src/utils/routeUtils.js delegate path-stripping
// to this SDK's normalizeRoute and keep only a WMSSite-specific featureAreaMap.
// So this suite asserts two things:
//
//   1. The SDK normalizer strips every dynamic segment in WMSSite's routes —
//      especially the PHI-bearing /patients/:id and /ivr/submit/:patientId/:woundId
//      routes whose ids must never leave the browser.
//   2. The feature-area gap: WMSSite's clinical areas fall back to "other" under
//      the SDK's cross-app defaults, while WMSSite's own map covers them. This
//      pins exactly what WMSSite must ship (or pass via a future featureAreaMap).

describe("WMS routes — static paths pass through unchanged", () => {
  for (const path of WMS_STATIC_PATHS) {
    it(`${path}`, () => {
      expect(normalizeRoute(path)).toBe(path === "/" ? "/" : path);
    });
  }
});

describe("WMS routes — numeric ids strip (PHI guarantee)", () => {
  for (const { input, expected, idCount } of ID_BEARING_PATHS) {
    it(`${input} (${idCount} id segment${idCount === 1 ? "" : "s"})`, () => {
      const normalized = normalizeRoute(input);
      expect(normalized).toBe(expected);
      expect(normalized.match(/:id/g)?.length ?? 0).toBe(idCount);
      // Hard PHI assertion: no original digit run survives normalization.
      expect(normalized).not.toMatch(/\d{2,}/);
    });
  }
});

describe("WMS routes — UUIDs collapse whole-segment (no hex-tail leakage)", () => {
  for (const { input, expected } of UUID_BEARING_PATHS) {
    it(`${input}`, () => {
      const normalized = normalizeRoute(input);
      expect(normalized).toBe(expected);
      expect(normalized).not.toContain("fa85f64");
    });
  }
});

describe("WMS feature-area gap — pinned for the WMSSite onboarding", () => {
  // WMSSite-specific clinical areas the SDK defaults do not cover yet.
  it("WMS clinical areas fall back to 'other' under the SDK defaults", () => {
    expect(getFeatureArea("/patients")).toBe("other");
    expect(getFeatureArea("/eligibility-queue")).toBe("other");
    expect(getFeatureArea("/insurance/prior-authorization")).toBe("other");
    expect(getFeatureArea("/ivr/submit/:id")).toBe("other");
    expect(getFeatureArea("/worklist")).toBe("other");
  });

  it("WMSSite's own map covers them", () => {
    expect(wmsFeatureArea("/patients")).toBe("patients");
    expect(wmsFeatureArea("/eligibility-queue")).toBe("eligibility");
    expect(wmsFeatureArea("/insurance/prior-authorization")).toBe("insurance");
    expect(wmsFeatureArea("/ivr/submit/123")).toBe("ivr");
    expect(wmsFeatureArea("/worklist")).toBe("worklist");
  });

  it("cross-app areas the SDK already covers behave the same on both", () => {
    expect(getFeatureArea("/reports")).toBe("reports");
    expect(getFeatureArea("/settings/users")).toBe("settings");
    expect(wmsFeatureArea("/reports")).toBe("reports");
    expect(wmsFeatureArea("/settings/users")).toBe("settings");
  });
});
