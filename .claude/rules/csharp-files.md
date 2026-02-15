---
paths:
  - "**/*.cs"
  - "**/*.swift"
---

# Copyright Headers for Source Files

Use `//` comment style for both C# and Swift files.

**New files** (original work):
```
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
```

**Modifying existing Microsoft files** (add Justin's line if not present):
```
// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
```

**Files derived from Microsoft code**:
```
// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
```

Files already containing Justin's copyright should not have their header modified.
