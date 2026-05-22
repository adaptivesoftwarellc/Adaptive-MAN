import { describe, it, expect } from "vitest";
import { normalizeRoute as adaptiveNormalize, getFeatureArea } from "../route";
import {
  normalizeRoute as schNormalize,
  routeToFeatureArea as schFeatureArea,
} from "./sch-fixtures/sch-routeUtils";
import {
  PARITY_STATIC_PATHS,
  SCH_OVERMATCHES_LITERAL,
  ID_BEARING_PATHS,
  UUID_BEARING_PATHS,
} from "./sch-fixtures/sch-paths";

// Phase 4.2 — verify the Adaptive-MAN JS SDK normalizer behaves correctly on
// the real SCH_UI route set, and document where it diverges from SCH_UI's
// current implementation. The audit produced two material findings worth
// capturing before SCH cutover (Phase 6.3):
//
//   1. SCH_UI's regex `/[A-Za-z0-9_-]{20,}/g` over-matches long literal route
//      segments (e.g. `/coordinator-dashboard` → `/:token`). Adaptive-MAN's
//      per-segment check correctly leaves them literal.
//   2. SCH_UI applies `/\d+/ → :id` before its UUID regex, which strips the
//      leading digit of a UUID and prevents the UUID rule from matching the
//      remainder. Adaptive-MAN's whole-segment UUID check is unaffected.
//
// The feature-area gap (SCH's map is much richer than Adaptive-MAN's defaults)
// is also pinned here so it surfaces during Phase 6.3 SCH integration.

describe("SCH parity — static paths normalize byte-identically", () => {
  for (const path of PARITY_STATIC_PATHS) {
    it(`${path}`, () => {
      expect(adaptiveNormalize(path)).toBe(schNormalize(path));
    });
  }
});

describe("SCH divergence — Adaptive preserves long literal segments that SCH over-matches", () => {
  for (const { input, schOutput } of SCH_OVERMATCHES_LITERAL) {
    it(`${input}`, () => {
      expect(adaptiveNormalize(input)).toBe(input);
      // Pin SCH's current (buggy) behavior so a future edit to the vendored
      // file shows up loudly.
      expect(schNormalize(input)).toBe(schOutput);
    });
  }
});

describe("SCH parity — numeric IDs match byte-for-byte", () => {
  for (const { input, idCount } of ID_BEARING_PATHS) {
    it(`${input} (${idCount} id segment${idCount === 1 ? "" : "s"})`, () => {
      const adaptive = adaptiveNormalize(input);
      expect(adaptive).toBe(schNormalize(input));
      expect(adaptive.match(/:id/g)?.length ?? 0).toBe(idCount);
    });
  }
});

describe("SCH divergence — Adaptive cleanly collapses UUIDs that SCH corrupts", () => {
  for (const { input, expected } of UUID_BEARING_PATHS) {
    it(`${input}`, () => {
      expect(adaptiveNormalize(input)).toBe(expected);
      const sch = schNormalize(input);
      // SCH outputs the original UUID with its leading digit replaced by :id
      // and the rest of the hex chars left literal — i.e. it should still
      // contain the UUID's tail, which Adaptive's output does not.
      expect(sch).toContain("fa85f64");
      expect(adaptiveNormalize(input)).not.toContain("fa85f64");
    });
  }
});

describe("SCH parity — feature-area gap pinned for Phase 6.3", () => {
  // Adaptive-MAN's FEATURE_AREA_RULES does not yet cover SCH-specific areas
  // (patients, claims, doc-holds, coordinator-dashboard, provider-portal, etc.).
  // Phase 6.3 will either extend the SDK's rule set or expose a runtime
  // featureAreaMap option so SCH can ship its richer map without forking.
  it("static SCH areas fall back to 'other' until 6.3 extends the rule set", () => {
    expect(getFeatureArea("/patients")).toBe("other");
    expect(getFeatureArea("/claims")).toBe("other");
    expect(getFeatureArea("/doc-holds")).toBe("other");
    expect(getFeatureArea("/coordinator-dashboard")).toBe("other");
    expect(getFeatureArea("/provider-portal/orders")).toBe("other");

    // SCH_UI's richer map covers them:
    expect(schFeatureArea("/patients")).toBe("patients");
    expect(schFeatureArea("/claims")).toBe("billing");
    expect(schFeatureArea("/doc-holds")).toBe("doc_holds");
    expect(schFeatureArea("/coordinator-dashboard")).toBe("dashboard");
    expect(schFeatureArea("/provider-portal/orders")).toBe("provider_portal");
  });

  it("areas the SDK already covers behave the same on both", () => {
    expect(getFeatureArea("/admin/users")).toBe("admin");
    expect(getFeatureArea("/orders")).toBe("orders");
    expect(getFeatureArea("/reports")).toBe("reports");
    expect(schFeatureArea("/admin/users")).toBe("admin");
    expect(schFeatureArea("/orders")).toBe("orders");
    expect(schFeatureArea("/reports")).toBe("reports");
  });
});
