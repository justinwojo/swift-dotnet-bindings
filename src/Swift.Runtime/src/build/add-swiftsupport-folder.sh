#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# add-swiftsupport-folder.sh — inject a compliant top-level SwiftSupport/<platform> folder
# into a .NET-for-iOS / .NET-for-tvOS App Store IPA.
#
# WHY THIS EXISTS (issue #42)
#   Apple requires a top-level `SwiftSupport/` folder inside the .ipa (a sibling of
#   `Payload/`) whenever the app ships Swift. Xcode populates it during
#   "Distribute App → App Store Connect"; the .NET / MAUI build zips the .ipa directly
#   (just `Payload/`) and never runs that pass, so App Store Connect rejects the upload
#   with ITMS-90426 ("Invalid Swift Support. The SwiftSupport folder is missing.").
#
# WHAT GOES IN THE FOLDER (the non-obvious part)
#   `swift-stdlib-tool` is the WRONG tool here: a stable-ABI app embeds no @rpath
#   libswift*.dylib (it links the OS-resident /usr/lib/swift runtime), so that tool copies
#   nothing and the folder stays empty — which does NOT clear 90426. The folder must hold the
#   Apple-signed BACK-DEPLOYMENT copies of the Swift runtime dylibs the app actually uses, taken
#   straight from the active Xcode toolchain. The set is computed as a closure: every non-weak
#   /usr/lib/swift/libswift* the APP references directly, PLUS everything those toolchain copies
#   themselves pull in (which they reference as @rpath/libswift*) — see swift_refs / the closure
#   walk below for why both install-name forms must be matched. Their Apple signature must be
#   preserved verbatim (copy with `ditto`, never re-sign), or Apple rejects with a signing-ID
#   error.
#
# CONTRACT
#   Invoked by buildTransitive/SwiftBindings.Runtime.targets after the workload's CreateIpa
#   target (Strategy B — post-process the finished IPA in place). Arguments:
#     $1  IPA path (SwiftSupport/ grow-appended on a scratch copy, swapped in atomically)
#     $2  SwiftSupport platform subdir: "iphoneos" (iOS) or "appletvos" (tvOS)
#     $3  (optional) Xcode developer dir ($(_SdkDevPath)); falls back to `xcode-select -p`
#
#   Exit codes:
#     0  SwiftSupport written, OR nothing to do (no embeddable Swift dylibs / not an app IPA)
#     1  hard failure — a NON-WEAK /usr/lib/swift libswift the app references has no toolchain copy
#        (shipping an incomplete folder would still be rejected), or a usage/IO error. Fails the build.
#
# ROBUSTNESS NOTES (mirrors the Codex review folded into the design doc)
#   1. Weak vs non-weak: parsed from `otool -l` (LC_LOAD_DYLIB vs LC_LOAD_WEAK_DYLIB). A
#      non-weak /usr/lib/swift ref with no toolchain copy is a hard failure; a missing weak copy
#      (or any @rpath ref with no toolchain copy) is expected (OS-resident/embedded) and skipped.
#   2. Dependency closure: a copied dylib may itself reference further libswift dylibs not
#      directly referenced by the app — those are pulled in too (best-effort, copy-if-present).
#      The toolchain copies reference each other as @rpath/libswift*, so swift_refs matches BOTH
#      /usr/lib/swift/libswift* (app→OS) and @rpath/libswift* (copy→copy) or the closure misses them.
#   3. The `swift-*/<platform>` back-deployment dirs are discovered dynamically (never
#      hard-coded to swift-5.0 / 5.5 / 6.2 — they move between Xcode versions).
#   4. Every Mach-O is scanned: app executable, Frameworks/*.framework binaries, bare
#      Frameworks/*.dylib, and PlugIns/*.appex executables + their own frameworks.
#   5. `ditto` preserves Apple's code signature; the dylibs are NEVER re-signed.
#   6. The new SwiftSupport/ folder is GROW-APPENDED (`zip -g`) onto a scratch copy of the IPA
#      and swapped over the original with an atomic `mv`: every existing top-level member the
#      workload wrote (Payload/, and any Symbols//WatchKit support a profile emits) stays
#      byte-for-byte untouched — no recompress, no risk of dropping a sibling — and a failure
#      mid-append can corrupt only the discarded copy, never the finished artifact. `zip -y`
#      preserves symlinks; no .DS_Store/__MACOSX is ever emitted.
#   7. The ACTIVE Xcode is used (passed-in developer dir or `xcode-select -p`) so the copied
#      dylibs always match the toolchain the IPA was built with.

