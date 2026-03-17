#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Build the Swift wrapper library and SwiftBindingsRuntime for device (ios-arm64).
#
# Creates:
#   output/SwiftBindings.xcframework/ios-arm64/SwiftBindings.framework/
#   .build/SwiftBindingsRuntime.xcframework/ios-arm64/SwiftBindingsRuntime.framework/
#
# The device csproj (RuntimeTestsApp.Device.csproj) conditionally includes these
# as NativeReferences when the ios-arm64 slices exist.
#
# Usage: ./build-wrapper-device.sh

set -euo pipefail
cd "$(dirname "$0")"

SLICE_ID="ios-arm64"
SDK_NAME="iphoneos"
TARGET_TRIPLE="arm64-apple-ios15.0"
PLIST_PLATFORM="iPhoneOS"
MIN_OS="15.0"
MODULE_NAME="SwiftBindingsTestLib"
WRAPPER_MODULE="SwiftBindings"
OUTPUT_BASE="output"
XCFW_DIR=".build/${MODULE_NAME}.xcframework/$SLICE_ID"
OUTPUT_FW_DIR="${OUTPUT_BASE}/${WRAPPER_MODULE}.xcframework/$SLICE_ID/${WRAPPER_MODULE}.framework"

# --- Part 1: Build SwiftBindings wrapper for device ---

echo "=== Building ${WRAPPER_MODULE} wrapper (device) ==="

# Verify device slice of test library exists
if [ ! -d "$XCFW_DIR" ]; then
    echo "ERROR: Device slice missing: $XCFW_DIR"
    echo "Run: ./build-xcframework.sh --include-device"
    exit 1
fi

# Reuse the same post-processed Swift files from the simulator build if available,
# otherwise run the full post-processing pipeline from build-async-wrapper.sh.
CLEANED_DIR="${OUTPUT_BASE}/.wrapper-build"
if [ ! -d "$CLEANED_DIR" ] || [ -z "$(find "$CLEANED_DIR" -name "*.swift" -type f 2>/dev/null)" ]; then
    # Run the simulator wrapper build first (it does post-processing)
    echo "Post-processed Swift files not found. Running build-async-wrapper.sh first..."
    ./build-async-wrapper.sh --platform ios
    # The simulator build cleans up .wrapper-build, so re-create from output
    echo "Re-post-processing for device build..."
fi

# Collect Swift wrapper files
SWIFT_FILES=$(find "$OUTPUT_BASE" -maxdepth 1 -name "*.swift" ! -name "*.SwiftUIBridge.swift" -type f 2>/dev/null || true)
if [ -z "$SWIFT_FILES" ]; then
    echo "No Swift wrapper files found — skipping wrapper build."
    exit 0
fi

# Post-process (reuse the same Python pipeline from build-async-wrapper.sh)
rm -rf "$CLEANED_DIR"
mkdir -p "$CLEANED_DIR"

