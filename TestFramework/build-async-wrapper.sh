#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Build the async Swift wrapper library for SwiftBindingsTestLib.
#
# Compiles the generator-produced Swift wrapper files (async method thunks)
# into a SwiftBindings.xcframework that the RuntimeTestsApp can reference.
#
# The generated Swift file may contain broken wrapper code for unsupported
# features (protocol proxy conformances, async closures, async inits).
# A post-processing step strips these before compilation.
#
# Usage: ./build-async-wrapper.sh

set -euo pipefail
cd "$(dirname "$0")"

MODULE_NAME="SwiftBindingsTestLib"
WRAPPER_MODULE="SwiftBindings"
XCFW_DIR=".build/${MODULE_NAME}.xcframework/ios-arm64-simulator"
OUTPUT_FW_DIR="output/${WRAPPER_MODULE}.xcframework/ios-arm64-simulator/${WRAPPER_MODULE}.framework"

# Collect generated Swift wrapper files (exclude SwiftUI bridge)
SWIFT_FILES=$(find output -maxdepth 1 -name "*.swift" ! -name "*.SwiftUIBridge.swift" -type f 2>/dev/null || true)

if [ -z "$SWIFT_FILES" ]; then
    echo "No Swift wrapper files found in output/ — skipping async wrapper build."
    exit 0
fi

FILE_COUNT=$(echo "$SWIFT_FILES" | wc -l | tr -d ' ')
echo "=== Building ${WRAPPER_MODULE} async wrapper ==="
echo "Swift wrapper files: $FILE_COUNT"

# Post-process: strip known-broken sections from generated Swift wrappers.
# The generator emits code for ALL features, including unsupported ones that
# produce uncompilable Swift. We strip these sections to allow the good async
# method wrappers to compile.
echo "Post-processing Swift wrappers..."
CLEANED_DIR="output/.wrapper-build"
rm -rf "$CLEANED_DIR"
mkdir -p "$CLEANED_DIR"

