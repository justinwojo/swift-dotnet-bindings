# Direct SwiftSelf exposure reporting

The generator reports every settled public member or accessor whose managed-to-native path reaches
a Swift-calling-convention call with a top-level, untyped `SwiftSelf` carrier. The report is
observability, not a routing decision: it changes no generated signature, attribute, wrapper,
withdrawal, or eligibility rule.

Rows use `DiagnosticId: "DirectSwiftSelfExposure"` in `binding-report.json`'s
`DegradedMembers` list and set `IsAttributeEmitted` to `false`. A safety-marked member can therefore
have both its existing `SB0001` row and a separate exposure row. `DegradedSurface.Total` counts
diagnostic rows; `DegradedSurface.Exposure.PublicMemberCount` deduplicates accessors to public
members, while `NativeCallCount` deduplicates shared physical calls.

`ExposureReportingVersion: 1` and `ExposureCompleteness` prove the final generated C# inventory was
read. `Complete` with zero classified calls is a valid zero. A missing receipt in an older report
means unknown coverage, not zero. The mandatory compile-only gate independently reparses the final
files and compares the exact public owner, accessor, native call, route, self role, reason, and
inventory hash with the report.

## Copy-ready text for the P8 Known Limitations wiki

### Mono full-AOT calls using SwiftSelf

Some generated members call a Swift-ABI entry point directly and pass an untyped `SwiftSelf` in the
register reserved by Swift. On iOS with Mono full-AOT, Mono's generated managed-to-native wrapper
may also choose that register for its GC safe-region cookie. Whether the cookie is clobbered depends
on the generated wrapper and allocator output, so two calls with similar Swift signatures can have
different outcomes. The observed failure is a process abort after the Swift callee returns; exposure
is a risk indicator, not a claim that every listed member crashes.

This is distinct from callback exceptions on Mono JIT, from NativeAOT behavior, from typed
`SwiftSelf<T>` values that travel through ordinary argument registers, and from calls carrying only
`SwiftError`. A call may carry both untyped `SwiftSelf` and `SwiftError`; the report records
`HasSwiftError` separately rather than treating the error carrier as the cause. NativeAOT has passed
known affected fixtures, but it is not a universal remedy guarantee for every interop defect.

To inspect a generated binding, filter `binding-report.json`:

```sh
jq '.DegradedMembers[] | select(.DiagnosticId == "DirectSwiftSelfExposure")' binding-report.json
```

For each row, inspect `ContainingType` and `PublicApiKey` for the managed owner, `Accessor` for a
getter/setter distinction, and `NativeCall` for library, entry point, convention, and lowered
carriers. `Route` distinguishes a direct Swift symbol (`swift_native`), a Swift-convention silgen
shim (`swift_silgen_wrapper`), and an invoked Swift function pointer
(`swift_function_pointer`). An `SBSW_` name does not imply C calling convention; these shims can
still use the Swift ABI. `WrapperReason` and `ReasonProvenance` explain why that final route exists
and where the explanation came from.

Check `ExposureCompleteness.Status` before interpreting an empty result. `Complete` plus a current
version means the final generated files were classified. A missing receipt is legacy/unknown
coverage. The exposure diagnostic is report-only and does not appear as a C# attribute, so searching
generated source for `DirectSwiftSelfExposure` is not a substitute for reading the report.
