#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Builds SwiftBindingsRuntime.xcframework — the shared Swift library that
# provides concurrency hook initialization for .NET interop, packaged as a
# proper framework inside an xcframework (Apple TN2435: a loose .dylib in an
# app's Frameworks/ is rejected on iOS/tvOS; it must live in a framework
# bundle). Each platform slice is a FLAT framework — the binary named
# `SwiftBindingsRuntime` plus an Info.plist at the .framework/ root, no
# Versions/A — exactly the shape SBApple and every generated binding ship, and
# the shape the .NET Apple workload embeds + signs from a single
# <NativeReference Kind="Framework">.
#
# Usage:
#   ./build-runtime.sh              # Build the full xcframework (all platforms)
#   ./build-runtime.sh all          # Same as above
#   ./build-runtime.sh <target>     # Build one platform's framework only (no xcframework)
#                                     <target> ∈ macos ios iossimulator maccatalyst tvos tvossimulator
#
# The committed artifact is ../native/SwiftBindingsRuntime.xcframework. Run this
# script (no args) to regenerate it after changing SwiftBindingsRuntime.swift or
# the collections shim, then commit the refreshed tree.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE="$SCRIPT_DIR/SwiftBindingsRuntime.swift"
# A small C translation unit holding cdecl wrappers for the seven Swift stdlib
# generic-collection ops whose direct CallConvSwift shape is mishandled by a
# Mono trampoline — six with sret + intermediate integer args + SwiftSelf
# (broken on Mac Catalyst-x64), plus Set.insert, whose mixed
# (Bool direct, @out Element via the first pointer argument) tuple return is
# broken on the iOS Simulator. Clang's `swiftcall` attribute +
# `swift_indirect_result` / `swift_context` parameter attrs lower the inner
# call via LLVM swiftcc, which is correct on every supported arch; C# enters
# via plain Cdecl, bypassing the broken trampoline. See
# SwiftBindingsRuntimeCollections.c for the rationale.
COLLECTIONS_SOURCE="$SCRIPT_DIR/SwiftBindingsRuntimeCollections.c"
OUTPUT_BASE="$SCRIPT_DIR/../native"

MODULE_NAME="SwiftBindingsRuntime"
# Install name is a relative @rpath token pointing INTO the framework bundle, so
# a consumer app can load it from .app/Frameworks/SwiftBindingsRuntime.framework/.
# This is the resolver's first search candidate
# (SwiftFrameworkResolver.GetSearchPaths). Without the framework-relative form,
# LC_ID_DYLIB would record either the absolute build path or a bare-dylib token
# that no longer matches the on-device layout.
INSTALL_NAME="@rpath/${MODULE_NAME}.framework/${MODULE_NAME}"

# Scratch staging for the per-platform .framework bundles before they are
# combined into the xcframework. Wiped and recreated on every full run.
STAGING="$OUTPUT_BASE/.xcframework-staging"

build_single_arch() {
    local output="$1"
    local sdk="$2"
    local triple="$3"

    local sdk_path
    sdk_path=$(xcrun --sdk "$sdk" --show-sdk-path)

    # Compile the cdecl-collections wrappers as a temporary object file using
    # the SAME -target triple as the Swift slice. clang's `swiftcall` lowering
    # is target-aware (x86_64 SysV vs arm64 AAPCS64), so the slice's exact
    # triple — not just the arch — matters for picking up macabi vs simulator
    # variants. The object lives next to the per-arch swiftc output and is
    # cleaned up by the caller via lipo/rm.
    # SDKROOT="" prevents clang from preferring an externally set sysroot over
    # the explicit per-slice `-isysroot` we pass here — without it, clang warns
    # about "using sysroot for 'MacOSX' but targeting 'iPhone'" on the iOS/tvOS
    # slices (objects are still correct, but the warning is misleading).
    local collections_obj="${output%.dylib}.collections.o"
    SDKROOT="" clang -c -O2 \
        -target "$triple" \
        -isysroot "$sdk_path" \
        -o "$collections_obj" \
        "$COLLECTIONS_SOURCE"

    # Unset SDKROOT to prevent clang sysroot mismatch warnings when
    # cross-compiling (swiftc uses the explicit -sdk flag instead).
    SDKROOT="" swiftc -emit-library \
        -o "$output" \
        -module-name "$MODULE_NAME" \
        -parse-as-library \
        -target "$triple" \
        -sdk "$sdk_path" \
        -Xlinker -install_name -Xlinker "$INSTALL_NAME" \
        "$SOURCE" \
        "$collections_obj"

    # The collections object is now linked into the dylib — drop the
    # intermediate file so it doesn't end up in the staging tree.
    rm -f "$collections_obj"
}

