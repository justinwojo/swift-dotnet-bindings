# Apple Frameworks — Remaining Work

## macOS

- **Smoke test wiring**: Add per-framework conditional `<Compile>` lines for CryptoKit and WeatherKit in `RuntimeTestsApp.Mac.csproj`, plus mirrored `_<Framework>SmokeGateCheck` targets for `osx-arm64`. The macOS pipeline itself works (`nuke runtime-tests-macos` — 1053/1077 passing).
- **7 `AsyncMethodTests` timeouts**: Swift async completion callbacks don't fire on macOS (all 7 pass on iOS sim). Likely the macOS native launcher's dispatch queue setup differs — may need `NSRunLoop` draining or a macOS-specific async dispatch mechanism. Low priority.

## tvOS

- **tvOS device runner**: Deferred — requires provisioning + physical Apple TV.

## Additional frameworks

- **Catalyst runtime runner**: No runtime runner exists. Resolver fallback is unit-tested only.
- **WorkoutKit / RoomPlan / ProximityReader / LiveCommunicationKit smokes**: Deferred until Tier-A smokes (StoreKit 2, CryptoKit, WeatherKit, TipKit, MusicKit) are stable.