TOTAL_STRIPPED=0
for SWIFT_FILE in $SWIFT_FILES; do
    BASENAME=$(basename "$SWIFT_FILE")
    PY_OUTPUT=$(python3 - "$SWIFT_FILE" "$CLEANED_DIR/$BASENAME" <<'PYEOF'
import sys, re

input_path = sys.argv[1]
output_path = sys.argv[2]

with open(input_path) as f:
    lines = f.readlines()

def find_block_end(lines, start):
    depth = 0
    seen_open = False
    for j in range(start, len(lines)):
        depth += lines[j].count("{") - lines[j].count("}")
        if "{" in lines[j]:
            seen_open = True
        if seen_open and depth <= 0 and j > start:
            return j
    return len(lines) - 1

def scan_block_body(lines, start, end):
    return "".join(lines[start:end+1])

output_lines = []
removed_count = 0
i = 0
seen_utf8slice = False
seen_empty_buffer = False

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
def _references_preserved_protocol(body):
    return bool(_preserved_pattern.search(body))

while i < len(lines):
    line = lines[i]
    stripped = line.strip()

    if stripped.startswith("extension EveryProtocol") or stripped.startswith("class EveryProtocol"):
        end = find_block_end(lines, i)
        body = scan_block_body(lines, i, end)
        if not _references_preserved_protocol(body):
            removed_count += 1
            i = end + 1
            continue

    if stripped.startswith("@_silgen_name("):
        end = find_block_end(lines, i)
        body = scan_block_body(lines, i, end)
        broken = False
        if "EveryProtocol()" in body:
            if not _references_preserved_protocol(body):
                broken = True
        if not broken and "_self:" not in body and "_self :" not in body:
            for bline in lines[i:end+1]:
                s = bline.strip()
                if s.startswith("self.") or " self." in s or "\tself." in s:
                    broken = True
                    break
        if not broken and "__self.init(" in body:
            broken = True
        if not broken and ".load(as: (any " in body:
            if "existential." in body and "let existential" in body:
                broken = True
        if not broken and "Task {" in body:
            sig_end = body.find("{")
            if sig_end > 0:
                sig = body[:sig_end]
                closure_params = re.findall(r',\s*\w+:\s*\([^)]*\)\s*->', sig)
                if closure_params:
                    broken = True
        if broken:
            removed_count += 1
            i = end + 1
            continue

    if stripped.startswith("extension ") and not stripped.startswith("extension EveryProtocol"):
        end = find_block_end(lines, i)
        body = scan_block_body(lines, i, end)
        broken = False
        if "EveryProtocol()" in body:
            if not _references_preserved_protocol(body):
                broken = True
        if not broken and "__self.init(" in body:
            broken = True
        if not broken and "Task {" in body:
            sig_end = body.find("{")
            if sig_end > 0:
                closure_params = re.findall(r',\s*\w+:\s*\([^)]*\)\s*->', body[:body.find("Task {")])
                if closure_params:
                    broken = True
        if broken:
            removed_count += 1
            i = end + 1
            continue

    if stripped.startswith("public func SBW_") or stripped.startswith("public func PInvoke_"):
        end = find_block_end(lines, i)
        body = scan_block_body(lines, i, end)
        broken = False
        if "EveryProtocol()" in body:
            if not _references_preserved_protocol(body):
                broken = True
        if not broken and "let existential" in body and "existential." in body:
            if ".load(as: (any " in body:
                broken = True
        if broken:
            removed_count += 1
            i = end + 1
            continue

    if ") -> @escaping " in line:
        line = line.replace(") -> @escaping ", ") -> ")
    if ".load(as: @escaping " in line:
        line = line.replace(".load(as: @escaping ", ".load(as: ")

    is_utf8slice_block = False
    if stripped.startswith("public struct SBW_Utf8Slice"):
        is_utf8slice_block = True
    elif stripped == "@frozen" and i + 1 < len(lines) and "SBW_Utf8Slice" in lines[i+1]:
        is_utf8slice_block = True
    if is_utf8slice_block:
        if seen_utf8slice:
            end = find_block_end(lines, i)
            i = end + 1
            continue
        if stripped.startswith("public struct SBW_Utf8Slice"):
            seen_utf8slice = True
    if stripped.startswith("fileprivate var _sbw_emptyBuffer") or stripped.startswith("private var _sbw_emptyBuffer"):
        if seen_empty_buffer:
            i += 1
            continue
        seen_empty_buffer = True

    output_lines.append(line)
    i += 1

with open(output_path, "w") as f:
    f.writelines(output_lines)

print(f"  Stripped {removed_count} broken wrapper(s) from {sys.argv[1]}")
print(f"STRIP_COUNT:{removed_count}")
PYEOF
)
    FILE_STRIPPED=$(echo "$PY_OUTPUT" | grep STRIP_COUNT | cut -d: -f2)
    echo "$PY_OUTPUT" | grep -v STRIP_COUNT
    TOTAL_STRIPPED=$((TOTAL_STRIPPED + ${FILE_STRIPPED:-0}))
done

# Compile for device
CLEANED_FILES=$(find "$CLEANED_DIR" -name "*.swift" -type f 2>/dev/null || true)
if [ -z "$CLEANED_FILES" ]; then
    echo "No cleaned Swift files to compile."
    exit 0
fi

# Create device slice in xcframework (preserve existing simulator slice)
mkdir -p "$OUTPUT_FW_DIR"

SDK_PATH=$(xcrun --sdk "$SDK_NAME" --show-sdk-path)

COMPILE_LOG=$(mktemp /tmp/wrapper-device-compile-XXXXXX.log)
MAX_RETRIES=3
ATTEMPT=0
while [ $ATTEMPT -lt $MAX_RETRIES ]; do
    ATTEMPT=$((ATTEMPT + 1))
    set +e
    SDKROOT="" xcrun swiftc -emit-library -target "$TARGET_TRIPLE" \
        -sdk "$SDK_PATH" \
        -F "$XCFW_DIR/" \
        -module-name "$WRAPPER_MODULE" \
        -strict-concurrency=minimal \
        -Xlinker -install_name -Xlinker "@rpath/${WRAPPER_MODULE}.framework/${WRAPPER_MODULE}" \
        -o "$OUTPUT_FW_DIR/$WRAPPER_MODULE" \
        $CLEANED_FILES > "$COMPILE_LOG" 2>&1
    COMPILE_EXIT=$?
    set -e
    if [ $COMPILE_EXIT -eq 0 ]; then break; fi

    if [ $ATTEMPT -eq $MAX_RETRIES ]; then
        echo "Wrapper compilation failed after $MAX_RETRIES attempts:"
        grep "error:" "$COMPILE_LOG" | head -20
        echo ""
        echo "Continuing without wrapper library."
        rm -rf "$CLEANED_DIR" "$COMPILE_LOG"
        exit 0
    fi

    echo "Compilation attempt $ATTEMPT failed — stripping broken functions..."
    ERROR_FILE=$(mktemp)
    grep "error:" "$COMPILE_LOG" | head -80 > "$ERROR_FILE"

    python3 - "$CLEANED_DIR" "$ERROR_FILE" <<'STRIPEOF'
