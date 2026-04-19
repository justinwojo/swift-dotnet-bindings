# Licensing Rationale

Why `SwiftBindings.*` can ship to nuget.org without Apple permission, and
what to check before each major publish. Companion to [`NOTICE.md`](NOTICE.md).

## Legal basis

1. **Apple Developer Program License Agreement, §7.5 (library carve-out).**
   Permits creating and distributing libraries that interoperate with
   Apple frameworks, provided the library does not redistribute Apple
   SDK contents (headers, source, compiled binaries, documentation) and
   does not present itself as an Apple product. Our published NuGet
   packages carry only interoperability metadata — layout, stride, size,
   alignment, method signatures, ABI entry-point symbols — generated on
   the consumer's build machine from Apple-provided ABI JSON and
   resolved via `dlsym` at runtime. No Apple-owned content is
   redistributed.

2. **Copyrightability of ABI metadata.** Layout, calling conventions,
   and entry-point symbols are functional facts, not creative
   expression. *Feist Publications v. Rural Telephone* (499 U.S. 340,
   1991): uncreative factual compilations are not copyrightable.
   *Google v. Oracle* (141 S. Ct. 1183, 2021): declaring API code
   reimplemented for interoperability is fair use. *17 U.S.C. §102(b)*
   excludes ideas, procedures, systems, and methods of operation from
   copyright's scope.

3. **Trademarks.** "Swift" is Apple's mark, but the Swift language
   itself is governed by Swift.org's permissive trademark policy
   (Apache 2.0), which allows descriptive use of the name.
   Nominative fair use covers the `SwiftBindings.*` package naming —
   descriptive of the factual target, not branding as Apple.

## Before each major publish

Re-check in case terms have shifted since the prior release:

- ADPLA current version — https://developer.apple.com/terms/ . Confirm
  §7.5 library carve-out is unchanged.
- Xcode and Apple SDKs Agreement (sibling of ADPLA, can change
  independently) — skim for new distribution restrictions on Swift
  interop or ABI metadata.
- Swift trademark policy — https://www.swift.org/community/#trademarks .
  Confirm descriptive use is still permitted.

If any of the three has materially changed, pause publication and
reassess before continuing.

## Incident response

- **Do not preemptively contact Apple licensing.** Opening a ticket
  creates a written record requesting permission for something the
  §7.5 carve-out already authorizes, and invites an ambiguous response
  that would be harder to proceed from than silence.
- **Do not ship Apple binaries, headers, or copied Apple source.** The
  §7.5 carve-out depends on this line holding. The generator reads
  Apple content at the consumer's build machine; the published NuGet
  package contains only derivative interop metadata.
- **If a third party raises a concern,** respond factually with
  reference to §7.5 and Feist / Google v. Oracle. Do not remove the
  package as a first response.
