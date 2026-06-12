#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# add-swiftsupport-folder.sh — inject a compliant top-level SwiftSupport/<platform> folder
# into a .NET-for-iOS / .NET-for-tvOS App Store artifact. Two modes:
#
#   --mode ipa      <ipa>           : post-process a finished .ipa (the workload's CreateIpa /
#                                     `dotnet publish -p:BuildIpa=true` path). SwiftSupport/ is
#                                     grow-appended onto a scratch copy and swapped in atomically.
#   --mode archive  <xcarchive-dir> : populate the .xcarchive the workload's Archive target emits
#                                     (VS Publish / `ArchiveOnBuild=true`), so a later Xcode
#                                     Organizer "Distribute App" or `xcodebuild -exportArchive`
#                                     carries the folder into the IPA it produces.
#
# WHY THIS EXISTS (issue #42)
#   Apple requires a top-level `SwiftSupport/` folder inside the .ipa (a sibling of `Payload/`)
#   whenever the app ships Swift. Xcode populates it during "Distribute App → App Store Connect";
#   the .NET / MAUI build zips the .ipa directly (just `Payload/`) and never runs that pass, so
#   App Store Connect rejects the upload with ITMS-90426 ("Invalid Swift Support. The SwiftSupport
#   folder is missing."). A .xcarchive built by the workload likewise lacks the folder, so a later
#   Organizer / exportArchive distribution carries nothing forward → the same 90426.
#
#   The .xcarchive layout puts SwiftSupport/<platform>/ at the archive ROOT, sibling to
#   Products/Applications/*.app — and Xcode's App Store export carries that pre-existing folder
#   into the IPA verbatim. This is the exact anchor Microsoft's own Xamarin.iOS.SwiftRuntimeSupport
#   package wrote to ($(ArchiveDir)/SwiftSupport, AfterTargets="Archive"), honored by Apple's
#   distribution for years.
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
#   Invoked by buildTransitive/SwiftBindings.Runtime.targets. Arguments:
#     --mode <ipa|archive>   required
#     $1  target — the .ipa file (ipa mode) or the .xcarchive directory (archive mode)
#     $2  SwiftSupport platform subdir: "iphoneos" (iOS) or "appletvos" (tvOS)
#     $3  (optional) Xcode developer dir ($(_SdkDevPath)); falls back to `xcode-select -p`
#
#   Exit codes:
#     0  SwiftSupport written, OR nothing to do (no embeddable Swift dylibs / not an app artifact)
#     1  hard failure — a NON-WEAK /usr/lib/swift libswift the app references has no toolchain copy
#        (shipping an incomplete folder would still be rejected), or a usage/IO error. Fails the build.
#
# ROBUSTNESS NOTES (mirrors the Codex/Grok reviews folded into the design doc)
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
#   6. ipa mode: the new SwiftSupport/ folder is GROW-APPENDED (`zip -g`) onto a scratch copy of
#      the IPA and swapped over the original with an atomic `mv` — every existing top-level member
#      (Payload/, Symbols/, …) stays byte-for-byte untouched, and a failure mid-append can corrupt
#      only the discarded copy. `zip -y` preserves symlinks; no .DS_Store/__MACOSX is ever emitted.
#      archive mode: the .xcarchive is a plain directory, so the folder is written directly at the
#      archive root (no zip); only our own <archive>/SwiftSupport/<platform> is cleared first so a
#      re-archive is idempotent and we never disturb a sibling platform subdir.
#   7. The ACTIVE Xcode is used (passed-in developer dir or `xcode-select -p`) so the copied
#      dylibs always match the toolchain the artifact was built with.

set -euo pipefail

usage() {
  echo "usage: add-swiftsupport-folder.sh --mode <ipa|archive> <target> <platform-dir:iphoneos|appletvos> [developer-dir]" >&2
}

# ── Argument parsing ──────────────────────────────────────────────────────────────────────────
MODE=""
while [ $# -gt 0 ]; do
  case "$1" in
    --mode)   MODE="${2:-}"; shift 2 || { usage; exit 1; } ;;
    --mode=*) MODE="${1#--mode=}"; shift ;;
    --)       shift; break ;;
    -*)       echo "SwiftSupport ERROR: unknown flag '$1'." >&2; usage; exit 1 ;;
    *)        break ;;
  esac
