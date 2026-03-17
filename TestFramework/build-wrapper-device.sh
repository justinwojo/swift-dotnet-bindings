#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Builds a universal SwiftBindings.xcframework with simulator + device slices.
# Uses build-async-wrapper.sh (with post-processing + error-based retry) for each
# platform separately, then combines into a single xcframework.
#
# Requires:
#   - .build/SwiftBindingsTestLib.xcframework/ with both ios-arm64 and ios-arm64-simulator slices
#   - output/SwiftBindingsTestLib.swift (generated wrapper source)
#
# Usage: ./build-wrapper-device.sh

set -e
cd "$(dirname "$0")"

XCFW=".build/SwiftBindingsTestLib.xcframework"

if [ ! -d "$XCFW/ios-arm64" ]; then
    echo "ERROR: Device slice not found in $XCFW"
    echo "Run ./build-xcframework.sh --include-device first."
    exit 1
fi

FINAL_XCFW="output/SwiftBindings.xcframework"
SIM_STAGING="output/.SwiftBindings-sim-staging.xcframework"
# Preserve the existing universal xcframework so we can restore it if the
# device compile fails. build-async-wrapper.sh deletes and recreates
# $FINAL_XCFW, so we must back it up before that runs.
BACKUP_XCFW="output/.SwiftBindings-backup.xcframework"
rm -rf "$BACKUP_XCFW"
if [ -d "$FINAL_XCFW" ]; then
    cp -R "$FINAL_XCFW" "$BACKUP_XCFW"
fi

echo "=== Building SwiftBindings wrapper (sim + device) ==="

# Step 1: Build simulator slice into staging area (using build-async-wrapper.sh)
echo "--- Simulator slice ---"
./build-async-wrapper.sh --platform ios --output-dir output 2>&1 | tail -5
# build-async-wrapper.sh writes into $FINAL_XCFW; move to staging
if [ ! -f "$FINAL_XCFW/ios-arm64-simulator/SwiftBindings.framework/SwiftBindings" ]; then
    echo "ERROR: Simulator wrapper build failed"
    # Restore backup if available
    rm -rf "$FINAL_XCFW"
    if [ -d "$BACKUP_XCFW" ]; then mv "$BACKUP_XCFW" "$FINAL_XCFW"; fi
    exit 1
fi
rm -rf "$SIM_STAGING"
mv "$FINAL_XCFW" "$SIM_STAGING"
echo "Simulator slice OK (staged)"

# Step 2: Build device slice (same post-processing, different target triple)
echo "--- Device slice ---"

# Build post-processed source for device slice
DEVICE_CLEAN_DIR="output/.wrapper-build-device"
rm -rf "$DEVICE_CLEAN_DIR"
mkdir -p "$DEVICE_CLEAN_DIR"

# Re-run the same post-processing as build-async-wrapper.sh
SWIFT_FILES=$(find output -maxdepth 1 -name "*.swift" ! -name "*.SwiftUIBridge.swift" -type f 2>/dev/null || true)
for SWIFT_FILE in $SWIFT_FILES; do
    BASENAME=$(basename "$SWIFT_FILE")
    python3 - "$SWIFT_FILE" "$DEVICE_CLEAN_DIR/$BASENAME" <<'PYEOF'
import sys, re

input_path = sys.argv[1]
output_path = sys.argv[2]

with open(input_path) as f:
    lines = f.readlines()

def find_block_end(lines, start):
    depth = 0; seen_open = False
    for j in range(start, len(lines)):
        depth += lines[j].count("{") - lines[j].count("}")
        if "{" in lines[j]: seen_open = True
        if seen_open and depth <= 0 and j > start: return j
    return len(lines) - 1

PRESERVED_PROTOCOLS = {
    "HasValue", "ExistentialParamDelegate",
    "ProcessingMode",
    "Describable", "TestIdentifiable", "Displayable",
    "Nameable", "Ageable", "Addable", "Subtractable", "Multipliable", "Dividable",
    "Named", "Prioritized",
    "TaskDescriptor", "StringProcessor",
    "StatusHandler", "PriorityHandler",
}
_preserved_pattern = re.compile(r'\b(' + '|'.join(re.escape(p) for p in PRESERVED_PROTOCOLS) + r')\b')
def _refs(body): return bool(_preserved_pattern.search(body))

