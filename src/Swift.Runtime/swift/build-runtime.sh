#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Builds libSwiftBindingsRuntime.dylib — the shared Swift library that
# provides concurrency hook initialization for .NET interop.
#
# Usage:
#   ./build-runtime.sh              # Build for macOS (default)
#   ./build-runtime.sh ios          # Build for iOS device
#   ./build-runtime.sh iossimulator # Build for iOS Simulator
#   ./build-runtime.sh all          # Build all targets

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE="$SCRIPT_DIR/SwiftBindingsRuntime.swift"
OUTPUT_BASE="$SCRIPT_DIR/../native"

build_single_arch() {
    local output="$1"
    local sdk="$2"
    local triple="$3"

    local sdk_path
    sdk_path=$(xcrun --sdk "$sdk" --show-sdk-path)

    # Unset SDKROOT to prevent clang sysroot mismatch warnings when
    # cross-compiling (swiftc uses the explicit -sdk flag instead)
    SDKROOT="" swiftc -emit-library \
        -o "$output" \
        -module-name SwiftBindingsRuntime \
        -parse-as-library \
        -target "$triple" \
        -sdk "$sdk_path" \
        "$SOURCE"
}

build_target() {
    local target="$1"
    local output_dir="$OUTPUT_BASE/$target"
    local output="$output_dir/libSwiftBindingsRuntime.dylib"

    mkdir -p "$output_dir"

    echo "Building libSwiftBindingsRuntime.dylib for $target..."

    case "$target" in
        macos)
            # Universal binary (arm64 + x86_64) for Intel and Apple Silicon
            local tmp_arm64="$output_dir/libSwiftBindingsRuntime_arm64.dylib"
            local tmp_x64="$output_dir/libSwiftBindingsRuntime_x64.dylib"

            build_single_arch "$tmp_arm64" "macosx" "arm64-apple-macosx12.0"
            build_single_arch "$tmp_x64"   "macosx" "x86_64-apple-macosx12.0"

            lipo -create "$tmp_arm64" "$tmp_x64" -output "$output"
            rm -f "$tmp_arm64" "$tmp_x64"
            ;;
        ios)
            build_single_arch "$output" "iphoneos" "arm64-apple-ios15.0"
            ;;
        iossimulator)
            # Universal binary (arm64 + x86_64) for Apple Silicon and Intel simulators
            local tmp_arm64="$output_dir/libSwiftBindingsRuntime_arm64.dylib"
            local tmp_x64="$output_dir/libSwiftBindingsRuntime_x64.dylib"

            build_single_arch "$tmp_arm64" "iphonesimulator" "arm64-apple-ios15.0-simulator"
            build_single_arch "$tmp_x64"   "iphonesimulator" "x86_64-apple-ios15.0-simulator"

            lipo -create "$tmp_arm64" "$tmp_x64" -output "$output"
            rm -f "$tmp_arm64" "$tmp_x64"
            ;;
        *)
            echo "Unknown target: $target"
            exit 1
            ;;
    esac

    echo "  -> $output"
}

TARGET="${1:-macos}"

if [ "$TARGET" = "all" ]; then
    build_target macos
    build_target ios
    build_target iossimulator
    echo ""
    echo "All targets built successfully."
else
    build_target "$TARGET"
fi