import sys, os, re

cleaned_dir = sys.argv[1]
error_file = sys.argv[2]
with open(error_file) as f:
    error_text = f.read()

file_error_lines = {}
for line in error_text.split("\n"):
    m = re.match(r"(.+\.swift):(\d+):\d+: error:", line)
    if m:
        filepath = os.path.basename(m.group(1))
        lineno = int(m.group(2))
        file_error_lines.setdefault(filepath, set()).add(lineno)

total_stripped = 0
for fname, error_lines in file_error_lines.items():
    fpath = os.path.join(cleaned_dir, fname)
    if not os.path.exists(fpath):
        continue
    with open(fpath) as f:
        lines = f.readlines()

    def find_block_end(lines, start):
        depth = 0
        seen_open = False
        for j in range(start, len(lines)):
            depth += lines[j].count("{") - lines[j].count("}")
            if "{" in lines[j]:
                seen_open = True
            if seen_open and depth <= 0 and j > start:
                return j
        return len(lines) - 1

    blocks_to_strip = set()
    i = 0
    while i < len(lines):
        stripped_line = lines[i].strip()
        if (stripped_line.startswith("@_cdecl(") or stripped_line.startswith("@_silgen_name(")
            or stripped_line.startswith("public func SBW_") or stripped_line.startswith("public func PInvoke_")
            or stripped_line.startswith("public func _sbw_")):
            end = find_block_end(lines, i)
            for eline in error_lines:
                if i + 1 <= eline <= end + 1:
                    blocks_to_strip.add((i, end))
                    break
            i = end + 1
        else:
            i += 1

    if not blocks_to_strip:
        continue

    expanded_blocks = set()
    for (start, end) in blocks_to_strip:
        actual_start = start
        while actual_start > 0:
            prev = lines[actual_start - 1].strip()
            if prev.startswith("@_cdecl(") or prev.startswith("@_silgen_name(") or prev.startswith("//") or prev.startswith("@MainActor"):
                actual_start -= 1
            else:
                break
        expanded_blocks.add((actual_start, end))

    skip_lines = set()
    for (start, end) in expanded_blocks:
        for j in range(start, end + 1):
            skip_lines.add(j)

    output_lines = [lines[j] for j in range(len(lines)) if j not in skip_lines]
    with open(fpath, "w") as f:
        f.writelines(output_lines)

    stripped_count = len(expanded_blocks)
    total_stripped += stripped_count
    print(f"  Stripped {stripped_count} broken function(s) from {fname}")
STRIPEOF

    rm -f "$ERROR_FILE"
    echo "Retrying compilation..."
done

echo "Device wrapper compilation succeeded (after $ATTEMPT attempt(s))."
rm -rf "$CLEANED_DIR" "$COMPILE_LOG"

# Create framework Info.plist
cat > "$OUTPUT_FW_DIR/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key>
    <string>com.swiftbindings.${WRAPPER_MODULE}</string>
    <key>CFBundleName</key>
    <string>${WRAPPER_MODULE}</string>
    <key>CFBundleExecutable</key>
    <string>${WRAPPER_MODULE}</string>
    <key>CFBundlePackageType</key>
    <string>FMWK</string>
    <key>CFBundleVersion</key>
    <string>1.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>MinimumOSVersion</key>
    <string>${MIN_OS}</string>
    <key>CFBundleSupportedPlatforms</key>
    <array>
        <string>${PLIST_PLATFORM}</string>
    </array>
</dict>
</plist>
EOF

# Update xcframework Info.plist to include device slice
# (preserve simulator slice if it exists)
XCFW_PLIST="${OUTPUT_BASE}/${WRAPPER_MODULE}.xcframework/Info.plist"
LIBRARIES=""

