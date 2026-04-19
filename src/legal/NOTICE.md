# NOTICE

`SwiftBindings.*` is an independent Swift/.NET interoperability toolkit.
It is not affiliated with, endorsed by, or sponsored by Apple Inc.

"Swift" is a trademark of Apple Inc., used here descriptively to refer to
the Swift programming language, in accordance with the Swift.org
community trademark policy (Apache License 2.0).

The NuGet packages produced by this project ship interoperability
metadata only — managed (C#) projections describing the shape (layout,
size, stride, alignment, method signatures, ABI entry-point symbols) of
Apple-framework Swift types. They do not contain, redistribute, or
derive from Apple SDK headers, source code, compiled binaries, or
documentation. Apple SDK materials are read on the consumer's build
machine at generation time and resolved via `dlsym` at runtime.

Consumers building applications against Apple SDKs remain solely
responsible for their own compliance with the Apple Developer Program
License Agreement and any other applicable Apple terms.

---

Copyright © Justin Wojciechowski. Distributed under the MIT License.
See `LICENSE` at the repository root for full terms.