done

case "$MODE" in
  ipa|archive) ;;
  *) echo "SwiftSupport ERROR: --mode must be 'ipa' or 'archive' (got '$MODE')." >&2; usage; exit 1 ;;
esac

TARGET="${1:-}"
PLATFORM_DIR="${2:-}"
DEVELOPER_DIR="${3:-}"
if [ -z "$TARGET" ] || [ -z "$PLATFORM_DIR" ]; then
  echo "SwiftSupport ERROR: missing target and/or platform subdir." >&2
  usage; exit 1
fi

# Resolve to an absolute path. ipa mode `cd`s into a work dir and references the target from there
# (a relative path would not resolve); archive mode writes under the target and is harmless to
# absolutize. $(IpaPackagePath)/$(ArchiveDir) are normally absolute; be defensive anyway.
case "$TARGET" in /*) ;; *) TARGET="$PWD/$TARGET" ;; esac

# Refinement 7: resolve the ACTIVE Xcode toolchain (matches the build).
if [ -z "$DEVELOPER_DIR" ] || [ ! -d "$DEVELOPER_DIR" ]; then
  DEVELOPER_DIR="$(xcode-select -p 2>/dev/null || true)"
fi
TOOLCHAIN="$DEVELOPER_DIR/Toolchains/XcodeDefault.xctoolchain/usr/lib"
if [ ! -d "$TOOLCHAIN" ]; then
  echo "SwiftSupport ERROR: Xcode toolchain lib dir not found at '$TOOLCHAIN' (developer dir: '$DEVELOPER_DIR'). Install Xcode or set the active developer dir with xcode-select." >&2
  exit 1
fi

# ── Shared helpers ────────────────────────────────────────────────────────────────────────────

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

# NOTE: macOS ships only bash 3.2 at /bin/bash, and the MSBuild Exec invokes us as
# `bash <script>` (shebang bypassed) — so associative arrays (declare -A, bash 4+) are NOT
# available on the build host. The "sets" below are space-delimited strings of basenames
# (libswift*.dylib names never contain spaces), with a tiny membership helper. set_contains
# is only ever used in `if`/`&&`/`||` context so `set -e` does not abort on its non-zero return.
set_contains() {  # set_contains "<space-delimited set>" "<item>"
  case " $1 " in *" $2 "*) return 0 ;; *) return 1 ;; esac
}

# build_swiftsupport <app-dir> <dest-dir>
#   The single shared core for both modes. Scans the .app's Mach-Os, computes the non-weak
#   /usr/lib/swift copy-set plus its transitive @rpath closure, HARD-FAILS (exit 1) on a missing
#   non-weak copy, and dittos the Apple-signed toolchain copies into <dest-dir>. <dest-dir> is
#   created only when there is ≥1 dylib to write. Sets global _COPIED to the count written (0 if
#   the app references no embeddable Swift runtime dylibs — stable-ABI/OS-only — i.e. nothing to do).
_COPIED=0
build_swiftsupport() {
  local app="$1" dest="$2"
  _COPIED=0

  # Refinement 4: collect every Mach-O that could pull in Swift. otool filters non-Mach-O, so
  # scanning all files under Frameworks/ and PlugIns/ is safe (and avoids relying on the +x
  # bit, which framework/dylib binaries do not reliably carry).
  local machos=()
  local exe
  exe="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$app/Info.plist" 2>/dev/null || true)"
  [ -n "$exe" ] && [ -f "$app/$exe" ] && machos+=("$app/$exe")
  local f
  while IFS= read -r -d '' f; do
    machos+=("$f")
  done < <(find "$app" \( -path '*/Frameworks/*' -o -path '*/PlugIns/*.appex/*' \) -type f -print0)

  # Aggregate referenced libswift dylibs across all Mach-Os; strong wins over weak.
  #   need_all       — every referenced libswift basename (strong or weak, os or rpath)
  #   need_strong_os — basenames referenced NON-weak via the /usr/lib/swift form by ≥1 Mach-O.
  #                    These are the genuine OS-runtime back-deployment needs whose absence is a
  #                    hard failure; @rpath refs are best-effort (see swift_refs).
  local need_all="" need_strong_os="" m kind origin base
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
  local missing_strong="" b
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
  local to_copy="" pending="" dep src
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
    return 0   # _COPIED stays 0 — nothing embeddable (stable-ABI / OS-only)
  fi

  # Refinement 5: ditto preserves Apple's signature; never re-sign.
  mkdir -p "$dest"
  for base in $to_copy; do
    src="$(find_copy "$base")"
    ditto "$src" "$dest/$base"
  done
  _COPIED="$(/bin/ls "$dest" | wc -l | tr -d ' ')"
}

# find_single_app <root>
#   Echo the single .app under <root> (matching <pattern>), or fail loudly. Used by both modes;
#   callers pass the mode-appropriate glob root.
find_single_app() {  # find_single_app <glob-root>
  local n
  n="$(/bin/ls -d "$1"/*.app 2>/dev/null | wc -l | tr -d ' ')"
  if [ "$n" = "0" ]; then echo ""; return 0; fi
  if [ "$n" != "1" ]; then
    echo "SwiftSupport ERROR: expected exactly one .app under '$1' but found $n. Aborting." >&2
    exit 1
  fi
  /bin/ls -d "$1"/*.app 2>/dev/null | head -1
}

# ── Mode: archive ─────────────────────────────────────────────────────────────────────────────
# The .xcarchive is a plain directory; write SwiftSupport/<platform> at its root, sibling to
# Products/. A later Organizer "Distribute App" / `xcodebuild -exportArchive` carries it into the
# IPA. Unlike a non-app IPA (which we skip), an archive without exactly one app is a real error:
# this target only runs for an app archive build.
if [ "$MODE" = "archive" ]; then
  if [ ! -d "$TARGET" ]; then
    echo "SwiftSupport ERROR: archive directory not found at '$TARGET'." >&2
    exit 1
  fi
  APP="$(find_single_app "$TARGET/Products/Applications")"
  if [ -z "$APP" ] || [ ! -d "$APP" ]; then
    echo "SwiftSupport ERROR: no .app found under '$TARGET/Products/Applications' — not an app archive. Aborting." >&2
    exit 1
  fi

  DEST="$TARGET/SwiftSupport/$PLATFORM_DIR"
  # Narrow, idempotent reset: only our own platform subdir, never a sibling platform's folder.
  rm -rf "$DEST"

  build_swiftsupport "$APP" "$DEST"
  if [ "$_COPIED" = "0" ]; then
    echo "SwiftSupport: no embeddable Swift runtime dylibs referenced (stable-ABI / OS-only) — nothing to add to the archive. Skipping."
    exit 0
  fi
  echo "SwiftSupport: wrote $_COPIED Apple-signed Swift dylib(s) to SwiftSupport/$PLATFORM_DIR in $(basename "$TARGET")."
  exit 0
fi

# ── Mode: ipa ─────────────────────────────────────────────────────────────────────────────────
IPA="$TARGET"
if [ ! -f "$IPA" ]; then
  echo "SwiftSupport ERROR: IPA not found at '$IPA'." >&2
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

APP="$(find_single_app "$WORK/Payload")"
if [ -z "$APP" ] || [ ! -d "$APP" ]; then
  echo "SwiftSupport: no .app found under Payload/ in '$IPA' — not an app IPA, skipping." >&2
  exit 0
fi

DEST="$WORK/SwiftSupport/$PLATFORM_DIR"
rm -rf "$WORK/SwiftSupport"
build_swiftsupport "$APP" "$DEST"
if [ "$_COPIED" = "0" ]; then
  echo "SwiftSupport: no embeddable Swift runtime dylibs referenced (stable-ABI / OS-only) — nothing to add. Skipping."
  exit 0
fi

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

echo "SwiftSupport: wrote $_COPIED Apple-signed Swift dylib(s) to SwiftSupport/$PLATFORM_DIR in $(basename "$IPA")."
