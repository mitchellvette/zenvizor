# Epic G — Known-process distinction

**Release:** 1.5.0 (minor) — shipped **complete** (catalog verification +
common-items), bundled with Epics F + I
**Status:** stub (catalog-verification phase ready to spec; common-items phase
needs content + UX planning)
**Depends on:** nothing (the two phases are independent)
**Build / QA note:** the catalog-verification phase touches the attribution
hot path + the per-app signature cache. Build and QA it **in isolation** (its
own commit, verified alone) even though it ships in the 1.5.0 bundle, so a
verification regression doesn't entangle with F / I when bisecting.

---

## The lock: three trust concepts, kept separate

Conflating these is how a security tool ends up vouching for an impostor. Do
not merge them.

1. **Catalog-signed (this epic's verification phase)** — real cryptographic
   verification of the actual file. The **only** concept that asserts
   authenticity.
2. **Baseline-known (Epic B's baseline phase)** — "was already on this machine
   when ZenVizor installed." Per-machine; suppresses the *new-app* signal only.
   Asserts nothing about trust.
3. **Common-items (this epic's annotation phase)** — name/path-keyed *context*,
   inherently spoofable. **Never** asserts trust; flags mismatches as caution.
   **Absence from the list is not suspicion.**

**User correction captured:** we do **not** attempt to *verify* system
processes — that invites false-verification risk. The annotation phase is
**anomaly detection** (named like X but in the wrong place / unsigned), not
reassurance.

## Verification phase — catalog-aware signature verification

- **Problem:** catalog-signed Windows binaries (most OS components are signed
  via security catalogs, not embedded Authenticode) currently mis-report as
  `Unsigned`.
- **Fix:** extend `WinVerifyTrustSignatureVerifier.cs`
  (`src/ZenVizor.Attribution/Authenticode/`) so that when `WinVerifyTrust`
  finds no embedded signature, it falls back to a catalog lookup
  (`CryptCATAdmin*` — `CryptCATAdminCalcHashFromFileHandle` +
  `CryptCATAdminEnumCatalogFromHash`) before concluding `Unsigned`.
- **Invariant 6 preserved:** offline only — `WTD_REVOKE_NONE`, no network.
- **Caution:** touches the attribution hot path + the per-app signature cache.
  Keep verification cached per app (never per event). Build and QA this phase
  **in isolation** (its own commit) even though it ships in the 1.5.0 bundle.
- **Correction in nature:** on its own this is a corrected attribution edge
  case (catalog-signed → `Unsigned`) with no new surface; it ships bundled
  with the annotation phase, which is what makes the release a minor.

## Annotation phase — common-items

- Curated, **locally-shipped** lookup with a visual cue + hover tooltip.
  Highest value is anomaly detection, not reassurance.
- **Owner-maintained** (Mitchell): curated at version tags as a pre-tag
  validation step. The list is a **tracked** repo artifact (not gitignored).
- New surface (the visual cue + the shipped table) → minor.
- **Planning hand-off:** data format / sourcing for the list, and the exact
  anomaly UX, go to Claude.ai.

## Open questions (annotation phase)

- List schema + how "wrong place / wrong signer" mismatches are expressed.
- How the cue stays clearly non-authoritative (absence ≠ suspicion).

## Version classification

**1.5.0 (minor).** The verification phase is a corrected attribution edge case
(no new surface) on its own, but it ships bundled with the annotation phase —
a new visual cue + a shipped lookup table — so the release adds surface and is
a minor. Shipped complete, with Epics F + I, in 1.5.0.