set -euo pipefail

IPA="${1:?usage: add-swiftsupport-folder.sh <ipa> <platform-dir:iphoneos|appletvos> [developer-dir]}"
PLATFORM_DIR="${2:?missing SwiftSupport platform subdir (iphoneos|appletvos)}"
DEVELOPER_DIR="${3:-}"

if [ ! -f "$IPA" ]; then
  echo "SwiftSupport ERROR: IPA not found at '$IPA'." >&2
  exit 1
fi
# Resolve to an absolute path: the scratch sidecar lives in the IPA's own directory (so the final
# `mv` is a same-filesystem atomic rename — see Refinement 6) and is referenced from inside the
# work dir after a `cd`, so a relative $IPA would not resolve. $(IpaPackagePath) is normally
# absolute; be defensive anyway.
case "$IPA" in /*) ;; *) IPA="$PWD/$IPA" ;; esac

# Refinement 7: resolve the ACTIVE Xcode toolchain (matches the build).
if [ -z "$DEVELOPER_DIR" ] || [ ! -d "$DEVELOPER_DIR" ]; then
  DEVELOPER_DIR="$(xcode-select -p 2>/dev/null || true)"
fi
TOOLCHAIN="$DEVELOPER_DIR/Toolchains/XcodeDefault.xctoolchain/usr/lib"
if [ ! -d "$TOOLCHAIN" ]; then
  echo "SwiftSupport ERROR: Xcode toolchain lib dir not found at '$TOOLCHAIN' (developer dir: '$DEVELOPER_DIR'). Install Xcode or set the active developer dir with xcode-select." >&2
  exit 1
fi

WORK="$(mktemp -d)"
# REPACKED (the scratch IPA copy) lives next to the real IPA, NOT under $WORK, so it is on the
# same filesystem and the final `mv` is an atomic rename (see Refinement 6). The EXIT trap must
# therefore clean both. It is assigned just before the copy; until then it is empty.
REPACKED=""
cleanup() { rm -rf "$WORK"; [ -n "$REPACKED" ] && rm -f "$REPACKED"; return 0; }
trap cleanup EXIT
unzip -q "$IPA" -d "$WORK"

APP="$(/bin/ls -d "$WORK"/Payload/*.app 2>/dev/null | head -1 || true)"
if [ -z "$APP" ] || [ ! -d "$APP" ]; then
  echo "SwiftSupport: no .app found under Payload/ in '$IPA' — not an app IPA, skipping." >&2
  exit 0
fi

# Refinement 3: discover the back-deployment dirs dynamically. Echoes the first matching
# toolchain copy for a given dylib basename, or nothing. `|| true` keeps `set -e`/pipefail
# from aborting on the no-match (ls non-zero) / SIGPIPE-from-head cases.
find_copy() {
  /bin/ls "$TOOLCHAIN"/swift-*/"$PLATFORM_DIR"/"$1" 2>/dev/null | head -1 || true
}

