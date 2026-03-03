# UnsupportedExistential Analysis (26 Skips)

> **Archived March 2026**: Moved to `Completed/`. Usability Session 3's `ExistentialBypassEmitter` covers the common default-arg patterns. The remaining 26 non-default-arg cases are narrow (library-specific provider/delegate protocols) and would require significant runtime extension for existential container construction from C#. Not worth pursuing — most of these are methods consumers rarely call directly.

**Priority**: Not prioritized — observation/research
**Area**: Generator — Existential handling
**Moved from**: `remaining-work.md` (February 2026)

---

## Overview

26 members across Nuke and Lottie are skipped with `UnsupportedExistential`. These are existential type arguments in bound generics where the parameter does **not** have a default value. Phase 51's `ExistentialBypassEmitter` handles the default-arg case. The non-default-arg case requires:

- Constructor/method signatures that accept `ExistentialContainer{N}` as a bound generic type argument
- C# callers to box their protocol-conforming object into a container
- Runtime support for existential container construction from C#

## Breakdown by Library

| Library | Existential Types | Count |
|---------|------------------|-------|
| Nuke | ImagePipelineDelegate, ImageProcessing, ImageDecoding, Error, anonymous | 10 |
| Lottie | AnimationImageProvider, AnyValueProvider, AnimationCacheProvider, DotLottieCacheProvider, Error, anonymous | 16 |

## Notes

Most are library-specific provider/delegate protocols. Addressing these would require the generator to emit methods that accept `ExistentialContainer` parameters in bound generic positions — a significant extension of the current existential support.
