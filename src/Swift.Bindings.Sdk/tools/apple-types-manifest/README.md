# Apple types manifest

Authoritative metadata for Apple Swift-only types bound into the
`SwiftBindings.Apple` supplement package. Consumed by two sites:

1. The generator's `TypeDatabase` — so references to e.g.
   `Foundation.Locale.Language` resolve to `Swift.Foundation.Locale.Language`
   in `SwiftBindings.Apple`, not to a non-existent Runtime type.
2. The `SwiftBindings.Apple` source-generation pipeline — which emits the
   managed types whose ABI the generator just bound against.

See `src/docs/apple-swift-types-architecture.md` for the design contract,
especially §"Resolved questions" Q7 and §"Implementation specifics" item 5.

## Location

Embedded under `SwiftBindings.Sdk/tools/apple-types-manifest/` for the 0.8
train. One NuGet coordination surface instead of two.

The format is intentionally self-contained — no SDK-relative paths, no
relative symbol references — so a later extraction into a standalone
`SwiftBindings.Apple.Metadata` data package is a relocation, not a
reformat.

## Files

- `manifest.json` — the manifest itself. Hand-seeded in Session 1; replaced
  by the generator in Session 2.
- `schema.json` — JSON Schema describing the manifest shape. Human- and
  tooling-readable; not a runtime dependency.
- `README.md` — this file.

## Key fields per type

- `swift_identity` — dotted Swift name, e.g. `Foundation.Locale.Language`.
- `managed_projection` — the CLR type a consumer's public surface uses.
- `abi_carrier` — the CLR type used to copy/destroy/pass values across the
  Swift→C boundary. Often the same as the projection for supplement types,
  but split out so legacy canonicals (where projection is `NSDate` but the
  carrier is our `Swift.Runtime.Date`) fit the same record.
- `metadata_accessor` — mangled symbol + library. VWT pointer is obtained
  via this accessor at runtime.
- `storage_strategy` — `vwt_opaque` by default. `sequential` requires the
  explicit `sequential_layout_whitelisted` gate.
- `value_witness` — whether the VWT comes from metadata or a static symbol,
  plus a `trivial` flag used to decide whether runtime copy/destroy can be
  lowered to memcpy.
- `conformance_descriptors` — cross-module protocol conformances anchored
  on this type. Ownership may differ from the type's module.
- `typealiases` (per module) — alias/projection metadata only; NOT
  duplicate type identity.

## Status tags

- `seed-placeholder` — hand-seeded in Session 1. Size/alignment are
  plausible-but-unvalidated guesses, symbols are best-effort. Session 2's
  ABI-JSON pipeline will overwrite these with generated entries.
- `generated` — produced by the generator from Apple's ABI JSON + live SDK
  symbol validation. Sessions 2+.