output_lines = []; i = 0; seen_utf8slice = False; seen_empty_buffer = False
while i < len(lines):
    line = lines[i]; stripped = line.strip()
    if stripped.startswith("extension EveryProtocol") or stripped.startswith("class EveryProtocol"):
        end = find_block_end(lines, i); body = "".join(lines[i:end+1])
        if not _refs(body): i = end + 1; continue
    if stripped.startswith("@_silgen_name("):
        end = find_block_end(lines, i); body = "".join(lines[i:end+1]); broken = False
        if "EveryProtocol()" in body and not _refs(body): broken = True
        if not broken and "_self:" not in body and "_self :" not in body:
            for bline in lines[i:end+1]:
                s = bline.strip()
                if s.startswith("self.") or " self." in s or "\tself." in s: broken = True; break
        if not broken and "__self.init(" in body: broken = True
        if not broken and ".load(as: (any " in body and "existential." in body and "let existential" in body: broken = True
        if not broken and "Task {" in body:
            sig_end = body.find("{")
            if sig_end > 0:
                closure_params = re.findall(r',\s*\w+:\s*\([^)]*\)\s*->', body[:sig_end])
                if closure_params: broken = True
        if broken: i = end + 1; continue
    if stripped.startswith("extension ") and not stripped.startswith("extension EveryProtocol"):
        end = find_block_end(lines, i); body = "".join(lines[i:end+1]); broken = False
        if "EveryProtocol()" in body and not _refs(body): broken = True
        if not broken and "__self.init(" in body: broken = True
        if broken: i = end + 1; continue
    if stripped.startswith("public func SBW_") or stripped.startswith("public func PInvoke_"):
        end = find_block_end(lines, i); body = "".join(lines[i:end+1]); broken = False
        if "EveryProtocol()" in body and not _refs(body): broken = True
        if not broken and "let existential" in body and "existential." in body and ".load(as: (any " in body: broken = True
        if broken: i = end + 1; continue
    if ") -> @escaping " in line: line = line.replace(") -> @escaping ", ") -> ")
    if ".load(as: @escaping " in line: line = line.replace(".load(as: @escaping ", ".load(as: ")
    is_utf8slice = stripped.startswith("public struct SBW_Utf8Slice") or (stripped == "@frozen" and i+1 < len(lines) and "SBW_Utf8Slice" in lines[i+1])
    if is_utf8slice:
        if seen_utf8slice: end = find_block_end(lines, i); i = end + 1; continue
        if stripped.startswith("public struct SBW_Utf8Slice"): seen_utf8slice = True
    if (stripped.startswith("fileprivate var _sbw_emptyBuffer") or stripped.startswith("private var _sbw_emptyBuffer")):
        if seen_empty_buffer: i += 1; continue
        seen_empty_buffer = True
    output_lines.append(line); i += 1

with open(output_path, "w") as f: f.writelines(output_lines)
PYEOF
done

SDK_PATH=$(xcrun --sdk iphoneos --show-sdk-path)
DEVICE_FW_DIR="output/.wrapper-device-fw"
rm -rf "$DEVICE_FW_DIR"
mkdir -p "$DEVICE_FW_DIR/SwiftBindings.framework"

CLEANED_FILES=$(find "$DEVICE_CLEAN_DIR" -name "*.swift" -type f 2>/dev/null || true)
COMPILE_LOG=$(mktemp /tmp/wrapper-device-compile-XXXXXX.log)
MAX_RETRIES=3
ATTEMPT=0
while [ $ATTEMPT -lt $MAX_RETRIES ]; do
    ATTEMPT=$((ATTEMPT + 1))
    set +e
    xcrun swiftc -emit-library -target arm64-apple-ios15.0 \
        -sdk "$SDK_PATH" \
        -F "$XCFW/ios-arm64/" \
        -module-name SwiftBindings \
        -Xlinker -install_name -Xlinker "@rpath/SwiftBindings.framework/SwiftBindings" \
        -o "$DEVICE_FW_DIR/SwiftBindings.framework/SwiftBindings" \
        $CLEANED_FILES > "$COMPILE_LOG" 2>&1
    COMPILE_EXIT=$?
    set -e
    if [ $COMPILE_EXIT -eq 0 ]; then break; fi

    if [ $ATTEMPT -eq $MAX_RETRIES ]; then
        echo "Device wrapper compilation failed after $MAX_RETRIES attempts:"
        grep "error:" "$COMPILE_LOG" | head -20
        rm -rf "$DEVICE_CLEAN_DIR" "$DEVICE_FW_DIR" "$COMPILE_LOG" "$SIM_STAGING"
        # Restore the previous universal xcframework so --skip-regen still works
        if [ -d "$BACKUP_XCFW" ]; then
            mv "$BACKUP_XCFW" "$FINAL_XCFW"
            echo "Restored previous wrapper xcframework."
        fi
        exit 1
    fi

    echo "Device compilation attempt $ATTEMPT failed — stripping broken functions..."
    python3 -c "
