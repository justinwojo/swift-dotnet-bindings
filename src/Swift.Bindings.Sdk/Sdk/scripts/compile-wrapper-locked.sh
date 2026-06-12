#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Serializes Swift wrapper-xcframework compilation across concurrent MSBuild
# ProjectInstances that share one obj/.../swift-binding/ tree.
#
# Why this exists: in a parallel fan-in build (one leaf Swift-binding csproj
# referenced by many siblings — the "Stripe" shape), MSBuild can schedule the
# SAME leaf csproj in two ProjectInstances at once. Both reach _CompileSwiftWrapper
# sharing the same obj dir. Without a mutex, either (a) both invoke the generator
# and the second's `Directory.Delete(xcframework)` nukes the first's in-progress
# output, or (b) one compiles (~2-5s) while the other observes the early-created
# partial xcframework dir, skips, and validates the still-False binding-metadata.props
# — firing a spurious SWIFTBIND051. Holding an obj-dir-scoped lock across the whole
# "recheck completeness -> compile if needed -> publish metadata" region makes exactly
# one context compile; the others block, then observe the published HasWrapper=True and
# skip cleanly.
#
# Args:
#   $1  lock path (a directory is created at "<path>.d" as the atomic mutex)
#   $2  binding-metadata.props path (completeness is HasWrapper=True there)
#   $3  wrapper xcframework path (must also exist on disk to count as complete)
#   $4  command file: a per-context (GUID-named) shell script holding the generator
#       --compile-wrapper-only invocation; removed on exit so obj/ doesn't accumulate one per build
#
# Exit code: the generator's exit code when we compile; 0 when a peer already
# published a complete wrapper (nothing to do).
set -u

if [ "$#" -ne 4 ]; then
  echo "compile-wrapper-locked.sh: expected 4 args (lock, props, xcfw, cmd-file), got $#" >&2
  exit 2
fi

LOCK_BASE="$1"
PROPS="$2"
XCFW="$3"
CMD_FILE="$4"

LOCKDIR="${LOCK_BASE}.d"
STALE_MINUTES=20   # backstop: a wrapper compile is ~5s; a lock older than this is abandoned

is_complete() {
  # A genuinely-published wrapper: metadata flag flipped True AND the artifact on disk.
  [ -d "$XCFW" ] && grep -Eq '<_SwiftBindingHasWrapperXCFramework>[[:space:]]*True[[:space:]]*</_SwiftBindingHasWrapperXCFramework>' "$PROPS" 2>/dev/null
}

cleanup() {
  # Single exit handler for EVERY exit path (holder, waiter, or stealer; normal, error, or
  # signal). Three responsibilities:
  #   1. Remove any steal-capture dirs WE created (named with our pid) so a kill mid-capture
  #      doesn't litter obj/ with abandoned ".steal.<pid>.*" trees.
  #   2. Release the lock ONLY if we still own it (on-disk pid == $$). We can only ever be
  #      stolen while NOT live, and a steal is only initiated against a non-live holder (see
  #      the acquire loop), so a live owner's lock is never taken out from under it — but the
  #      pid==$$ fence is kept as defence in depth against the residual re-acquire race.
  #   3. Drop our per-context (GUID-named) cmd file so obj/ doesn't accumulate one per build.
  rm -rf "${LOCKDIR}.steal.$$."* 2>/dev/null
  if [ "$(cat "$LOCKDIR/pid" 2>/dev/null || true)" = "$$" ]; then
    rm -rf "$LOCKDIR"
  fi
  rm -f "$CMD_FILE"
}
# Arm the cleanup BEFORE the acquire loop so a waiter/stealer killed before it ever acquires
# still cleans up its captures and cmd file (and so a failed pid write can never leave an
# untrapped lockdir behind).
trap cleanup EXIT

steal_lock() {
  # Reclaim an abandoned lock WITHOUT the "rm -rf "$LOCKDIR" by path" hazard: a peer can
  # recover and re-acquire between our observation and our removal, and a blind by-path rm
  # would then delete the peer's freshly-stamped lock → a third context enters concurrently.
  # Instead, atomically CAPTURE the lockdir by renaming it aside (rename(2) is atomic; only
  # one waiter can win the rename of a given directory — losers get ENOENT and just retry),
  # and only ever rm the uniquely-named copy WE captured, never LOCKDIR by path. After
  # capturing, re-verify the holder is genuinely gone (pid absent => died before stamping;
  # or pid present but not live). If instead a LIVE holder re-acquired into our capture
  # (the irreducible re-acquire race of a portable mkdir/rename mutex — a peer recovered in
  # the gap between our observation and this rename), restore the lock to that holder rather
  # than discard it. The residual window during restore is sub-syscall, only reachable after
  # a prior abnormal exit, and the compile it guards is idempotent.
  local cap="${LOCKDIR}.steal.$$.${RANDOM}"
  mv "$LOCKDIR" "$cap" 2>/dev/null || return   # lost the capture race; a peer is handling it
  local p; p="$(cat "$cap/pid" 2>/dev/null || true)"
  if [ -z "$p" ] || ! kill -0 "$p" 2>/dev/null; then
    rm -rf "$cap"                                       # confirmed dead/empty holder — discard
  else
    mv "$cap" "$LOCKDIR" 2>/dev/null || rm -rf "$cap"   # live re-acquire raced us — give it back
  fi
}

# --- Acquire the obj-dir mutex (atomic mkdir; portable, no flock dependency) ---
while true; do
  if mkdir "$LOCKDIR" 2>/dev/null; then
    if ! echo $$ > "$LOCKDIR/pid"; then
      # Couldn't stamp ownership (disk full / IO error). Explicitly drop the lockdir —
      # cleanup() would skip it (pid file absent => not == $$) and leave a pid-less lock
      # that no liveness/steal path can reclaim until the mtime backstop. Fail loudly.
      rm -rf "$LOCKDIR"
      echo "compile-wrapper-locked.sh: failed to write lock pid file" >&2
      exit 3
    fi
    break
  fi

  # Lock is held. Reclaim it only when the holder is genuinely gone — NEVER preempt a live
  # holder (a merely-slow compile is not an abandoned lock).
  holder="$(cat "$LOCKDIR/pid" 2>/dev/null || true)"
  if [ -n "$holder" ] && ! kill -0 "$holder" 2>/dev/null; then
    steal_lock   # holder process is gone (build killed without releasing) — the common case
    continue
  fi
  if [ -z "$holder" ] && [ -n "$(find "$LOCKDIR" -maxdepth 0 -mmin "+${STALE_MINUTES}" 2>/dev/null)" ]; then
    # No pid was ever stamped (holder died in the mkdir→pid-write window) AND the lock is old.
    # A non-empty live pid is deliberately NOT stolen here, so a genuinely-slow holder is never
    # preempted (the trade is that the astronomically-rare pid-reuse case waits rather than risk
    # clobbering a live holder; a wedged build is cancelled and recovers via the dead-pid path).
    steal_lock
    continue
  fi
  sleep 0.2
done

# --- Critical section: only one context runs this at a time per obj dir ---
if is_complete; then
  # A concurrent (or prior) context already produced a complete wrapper. Nothing to do;
  # MSBuild's _UpdateSwiftWrapperMetadata will re-peek the published HasWrapper=True.
  exit 0
fi

bash "$CMD_FILE"
exit $?