# Refinement 1: print "<strong|weak> <os|rpath> <basename>" for each libswift*.dylib load
# command of a Mach-O. Non-Mach-O inputs produce nothing (otool errors are suppressed), so
# callers can scan candidate files indiscriminately.
#
# Two install-name forms matter, and the distinction drives the hard-fail policy below:
#   os    — /usr/lib/swift/libswift*.dylib : how an APP binary references the OS-resident Swift
#           runtime it needs back-deployment copies of. A missing copy for a NON-WEAK os ref is a
#           hard failure (the SwiftSupport folder would be incomplete → ITMS-90426).
#   rpath — @rpath/libswift*.dylib         : how the toolchain's OWN back-deployment dylibs
#           reference each other (e.g. libswiftFoundation → @rpath/libswiftCore). The transitive
#           closure walk MUST match this form or it silently finds no further deps. These are
#           best-effort: copy if the toolchain has it, skip otherwise (it is OS-resident or
#           embedded) — never a hard failure.
swift_refs() {
  otool -l "$1" 2>/dev/null | awk '
    $1=="cmd" && $2=="LC_LOAD_DYLIB"      { kind="strong"; next }
    $1=="cmd" && $2=="LC_LOAD_WEAK_DYLIB" { kind="weak";   next }
    $1=="cmd"                             { kind="";       next }
    $1=="name" && kind!="" {
      origin=""
      if ($2 ~ /^\/usr\/lib\/swift\/libswift.*\.dylib$/) origin="os"
      else if ($2 ~ /^@rpath\/libswift.*\.dylib$/)       origin="rpath"
      if (origin != "") {
        n = split($2, parts, "/"); print kind " " origin " " parts[n]
      }
    }
  '
}

# Refinement 4: collect every Mach-O that could pull in Swift. otool filters non-Mach-O, so
# scanning all files under Frameworks/ and PlugIns/ is safe (and avoids relying on the +x
# bit, which framework/dylib binaries do not reliably carry).
machos=()
EXE="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP/Info.plist" 2>/dev/null || true)"
[ -n "$EXE" ] && [ -f "$APP/$EXE" ] && machos+=("$APP/$EXE")
while IFS= read -r -d '' f; do
  machos+=("$f")
done < <(find "$APP" \( -path '*/Frameworks/*' -o -path '*/PlugIns/*.appex/*' \) -type f -print0)

# NOTE: macOS ships only bash 3.2 at /bin/bash, and the MSBuild Exec invokes us as
# `bash <script>` (shebang bypassed) — so associative arrays (declare -A, bash 4+) are NOT
# available on the build host. The "sets" below are space-delimited strings of basenames
# (libswift*.dylib names never contain spaces), with a tiny membership helper. set_contains
# is only ever used in `if`/`&&`/`||` context so `set -e` does not abort on its non-zero return.
set_contains() {  # set_contains "<space-delimited set>" "<item>"
  case " $1 " in *" $2 "*) return 0 ;; *) return 1 ;; esac
}

# Aggregate referenced libswift dylibs across all Mach-Os; strong wins over weak.
#   need_all       — every referenced libswift basename (strong or weak, os or rpath)
#   need_strong_os — basenames referenced NON-weak via the /usr/lib/swift form by ≥1 Mach-O.
#                    These are the genuine OS-runtime back-deployment needs whose absence is a
#                    hard failure; @rpath refs are best-effort (see swift_refs).
need_all=""
need_strong_os=""
for m in "${machos[@]}"; do
  while read -r kind origin base; do
    [ -n "$base" ] || continue
    set_contains "$need_all" "$base" || need_all="$need_all $base"
    if [ "$kind" = "strong" ] && [ "$origin" = "os" ]; then
      set_contains "$need_strong_os" "$base" || need_strong_os="$need_strong_os $base"
    fi
  done < <(swift_refs "$m")
done

# Refinement 1 (hard fail): a non-weak /usr/lib/swift libswift with no Apple-signed toolchain
# copy means we cannot produce a complete folder — shipping a partial one would still be
# rejected (ITMS-90426). Fail the build loudly rather than emit something half-right.
missing_strong=""
for base in $need_strong_os; do
  [ -z "$(find_copy "$base")" ] && missing_strong="$missing_strong $base"
done
if [ -n "$(printf '%s' "$missing_strong" | tr -d '[:space:]')" ]; then
  {
    echo "SwiftSupport ERROR: these NON-WEAK Swift runtime dylibs are referenced by the app but"
    echo "have no Apple-signed back-deployment copy in the active toolchain"
    echo "($TOOLCHAIN/swift-*/$PLATFORM_DIR):"
    for b in $missing_strong; do echo "  - $b"; done
    echo "An incomplete SwiftSupport folder would still be rejected by App Store Connect (ITMS-90426)."
    echo "This usually means the Xcode toolchain does not match the build; aborting."
  } >&2
  exit 1
