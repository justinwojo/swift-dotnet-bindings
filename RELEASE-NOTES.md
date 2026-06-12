A fast follow-up to 0.14.0 that unblocks App Store submission of device apps built with these bindings, and clears a packing regression plus two marshalling and dispatch crashes surfaced since the last release. SDK lane only — `SwiftBindings.Apple` is unchanged.

## Highlights

- **App Store submission unblocked, both distribution flows** — Apps that embed Swift now get the top-level `SwiftSupport` folder Apple requires whether you publish a device IPA directly *or* produce a `.xcarchive` and distribute it through Xcode Organizer ("Distribute App"), so uploads to App Store Connect and TestFlight no longer bounce with `ITMS-90426` ([#42](https://github.com/justinwojo/swift-dotnet-bindings/issues/42)).
- **Optional value types round-trip `nil` correctly** — A value-type `Optional` carried as a tuple or collection element now marshals a genuine Swift `nil` to C# as `null` instead of a wrong non-`null` default.
- **Closure callbacks no longer crash on partial conformance** — Calling back into a C#-implemented protocol no longer `SIGSEGV`s when only one of several peer protocols sharing a closure signature is implemented in C#.
- **Mixed ObjC+Swift bindings pack again** — Fixes a 0.14.0 regression where packing a mixed binding with `dotnet pack --no-build` failed with `NETSDK1085` and produced no package.

## Bug fixes

- **`Optional<value type>` element `None` collapse** — A value-type `Optional` appearing as a tuple or container element previously marshalled a real Swift `nil` to C# as `Some(default)` rather than `null`, silently turning an absent value into a wrong present one. The optional projection now preserves the `nil` case through tuple and collection copy-out.
- **Closure fan-out and same-signature dispatch crashes** — A force-unwrap in the field-filtered single-branch closure fan-out, and a `nil` owner-vtable unwrap when a same-signature closure method had only its peer protocol implemented in C#, both hard-crashed with `SIGSEGV`. Both now resolve the vtable safely instead of force-unwrapping.
- **Mixed-binding `--no-build` pack regression** — `dotnet pack --no-build` forwards `NoBuild=true` as a global property, which leaked into the out-of-band ObjC-companion build the SDK schedules and tripped `NETSDK1085`, so every mixed (ObjC+Swift) binding failed to pack on 0.14.0. The SDK now pins `NoBuild=false` on its internal companion and sibling-dependency builds, insulating them from the outer no-build pack.

## Reported issues fixed

- **[#42](https://github.com/justinwojo/swift-dotnet-bindings/issues/42) — App Store rejects Swift apps with `ITMS-90426` (missing `SwiftSupport`)** — The .NET-for-iOS build never runs the "Distribute App" pass that adds the top-level `SwiftSupport` folder Xcode would, so App Store Connect rejected the upload. Because the binding is what pulls Swift into the app, `SwiftBindings.Runtime` now injects a compliant `SwiftSupport/<platform>` built from the Apple-signed back-deployment `libswift*` dylibs the app references, on **both** App Store distribution paths: it post-processes the finished device IPA on a direct `BuildIpa` publish (`Payload/` stays byte-for-byte intact), and it writes the folder into the `.xcarchive` root on an `ArchiveOnBuild` build so Xcode Organizer's "Distribute App" carries it into the exported IPA — the flow the reporter actually uses, and the standard Visual Studio / MAUI "Publish" path. It applies automatically to iOS and tvOS; opt out with `<EnableSwiftSupportFolder>false</EnableSwiftSupportFolder>`, and it is a no-op when the app references the Runtime without linking Swift. This is delivered entirely in the Runtime package, so existing bindings pick it up by referencing Runtime 0.14.1.

## Packages

| Package | Version |
|---------|---------|
| SwiftBindings.Runtime | 0.14.1 |
| SwiftBindings.Sdk | 0.14.1 |
| SwiftBindings.Templates | 0.14.1 |

`SwiftBindings.Apple` is unchanged and stays at `26.2.6`. See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for installation and usage.
