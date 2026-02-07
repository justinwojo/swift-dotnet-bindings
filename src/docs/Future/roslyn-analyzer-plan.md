# Roslyn Analyzer for Undisposed Swift Objects

**Priority**: P3
**Area**: Tooling
**Moved from**: `remaining-work.md` (February 2026)

---

## Overview

A Roslyn analyzer can warn at compile time when Swift objects implementing `IDisposable` are created without `using` or explicit `Dispose()`.

## What's Needed

1. Create analyzer project targeting `ISwiftObject` / `SwiftSafeHandle<T>` types
2. Warn on: local variables without `using`, field assignments without dispose in containing type
3. Package as NuGet alongside `Swift.Runtime`

## Acceptance Criteria

- [ ] Analyzer warns on undisposed `SwiftSafeHandle<T>` locals
- [ ] Analyzer packaged and included in `Swift.Runtime` NuGet
- [ ] No false positives on properly disposed objects
