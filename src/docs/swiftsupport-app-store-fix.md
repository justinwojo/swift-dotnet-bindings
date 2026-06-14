# SwiftSupport folder for App Store submission (issue #42) — SUPERSEDED

Status: **SUPERSEDED / reverted (2026-06-14).** This approach — injecting a top-level
`SwiftSupport/<platform>` folder into the IPA/`.xcarchive` — was the **wrong fix** for issue #42.
It treated a symptom of a misleading Apple error and never made the reporter's app pass. The real
root cause and the implemented fix are in **[`runtime-framework-packaging.md`](runtime-framework-packaging.md)**.

This doc is kept (not deleted) so a future session understands *why* the first attempt was wrong
and does not re-attempt it. The reverted implementation — the injector script, the two `.targets`
hooks, the `EnableSwiftSupportFolder` property, and the old `--swiftsupport` gate — lives in git
history (removed in the 2026-06-14 framework-repackaging change). Originally captured 2026-06-11,
extended 2026-06-12; superseded 2026-06-14.

---

## What we originally saw, and the misdiagnosis

GitHub issue #42 (reporter `carljohansen`): a NuGet binding for the **Kidoz** Swift SDK, referenced
from a **.NET 10 MAUI iOS** app, deployed to a physical iPhone fine, but App Store Connect rejected
the upload — first with:

```
ITMS-90426: Invalid Swift Support. The SwiftSupport folder is missing.
```

We read that error literally: "the SwiftSupport folder is missing" → "add a SwiftSupport folder."
So we built an injector (`add-swiftsupport-folder.sh`) that scanned the app's Mach-Os for their
back-deployment `libswift*.dylib` closure and copied those Apple-signed dylibs into a top-level
`SwiftSupport/iphoneos` folder — on both the direct `BuildIpa` path and the `.xcarchive` →
Xcode Organizer path the reporter actually uses.

**That was chasing a misleading error.** Adding the folder did not unblock the app. It:
- converted `ITMS-90426` into **`ITMS-90429`** ("files … aren't at the expected location
  /Frameworks") and the sibling **`ITMS-90171`** — i.e. it surfaced the *actual* problem rather
  than fixing it, and
- bloated the reporter's `.xcarchive` from **138 MB → 185 MB** (~47 MB of duplicated `libswift*`
  back-deployment dylibs) — a size regression the reporter flagged.

## The real root cause (why the folder approach could never work)

Apple **TN2435** ("Embedding Frameworks In An App", section *Embedded .dylib Files*):

> "Dynamic libraries outside of a framework bundle, which typically have the file extension
> `.dylib`, are not supported on iOS, watchOS, or tvOS, except for the system Swift libraries
> provided by Xcode."

The binding dropped our runtime into the app as a **bare, loose `libSwiftBindingsRuntime.dylib`**
in `App.app/Frameworks/`. It is a Swift Mach-O whose name does not begin with `libswift` — exactly
the shape TN2435 says you must convert to a framework. The `ITMS-90426` / `90429` / `90171`
rejections are all **symptoms of that one loose dylib**, not of a genuinely missing SwiftSupport
folder. A SwiftSupport folder is only relevant when an app actually *embeds* back-deployment
`libswift*.dylib` files; a correctly built, stable-ABI app (min iOS 15, linking the OS-resident
`/usr/lib/swift`) embeds **none** and needs **no** SwiftSupport folder — which is what a normal
Xcode Swift app produces.

So the fix is not "add a folder." It is "stop shipping a loose dylib": package the runtime as
`SwiftBindingsRuntime.framework` (inside an xcframework), embedded via `<NativeReference
Kind="Framework">` — the exact shape every other Swift framework in this repo already uses — and
delete the SwiftSupport injector entirely. The reporter's own hypothesis was correct; two
independent reviewers (Codex, Grok) and TN2435 concur.

## Where the implemented fix lives

- **[`runtime-framework-packaging.md`](runtime-framework-packaging.md)** — the implemented design
  (decisions D1–D6), reviewed by Codex + Grok.
- The TN2435-hygiene gate replacing the old SwiftSupport gate: `nuke binding-tests
  --appstore-hygiene` (`build/Build.BindingTests.AppStoreHygiene.cs`) — asserts the runtime embeds as
  a signed `SwiftBindingsRuntime.framework`, the app embeds zero `libswift*.dylib`, and no
  SwiftSupport folder is present.

## The lesson (for a future session)

If an iOS App Store rejection mentions SwiftSupport or `/Frameworks` location, first check
**whether the app embeds any loose, non-`libswift*` `.dylib`** in `Frameworks/`. If it does, that
loose dylib is the root cause (TN2435) — convert it to a framework. Do **not** reach for a
SwiftSupport-folder injector: that only applies to apps legitimately back-deploying `libswift*`,
and for a stable-ABI app it adds tens of MB while masking the real defect. A genuine future
back-deployment need (embed `libswift*` in `Frameworks/` **and** mirror into SwiftSupport,
`swift-stdlib-tool` style) would be written fresh against that real requirement, not resurrected
from this reverted code.
