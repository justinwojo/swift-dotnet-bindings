# Codex Task Specifications - Phase 43

Task specifications for continued runtime hardening and binding coverage improvements. With Lottie runtime validated in Phase 42, the focus shifts to fixing remaining runtime issues and expanding validation.

**Date**: February 2026
**Starting Point**: Phase 42 complete, 1032 unit tests passing
**Libraries**: Nuke (0 errors ✅, runtime validated), BlinkID (0 errors ✅), Lottie (0 errors ✅, 8/9 runtime tests pass)

---

## Status Summary

| Task | Description | Status | Priority |
|------|-------------|--------|----------|
| 1 | Fix LottieConfiguration.Shared property getter | 🔲 Pending | P1 |
| 2 | Lottie test app full validation pass | 🔲 Pending | P1 |
| 3 | BlinkID runtime validation | 🔲 Pending | P2 |

**Target**: All three libraries runtime validated
**Current**: Nuke fully validated, Lottie 8/9, BlinkID compiles only

---

## Task 1: Fix LottieConfiguration.Shared Property Getter

### Status: 🔲 Pending
### Priority: P1 (Last remaining Lottie runtime failure)
### Dependencies: None

### Problem Statement

`LottieConfiguration.Shared` returns a non-null object but accessing its properties throws `NullReferenceException`. This is the only failing test in the Lottie runtime suite.

**Runtime error**: `Object reference not set to an instance of an object`

**Test code** (in `BindingTesting/Lottie/LottieTestApp/Program.cs`):
```csharp
var config = LottieConfiguration.Shared;
// config is non-null, config.Payload is non-null
// But the null check `config != null && config.Payload != null` still fails
```

### Investigation Steps

1. Check how `LottieConfiguration.Shared` is generated in `Swift.Lottie.cs`
2. Verify the property getter P/Invoke returns a valid handle
3. Compare with working property getters (e.g., LottieColor properties which work)
4. Check if this is a static property getter marshalling issue

### Acceptance Criteria

- [ ] Root cause identified
- [ ] Fix implemented (or documented as architectural limitation)
- [ ] LottieTestApp passes 9/9 tests

---

## Task 2: Lottie Test App Full Validation Pass

### Status: 🔲 Pending
### Priority: P1
### Dependencies: Task 1

### Problem Statement

The Lottie test app currently exits with `TEST FAILURE` because of the 1 failing test. Once Task 1 is resolved, the `validate-sim.sh` script should exit 0.

### Acceptance Criteria

- [ ] `./validate-sim.sh 30` exits 0
- [ ] Console shows `TEST SUCCESS`
- [ ] All 9 tests pass

---

## Task 3: BlinkID Runtime Validation

### Status: 🔲 Pending
### Priority: P2
### Dependencies: None

### Problem Statement

BlinkID bindings compile cleanly but have never been runtime tested. Need a test app similar to `LottieTestApp` that exercises basic BlinkID APIs.

### Implementation Approach

1. Create `BindingTesting/BlinkID/BlinkIDTestApp/` project structure
2. Add basic tests: type metadata access, configuration, enum types
3. Add `validate-sim.sh` script
4. Test on simulator

### Acceptance Criteria

- [ ] BlinkIDTestApp project created
- [ ] Basic API smoke tests implemented
- [ ] validate-sim.sh runs and reports results

---

## Testing Commands Reference

```bash
# Run all unit tests
./run-tests.sh

# Build Lottie test app
cd BindingTesting/Lottie
dotnet build LottieTestApp/LottieTestApp.csproj

# Regenerate Lottie bindings (after generator changes)
./regenerate-bindings.sh

# Validate Lottie on simulator
cd BindingTesting/Lottie
./validate-sim.sh 30

# Validate Nuke on simulator
cd BindingTesting/Nuke
./validate-sim.sh 15
```

---

## Notes

- Phase 43 focuses on runtime hardening across all test libraries
- The `LottieConfiguration.Shared` issue may reveal a pattern affecting other static property getters
- BlinkID validation is lower priority but important for proving multi-library support
- The `comprehensive-test-library-design.md` doc (if present) may have additional context
