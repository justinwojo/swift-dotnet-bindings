# Session 2 — C# verification gate

**Verdict (by experiment, 5 libs): MSBuild+SARIF is the publication gate; the in-process Roslyn probe is an acceleration heuristic only.** Probe vs real `dotnet build /errorlog:` on Hero (no-deps), CocoaMQTT (deps), Segment (mixed+deps), Eureka, AEXML. Per-item: P1 ref-pack version is a guess; P2 dependency + Apple-supplement assemblies are NuGet-restored at build, absent at gen (CS0234); P3 SDK Roslyn 5.x-preview vs released 4.12.0; **P4 (dominant): `[LibraryImport]` interop generator doesn't run in-process → CS8795 flood, 85–99% of probe errors.** Both agreed on 0 diagnostics for healthy libs.

**Gate:** fail-closed at stage `CSharpCompile`, code **SWIFTBIND113**, on standalone wrapper-compiling paths (xcframework + direct + pure-ObjC). SDK two-pass, `--compile-only`, BindingTests (`--no-verify-csharp`) opt out. Only `CS####` fails; NU/MSB-only = Inconclusive; verifier exception = logged — the diagnostic shape session 03 consumes.

**Roslyn 4.0.0→4.12.0** (newest released; probe-only refs).

**Hermeticity:** verification build forces `-p:TreatWarningsAsErrors=false`; genuine errors always fail.

**Honest-red (Hero):** gate-off = gen exit 0, consumer build fails 5 CS errors; gate-on = gen exit 1, SWIFTBIND113 lists the same 5 at gen time.

Pure-ObjC gate validated on MBProgressHUD. Gates: unit 15,246/0, compile-only 0, sim 3242/0/0/37skip.
