STATUS: COMPLETE

Fixed 5 corpus-found C#-emission bugs at root cause (test-first):

a. **AnyError ctor drift**: stale published Apple-supplement floor `[26.0.0,)` resolved to 26.2.0, which lacks the `ownsContainer:` ctor the emitter calls (CS1739 in SwiftyStoreKit/Siren). Bumped `DefaultAppleSupplementVersion`→26.2.4 (first published with the ctor), forwarded from all 3 sites; drift-guard test. Symptom gone.

b. **Label-only overloads**: foreign-ext members differing only by argument label collapsed to one C# signature (Hue CS0111×4). Rename the whole colliding group; reserve natural-sibling keys class-wide (closes a review-found cross-group gap). Hue ok.

c. **Uninhabited caseless enum**: emitted as an unusable static class → CS0718/CS0721 (FloatingPanel). Now projects as an empty value `enum`. Symptom gone.

d. **Array-metatype discriminator** (`[T].self`) projected as IEnumerable<T> (Disk CS1061). Routes out of the container path → method honestly skipped. BindingTests fixture.

e. **Empty dependency shim**: zero-decl ABI hard-failed SWIFTBIND073 (swift-case-paths). Skip-with-warning; malformed ABI still fail-closed.

**SWIFTBIND024** overwrite storm = BENIGN (additive superset merge, aborts before emission).

Gates: `nuke test` 14677/0; `--compile-only` Succeeded; sim 3231 (6 env, ≥3192). Review: Grok 2 rounds (Codex not installed); (c)-High = false positive (no regression), (b)-Medium fixed+tested.
