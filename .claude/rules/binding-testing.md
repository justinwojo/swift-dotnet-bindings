---
globs: ["BindingTesting/**"]
---

# Binding Testing Scripts

## Nuke (BindingTesting/Nuke/)
| Script | Purpose |
|--------|---------|
| `build-all.sh` | Full rebuild: bindings + Swift wrapper + test app |
| `regenerate-bindings.sh` | Regenerate C# bindings only |
| `build-swift-wrapper.sh` | Rebuild the Swift wrapper library |
| `build-testapp.sh` | Build the NukeTestApp |
| `validate-sim.sh [timeout]` | Run test app on iOS Simulator |

## BlinkIDUX (BindingTesting/BlinkId/) — Shadow Validation
| Script | Purpose |
|--------|---------|
| `build-all-bridge.sh` | Full pipeline: generator + bridge + test app |
| `validate-bridge.sh [timeout]` | Run bridge tests on iOS Simulator |

## BridgeParamTest (BindingTesting/BridgeTest/) — Shadow Validation
| Script | Purpose |
|--------|---------|
| `build-all.sh` | Full pipeline: xcframework + bindings + bridge + test app |
| `validate.sh [timeout]` | Run tests on iOS Simulator |

## validate-sim.sh Behavior
- Watches for `TEST SUCCESS` marker (exits early on success)
- Detects crashes via console output and crash log files
- Returns exit code 0 on success, 1 on failure/crash/timeout
- Always use this instead of manual `xcrun simctl` commands