# Add simulator slice if it exists
if [ -d "${OUTPUT_BASE}/${WRAPPER_MODULE}.xcframework/ios-arm64-simulator" ]; then
    LIBRARIES="$LIBRARIES
        <dict>
            <key>LibraryIdentifier</key>
            <string>ios-arm64-simulator</string>
            <key>LibraryPath</key>
            <string>${WRAPPER_MODULE}.framework</string>
            <key>SupportedArchitectures</key>
            <array>
                <string>arm64</string>
            </array>
            <key>SupportedPlatform</key>
            <string>ios</string>
            <key>SupportedPlatformVariant</key>
            <string>simulator</string>
        </dict>"
fi

# Add device slice
LIBRARIES="$LIBRARIES
        <dict>
            <key>LibraryIdentifier</key>
            <string>ios-arm64</string>
            <key>LibraryPath</key>
            <string>${WRAPPER_MODULE}.framework</string>
            <key>SupportedArchitectures</key>
            <array>
                <string>arm64</string>
            </array>
            <key>SupportedPlatform</key>
            <string>ios</string>
        </dict>"

cat > "$XCFW_PLIST" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>AvailableLibraries</key>
    <array>${LIBRARIES}
    </array>
    <key>CFBundlePackageType</key>
    <string>XFWK</string>
    <key>XCFrameworkFormatVersion</key>
    <string>1.0</string>
</dict>
</plist>
EOF

echo "${WRAPPER_MODULE} device wrapper built successfully."

# --- Part 2: Build SwiftBindingsRuntime xcframework for device ---

echo ""
echo "=== Building SwiftBindingsRuntime xcframework (device) ==="

RUNTIME_DYLIB="../src/Swift.Runtime/native/ios/libSwiftBindingsRuntime.dylib"
RUNTIME_XCFW=".build/SwiftBindingsRuntime.xcframework"
RUNTIME_FW_DIR="${RUNTIME_XCFW}/ios-arm64/SwiftBindingsRuntime.framework"

if [ ! -f "$RUNTIME_DYLIB" ]; then
    echo "Device runtime dylib not found. Building..."
    (cd ../src/Swift.Runtime/swift && ./build-runtime.sh ios)
fi

mkdir -p "$RUNTIME_FW_DIR"
cp "$RUNTIME_DYLIB" "$RUNTIME_FW_DIR/SwiftBindingsRuntime"

# Fix install_name to use @rpath (original has absolute build path)
install_name_tool -id "@rpath/SwiftBindingsRuntime.framework/SwiftBindingsRuntime" \
    "$RUNTIME_FW_DIR/SwiftBindingsRuntime"

# Sign the framework binary for device
codesign --force --sign - "$RUNTIME_FW_DIR/SwiftBindingsRuntime" 2>/dev/null || true

cat > "$RUNTIME_FW_DIR/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key>
    <string>com.swiftbindings.SwiftBindingsRuntime</string>
    <key>CFBundleName</key>
    <string>SwiftBindingsRuntime</string>
    <key>CFBundleExecutable</key>
    <string>SwiftBindingsRuntime</string>
    <key>CFBundlePackageType</key>
    <string>FMWK</string>
    <key>CFBundleVersion</key>
    <string>1.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>MinimumOSVersion</key>
    <string>15.0</string>
    <key>CFBundleSupportedPlatforms</key>
    <array>
        <string>iPhoneOS</string>
    </array>
</dict>
</plist>
EOF

# Build xcframework Info.plist with device slice
RUNTIME_LIBS=""
if [ -d "${RUNTIME_XCFW}/ios-arm64-simulator" ]; then
    RUNTIME_LIBS="$RUNTIME_LIBS
        <dict>
            <key>LibraryIdentifier</key>
            <string>ios-arm64-simulator</string>
            <key>LibraryPath</key>
            <string>SwiftBindingsRuntime.framework</string>
            <key>SupportedArchitectures</key>
            <array>
                <string>arm64</string>
            </array>
            <key>SupportedPlatform</key>
            <string>ios</string>
            <key>SupportedPlatformVariant</key>
            <string>simulator</string>
        </dict>"
fi
RUNTIME_LIBS="$RUNTIME_LIBS
        <dict>
            <key>LibraryIdentifier</key>
            <string>ios-arm64</string>
            <key>LibraryPath</key>
            <string>SwiftBindingsRuntime.framework</string>
            <key>SupportedArchitectures</key>
            <array>
                <string>arm64</string>
            </array>
            <key>SupportedPlatform</key>
            <string>ios</string>
        </dict>"

cat > "${RUNTIME_XCFW}/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>AvailableLibraries</key>
    <array>${RUNTIME_LIBS}
    </array>
    <key>CFBundlePackageType</key>
    <string>XFWK</string>
    <key>XCFrameworkFormatVersion</key>
    <string>1.0</string>
</dict>
</plist>
EOF

echo "SwiftBindingsRuntime device xcframework built successfully."
echo ""
echo "=== Device wrapper build complete ==="
