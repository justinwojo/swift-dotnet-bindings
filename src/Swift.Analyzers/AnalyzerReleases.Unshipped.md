### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
SB1001  | Usage    | Info     | ISwiftObject can benefit from deterministic disposal
SB1002  | Reliability | Warning | Callback captures the Swift object it is attached to (possible retain cycle)
SB1003  | Reliability | Warning | Write through a Swift struct property mutates a temporary copy
