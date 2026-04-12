# Apple Frameworks — Remaining Work

## macOS

- ~~**Smoke test wiring**: Add per-framework conditional `<Compile>` lines for CryptoKit and WeatherKit in `RuntimeTestsApp.Mac.csproj`, plus mirrored `_<Framework>SmokeGateCheck` targets for `osx-arm64`.~~ Done (ccc8cae6).
- ~~**7 `AsyncMethodTests` timeouts**: Swift async completion callbacks don't fire on macOS.~~ Fixed via NSRunLoop pump in macOS launcher (12beef40). Baseline moved from 1053/7/17 to 1060/0/17.

## tvOS

- **tvOS device runner**: Deferred — requires provisioning + physical Apple TV.

## Mac Catalyst

- ~~**Catalyst runtime runner**: No runtime runner exists.~~ Done (0d157a9c). `nuke runtime-tests-catalyst` — 849/0/14 on first bringup. Pre-existing Mono JIT SIGSEGV in OwnershipGCStressTests cuts the run short (not a regression).

## Additional frameworks

- ~~**WorkoutKit / RoomPlan / ProximityReader / LiveCommunicationKit smokes**: Deferred until Tier-A smokes stable.~~ Done (3c445223). All 4 wired with 2 metadata-only tests each. iOS-only for now.