import sys, os, re
cleaned_dir = '$DEVICE_CLEAN_DIR'
with open('$COMPILE_LOG') as f: error_text = f.read()
file_error_lines = {}
for line in error_text.split('\n'):
    m = re.match(r'(.+\.swift):(\d+):\d+: error:', line)
    if m:
        filepath = os.path.basename(m.group(1))
        file_error_lines.setdefault(filepath, set()).add(int(m.group(2)))
total = 0
for fname, error_lines in file_error_lines.items():
    fpath = os.path.join(cleaned_dir, fname)
    if not os.path.exists(fpath): continue
    with open(fpath) as f: lines = f.readlines()
    def find_block_end(lines, start):
        depth = 0; seen = False
        for j in range(start, len(lines)):
            depth += lines[j].count('{') - lines[j].count('}')
            if '{' in lines[j]: seen = True
            if seen and depth <= 0 and j > start: return j
        return len(lines) - 1
    blocks = set(); i = 0
    while i < len(lines):
        s = lines[i].strip()
        if s.startswith('@_cdecl(') or s.startswith('@_silgen_name(') or s.startswith('public func SBW_') or s.startswith('public func PInvoke_') or s.startswith('public func _sbw_'):
            end = find_block_end(lines, i)
            for e in error_lines:
                if i+1 <= e <= end+1:
                    actual_start = i
                    while actual_start > 0 and (lines[actual_start-1].strip().startswith('@_cdecl(') or lines[actual_start-1].strip().startswith('@_silgen_name(') or lines[actual_start-1].strip().startswith('//')):
                        actual_start -= 1
                    blocks.add((actual_start, end)); break
            i = end + 1
        else: i += 1
    if not blocks: continue
    skip = set()
    for (s, e) in blocks:
        for j in range(s, e+1): skip.add(j)
    out = [lines[j] for j in range(len(lines)) if j not in skip]
    with open(fpath, 'w') as f: f.writelines(out)
    total += len(blocks)
    print(f'  Stripped {len(blocks)} from {fname}')
print(f'Total stripped: {total}')
"
done
rm -f "$COMPILE_LOG"

if [ ! -f "$DEVICE_FW_DIR/SwiftBindings.framework/SwiftBindings" ]; then
    echo "ERROR: Device wrapper binary not produced"
    rm -rf "$DEVICE_CLEAN_DIR" "$DEVICE_FW_DIR" "$SIM_STAGING"
    if [ -d "$BACKUP_XCFW" ]; then
        mv "$BACKUP_XCFW" "$FINAL_XCFW"
        echo "Restored previous wrapper xcframework."
    fi
    exit 1
fi

# Create Info.plist for device framework
cat > "$DEVICE_FW_DIR/SwiftBindings.framework/Info.plist" << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>com.swiftbindings.SwiftBindings</string>
<key>CFBundleName</key><string>SwiftBindings</string>
<key>CFBundleExecutable</key><string>SwiftBindings</string>
<key>CFBundlePackageType</key><string>FMWK</string>
<key>MinimumOSVersion</key><string>15.0</string>
<key>CFBundleSupportedPlatforms</key><array><string>iPhoneOS</string></array>
</dict></plist>
EOF

echo "Device slice compiled successfully"

# Step 3: Combine into universal xcframework
echo "--- Creating universal xcframework ---"
WORK_DIR=".build/wrapper-combine"
rm -rf "$WORK_DIR"
mkdir -p "$WORK_DIR/sim" "$WORK_DIR/dev"

# Copy sim framework from staging
cp -R "$SIM_STAGING/ios-arm64-simulator/SwiftBindings.framework" "$WORK_DIR/sim/SwiftBindings.framework"

# Copy device framework
cp -R "$DEVICE_FW_DIR/SwiftBindings.framework" "$WORK_DIR/dev/SwiftBindings.framework"

# Recreate combined xcframework
rm -rf "$FINAL_XCFW"
xcodebuild -create-xcframework \
    -framework "$WORK_DIR/sim/SwiftBindings.framework" \
    -framework "$WORK_DIR/dev/SwiftBindings.framework" \
    -output "$FINAL_XCFW"

# Cleanup — remove staging, backup, and temp dirs on success
rm -rf "$DEVICE_CLEAN_DIR" "$DEVICE_FW_DIR" "$WORK_DIR" "$SIM_STAGING" "$BACKUP_XCFW"

echo ""
echo "=== Wrapper Build Complete ==="
echo "xcframework: $FINAL_XCFW"
ls -la "$FINAL_XCFW/"
