#!/usr/bin/env bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Builds the StaticSwiftLib.xcframework fixture used by Bundle 8's
# detection-order regression test. Run from anywhere; outputs go alongside
# this script.
#
# Output shape mirrors an indoor-maps SDK's distribution:
#   StaticSwiftLib.xcframework/
#     Info.plist
#     ios-arm64-simulator/
#       libStaticSwiftLib.a              (static `ar archive`, NOT a dylib)
#       Modules/
#         StaticSwiftLib.swiftmodule/
#           arm64-apple-ios-simulator.swiftinterface
#           arm64-apple-ios-simulator.swiftmodule
#           arm64-apple-ios-simulator.abi.json
#
# Library evolution + module-interface emission are required so the
# .swiftinterface is real (not a stub) — that's what the SDK's Swift detector
# keys on. The binary stays a static archive so the binary-kind probe alone
# would misclassify it as ObjC; the fix is to check .swiftinterface first.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="${HERE}/Sources/StaticSwiftLib.swift"
OUT="${HERE}/StaticSwiftLib.xcframework"
WORK="${HERE}/.build"
MODULE="StaticSwiftLib"
TARGET="arm64-apple-ios15.0-simulator"
SLICE_DIR="${WORK}/${MODULE}-ios-arm64-simulator"
SDK_PATH="$(xcrun --sdk iphonesimulator --show-sdk-path)"

rm -rf "${OUT}" "${WORK}"
mkdir -p "${SLICE_DIR}/Modules/${MODULE}.swiftmodule"

# Compile the static slice.
xcrun -sdk iphonesimulator swiftc \
    -emit-module \
    -emit-module-interface \
    -emit-library \
    -static \
    -enable-library-evolution \
    -module-name "${MODULE}" \
    -target "${TARGET}" \
    -sdk "${SDK_PATH}" \
    -emit-module-path "${SLICE_DIR}/Modules/${MODULE}.swiftmodule/${TARGET}.swiftmodule" \
    -emit-module-interface-path "${SLICE_DIR}/Modules/${MODULE}.swiftmodule/${TARGET}.swiftinterface" \
    -o "${SLICE_DIR}/lib${MODULE}.a" \
    "${SRC}"

# xcframework wrapper. We hand-roll the Info.plist because `xcodebuild
# -create-xcframework` insists on a dylib for some configurations; this
# fixture deliberately ships the static-archive shape.
mkdir -p "${OUT}/ios-arm64-simulator"
cp -R "${SLICE_DIR}/." "${OUT}/ios-arm64-simulator/"

cat >"${OUT}/Info.plist" <<PLIST
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
            <string>lib${MODULE}.a</string>
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
PLIST

# Sanity-check: the binary must be a static `ar archive`, not a Mach-O dylib.
file "${OUT}/ios-arm64-simulator/lib${MODULE}.a" | grep -q "ar archive" \
    || { echo "ERROR: binary is not a static ar archive — fixture is invalid" >&2; exit 1; }

# And the .swiftinterface must be present and non-empty.
[ -s "${OUT}/ios-arm64-simulator/Modules/${MODULE}.swiftmodule/${TARGET}.swiftinterface" ] \
    || { echo "ERROR: .swiftinterface missing or empty" >&2; exit 1; }

rm -rf "${WORK}"

echo "Built ${OUT}"