# Wraps a finished slice binary in a flat .framework bundle (binary + Info.plist
# at the root) and ad-hoc code-signs it. min_os / plist_platform mirror
# PlistGenerator.WriteFrameworkPlist. The runtime exposes only @_cdecl symbols
# (no Swift module interface), so there is no Modules/ directory — consumers
# P/Invoke it, they never `import` it.
make_framework() {
    local binary="$1"      # the built (possibly fat) dylib
    local fw_dir="$2"      # .../SwiftBindingsRuntime.framework
    local min_os="$3"
    local plist_platform="$4"

    mkdir -p "$fw_dir"
    cp "$binary" "$fw_dir/$MODULE_NAME"

    cat > "$fw_dir/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
    "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>${MODULE_NAME}</string>
    <key>CFBundleIdentifier</key>
    <string>com.swiftbindings.${MODULE_NAME}</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>${MODULE_NAME}</string>
    <key>CFBundlePackageType</key>
    <string>FMWK</string>
    <key>CFBundleVersion</key>
    <string>1.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>MinimumOSVersion</key>
    <string>${min_os}</string>
    <key>CFBundleSupportedPlatforms</key>
    <array>
        <string>${plist_platform}</string>
    </array>
</dict>
</plist>
PLIST

    codesign --force --sign - "$fw_dir/$MODULE_NAME" >/dev/null 2>&1 || \
        echo "  (warning: ad-hoc codesign of $fw_dir/$MODULE_NAME failed; consumer build will re-sign)"
}

# Builds one platform's flat framework into the staging tree and echoes the
# framework path (for create-xcframework) on the LAST line of stdout.
build_target() {
    local target="$1"
    local stage_dir="$STAGING/$target"
    local fw_dir="$stage_dir/${MODULE_NAME}.framework"
    local binary="$stage_dir/${MODULE_NAME}.dylib"

    mkdir -p "$stage_dir"
    echo "Building ${MODULE_NAME}.framework for $target..." >&2

    local min_os plist_platform
    case "$target" in
        macos)
            min_os="12.0"; plist_platform="MacOSX"
            local tmp_arm64="$stage_dir/_arm64.dylib"
            local tmp_x64="$stage_dir/_x64.dylib"
            build_single_arch "$tmp_arm64" "macosx" "arm64-apple-macosx12.0"
            build_single_arch "$tmp_x64"   "macosx" "x86_64-apple-macosx12.0"
            lipo -create "$tmp_arm64" "$tmp_x64" -output "$binary"
            rm -f "$tmp_arm64" "$tmp_x64"
            ;;
        ios)
            min_os="15.0"; plist_platform="iPhoneOS"
            build_single_arch "$binary" "iphoneos" "arm64-apple-ios15.0"
            ;;
        iossimulator)
            min_os="15.0"; plist_platform="iPhoneSimulator"
            local tmp_arm64="$stage_dir/_arm64.dylib"
            local tmp_x64="$stage_dir/_x64.dylib"
            build_single_arch "$tmp_arm64" "iphonesimulator" "arm64-apple-ios15.0-simulator"
            build_single_arch "$tmp_x64"   "iphonesimulator" "x86_64-apple-ios15.0-simulator"
            lipo -create "$tmp_arm64" "$tmp_x64" -output "$binary"
            rm -f "$tmp_arm64" "$tmp_x64"
            ;;
        maccatalyst)
            min_os="15.0"; plist_platform="MacOSX"
            local tmp_arm64="$stage_dir/_arm64.dylib"
            local tmp_x64="$stage_dir/_x64.dylib"
            build_single_arch "$tmp_arm64" "macosx" "arm64-apple-ios15.0-macabi"
            build_single_arch "$tmp_x64"   "macosx" "x86_64-apple-ios15.0-macabi"
            lipo -create "$tmp_arm64" "$tmp_x64" -output "$binary"
            rm -f "$tmp_arm64" "$tmp_x64"
            ;;
        tvos)
            min_os="15.0"; plist_platform="AppleTVOS"
            build_single_arch "$binary" "appletvos" "arm64-apple-tvos15.0"
            ;;
        tvossimulator)
            min_os="15.0"; plist_platform="AppleTVSimulator"
            local tmp_arm64="$stage_dir/_arm64.dylib"
            local tmp_x64="$stage_dir/_x64.dylib"
            build_single_arch "$tmp_arm64" "appletvsimulator" "arm64-apple-tvos15.0-simulator"
            build_single_arch "$tmp_x64"   "appletvsimulator" "x86_64-apple-tvos15.0-simulator"
            lipo -create "$tmp_arm64" "$tmp_x64" -output "$binary"
            rm -f "$tmp_arm64" "$tmp_x64"
            ;;
        *)
            echo "Unknown target: $target" >&2
            exit 1
            ;;
    esac

    make_framework "$binary" "$fw_dir" "$min_os" "$plist_platform"
    rm -f "$binary"
    echo "  -> $fw_dir" >&2
    echo "$fw_dir"
}

build_xcframework() {
    rm -rf "$STAGING"
    mkdir -p "$STAGING"

    local fw_args=()
    local t
    for t in macos ios iossimulator maccatalyst tvos tvossimulator; do
        local fw
        fw=$(build_target "$t")
        fw_args+=(-framework "$fw")
    done

    local xcframework="$OUTPUT_BASE/${MODULE_NAME}.xcframework"
    echo ""
    echo "Assembling ${MODULE_NAME}.xcframework from ${#fw_args[@]} framework args..."
    rm -rf "$xcframework"
    xcodebuild -create-xcframework "${fw_args[@]}" -output "$xcframework"

    rm -rf "$STAGING"
    echo ""
    echo "Built $xcframework"
}

TARGET="${1:-all}"

if [ "$TARGET" = "all" ]; then
    build_xcframework
else
    # Single-target mode: build just that platform's framework into the staging
    # tree (useful for inspecting one slice). Does NOT assemble the xcframework.
    mkdir -p "$STAGING"
    build_target "$TARGET" >/dev/null
    echo "Built $STAGING/$TARGET/${MODULE_NAME}.framework (single-target; run with no args to assemble the xcframework)."
fi