fi

# Build the copy set (basenames that HAVE a toolchain copy) + dependency closure.
# Weak refs with no copy are OS-only and correctly excluded.
to_copy=""
pending=""
for base in $need_all; do
  [ -n "$(find_copy "$base")" ] || continue
  set_contains "$to_copy" "$base" && continue
  to_copy="$to_copy $base"
  pending="$pending $base"
done

# Refinement 2: transitive closure over the copies' own libswift references. Pop the head of
# `pending` each iteration (basenames are whitespace-safe, so word-splitting via `set --` is fine).
while [ -n "$(printf '%s' "$pending" | tr -d '[:space:]')" ]; do
  set -- $pending
  base="$1"; shift; pending="$*"
  src="$(find_copy "$base")"
  [ -n "$src" ] || continue
  while read -r _kind _origin dep; do
    [ -n "$dep" ] || continue
    set_contains "$to_copy" "$dep" && continue
    [ -n "$(find_copy "$dep")" ] || continue   # OS-resident/embedded dependency — skip
    to_copy="$to_copy $dep"
    pending="$pending $dep"
  done < <(swift_refs "$src")
done

if [ -z "$(printf '%s' "$to_copy" | tr -d '[:space:]')" ]; then
  echo "SwiftSupport: no embeddable Swift runtime dylibs referenced (stable-ABI / OS-only) — nothing to add. Skipping."
  exit 0
fi

# Refinement 5: ditto preserves Apple's signature; never re-sign.
DEST="$WORK/SwiftSupport/$PLATFORM_DIR"
rm -rf "$WORK/SwiftSupport"
mkdir -p "$DEST"
for base in $to_copy; do
  src="$(find_copy "$base")"
  ditto "$src" "$DEST/$base"
done

# Refinement 6: add the SwiftSupport/ folder by GROW-APPENDING it, but stage the result on a
# scratch sidecar and swap it in atomically:
#   * copy the finished IPA to a sidecar IN THE IPA'S OWN DIRECTORY (a fast byte copy — far
#     cheaper than re-deflating Payload, the cost the old full-rewrite paid — and on the same
#     filesystem so the final `mv` below is a true atomic rename, not a cross-device copy+unlink);
#   * purge any pre-existing SwiftSupport/ from the copy so a re-run is idempotent (the workload
#     never emits one, but never leave stale/duplicate entries — covers a re-run on a fixed IPA);
#   * `zip -g` (grow) appends ONLY the new SwiftSupport/ entries: Payload/ and every other
#     top-level member the workload wrote stay byte-for-byte untouched (no recompress, no dropped
#     sibling, and the app signature inside Payload/ is untouched — we only add a sibling folder);
#   * `mv -f` over the original — a same-filesystem atomic rename. The original IPA is mutated
#     only by that rename, so a failure mid-append (I/O error, ENOSPC, signal) corrupts only the
#     discarded sidecar (cleaned by the EXIT trap), never the caller's finished artifact.
# -y preserves symlinks; the ditto'd dylibs are clean real files (no .DS_Store/__MACOSX emitted).
# The sidecar is a per-process DOTFILE so concurrent publishes do not collide and a stray glob
# never matches it. zip needs an absolute sidecar path (we `cd` into $WORK to name SwiftSupport/
# as a top-level entry); $IPA was absolutized at the top, so $(dirname "$IPA") is absolute too.
REPACKED="$(dirname "$IPA")/.swiftsupport-repack.$$.ipa"
cp "$IPA" "$REPACKED"
( cd "$WORK" \
    && { zip -dq "$REPACKED" 'SwiftSupport/*' 'SwiftSupport' >/dev/null 2>&1 || true; } \
    && zip -qry -g "$REPACKED" -- SwiftSupport )
mv -f "$REPACKED" "$IPA"
REPACKED=""  # ownership transferred to $IPA; nothing left for the trap to remove

echo "SwiftSupport: wrote $(/bin/ls "$DEST" | wc -l | tr -d ' ') Apple-signed Swift dylib(s) to SwiftSupport/$PLATFORM_DIR in $(basename "$IPA")."