TOTAL_STRIPPED=0
for SWIFT_FILE in $SWIFT_FILES; do
    BASENAME=$(basename "$SWIFT_FILE")
    PY_OUTPUT=$(python3 - "$SWIFT_FILE" "$CLEANED_DIR/$BASENAME" <<'PYEOF'
import sys

input_path = sys.argv[1]
output_path = sys.argv[2]

with open(input_path) as f:
    lines = f.readlines()

def find_block_end(lines, start):
    """Find the end of a brace-delimited block starting at `start`."""
    depth = 0
    for j in range(start, len(lines)):
        depth += lines[j].count("{") - lines[j].count("}")
        if depth <= 0 and j > start:
            return j
    return len(lines) - 1

def scan_block_body(lines, start, end):
    """Return concatenated text of lines[start..end]."""
    return "".join(lines[start:end+1])

output_lines = []
removed_count = 0
i = 0

# Protocols to preserve for runtime testing (Session 6+).
# EveryProtocol conformances for these protocols are kept so proxy dispatch works at runtime.
PRESERVED_PROTOCOLS = {"HasValue", "ExistentialParamDelegate"}

while i < len(lines):
    line = lines[i]
    stripped = line.strip()

    # Pattern 1: Skip EveryProtocol conformance extensions and class definition,
    # EXCEPT those for preserved protocols needed for runtime testing.
    if stripped.startswith("extension EveryProtocol") or stripped.startswith("class EveryProtocol"):
        end = find_block_end(lines, i)
        body = scan_block_body(lines, i, end)
        # Preserve if the block references a preserved protocol
        preserve = any(p in body for p in PRESERVED_PROTOCOLS)
        if not preserve:
            removed_count += 1
            i = end + 1
            continue

    # Pattern 2: Skip @_silgen_name + function blocks that have broken patterns.
    # Detect the start of a @_silgen_name / function pair, scan the body for
    # known-broken patterns, and skip the entire block if found.
    if stripped.startswith("@_silgen_name("):
        # @_silgen_name line, followed by function on next line(s)
        end = find_block_end(lines, i)
        body = scan_block_body(lines, i, end)

        # Check for known-broken patterns:
        broken = False

        # (a) EveryProtocol() — protocol witness dispatch for unimplemented conformances
        # Preserve if the block references a preserved protocol (Session 6+)
        if "EveryProtocol()" in body:
            preserve = any(p in body for p in PRESERVED_PROTOCOLS)
            if not preserve:
                broken = True

        # (b) self.functionName() in free function (no _self: parameter)
        if not broken and "_self:" not in body and "_self :" not in body:
            # Free function — check for bare `self.` reference
            for bline in lines[i:end+1]:
                s = bline.strip()
                if s.startswith("self.") or " self." in s or "\tself." in s:
                    broken = True
                    break

        # (c) __self.init( — async init wrapper (invalid Swift)
        if not broken and "__self.init(" in body:
            broken = True

        # (d) mutating member on let existential
        if not broken and ".load(as: (any " in body:
            # Check if loaded existential is used with mutating method
            if "existential." in body and "let existential" in body:
                broken = True

        # (e) Non-escaping closure param passed to Task (async closure methods)
        # The function signature has a closure param like `(Int32) -> Int32`
        # that's non-escaping but used inside a Task { } block.
        if not broken and "Task {" in body:
            # Check if any param is a non-escaping closure (contains `->` but no `@escaping`)
            sig_end = body.find("{")
            if sig_end > 0:
                sig = body[:sig_end]
                # Closure param pattern: `name: (Type) -> Type` without @escaping
                import re
                closure_params = re.findall(r',\s*\w+:\s*\([^)]*\)\s*->', sig)
                if closure_params:
                    # Has closure param + Task block = non-escaping issue
                    broken = True

        if broken:
            removed_count += 1
            i = end + 1
            continue

    # Pattern 3: Skip extension blocks that contain broken code.
    if stripped.startswith("extension ") and not stripped.startswith("extension EveryProtocol"):
        end = find_block_end(lines, i)
        body = scan_block_body(lines, i, end)

        broken = False
        if "EveryProtocol()" in body:
            preserve = any(p in body for p in PRESERVED_PROTOCOLS)
            if not preserve:
                broken = True
        if not broken and "__self.init(" in body:
            broken = True
        # Non-escaping closure in Task
        if not broken and "Task {" in body:
            sig_end = body.find("{")
            if sig_end > 0:
                import re
                closure_params = re.findall(r',\s*\w+:\s*\([^)]*\)\s*->', body[:body.find("Task {")])
                if closure_params:
                    broken = True

        if broken:
            removed_count += 1
            i = end + 1
            continue

    # Also catch standalone public func blocks (without @_silgen_name prefix)
    if stripped.startswith("public func SBW_") or stripped.startswith("public func PInvoke_"):
        end = find_block_end(lines, i)
        body = scan_block_body(lines, i, end)

        broken = False
        if "EveryProtocol()" in body:
            preserve = any(p in body for p in PRESERVED_PROTOCOLS)
            if not preserve:
                broken = True
        if not broken and "let existential" in body and "existential." in body:
            if ".load(as: (any " in body:
                broken = True

        if broken:
            removed_count += 1
            i = end + 1
            continue

    # Fix: Strip @escaping from return type position (only valid in parameter position).
    # Generator sometimes emits `-> @escaping (Type) -> Type` for closure-returning methods.
    if ") -> @escaping " in line:
        line = line.replace(") -> @escaping ", ") -> ")

    # Default: keep the line
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
echo "$TOTAL_STRIPPED" > "output/wrapper-stripped-count"

# Build cleaned files list
CLEANED_FILES=$(find "$CLEANED_DIR" -name "*.swift" -type f 2>/dev/null || true)
if [ -z "$CLEANED_FILES" ]; then
    echo "No cleaned Swift files to compile."
    exit 0
fi

# Create output framework structure
rm -rf "output/${WRAPPER_MODULE}.xcframework"
mkdir -p "$OUTPUT_FW_DIR"

SDK_PATH=$(xcrun --sdk iphonesimulator --show-sdk-path)

# Compile cleaned wrapper files, linking against the test library framework
xcrun swiftc -emit-library -target arm64-apple-ios15.0-simulator \
    -sdk "$SDK_PATH" \
    -F "$XCFW_DIR/" \
    -module-name "$WRAPPER_MODULE" \
    -Xlinker -install_name -Xlinker "@rpath/${WRAPPER_MODULE}.framework/${WRAPPER_MODULE}" \
    -o "$OUTPUT_FW_DIR/$WRAPPER_MODULE" \
    $CLEANED_FILES

# Clean up temporary directory
rm -rf "$CLEANED_DIR"

# Create Info.plist
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
    <string>15.0</string>
    <key>CFBundleSupportedPlatforms</key>
    <array>
        <string>iPhoneSimulator</string>
    </array>
</dict>
</plist>
EOF

# Create xcframework Info.plist
cat > "output/${WRAPPER_MODULE}.xcframework/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>AvailableLibraries</key>
    <array>
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
        </dict>
    </array>
    <key>CFBundlePackageType</key>
    <string>XFWK</string>
    <key>XCFrameworkFormatVersion</key>
    <string>1.0</string>
</dict>
</plist>
EOF

echo "${WRAPPER_MODULE} async wrapper framework built successfully"
