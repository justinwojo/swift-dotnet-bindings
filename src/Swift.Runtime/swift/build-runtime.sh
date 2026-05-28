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
#   ./build-runtime.sh maccatalyst  # Build for Mac Catalyst
#   ./build-runtime.sh tvos         # Build for tvOS device
#   ./build-runtime.sh tvossimulator # Build for tvOS Simulator
#   ./build-runtime.sh all          # Build all targets

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE="$SCRIPT_DIR/SwiftBindingsRuntime.swift"
# A small C translation unit holding cdecl wrappers for the six Swift stdlib
# generic-collection ops whose direct CallConvSwift shape (sret + intermediate
# integer args + SwiftSelf) is mishandled by the Mac Catalyst-x64 workload Mono
# trampoline. Clang's `swiftcall` attribute + `swift_indirect_result` /
# `swift_context` parameter attrs lower the inner call via LLVM swiftcc, which
# is correct on every supported arch; C# enters via plain Cdecl, bypassing the
# broken trampoline. See SwiftBindingsRuntimeCollections.c for the rationale.
COLLECTIONS_SOURCE="$SCRIPT_DIR/SwiftBindingsRuntimeCollections.c"
OUTPUT_BASE="$SCRIPT_DIR/../native"

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
    # -Xlinker -install_name @rpath/...: the install name must be a relative
    # rpath token so consumer bundles can load this dylib from their
    # Frameworks/ directory. Without it, LC_ID_DYLIB records the absolute
    # build-machine path, leaking dev paths into shipped nupkgs and preventing
    # relocation to the app bundle.
    SDKROOT="" swiftc -emit-library \
        -o "$output" \
        -module-name SwiftBindingsRuntime \
        -parse-as-library \
        -target "$triple" \
        -sdk "$sdk_path" \
        -Xlinker -install_name -Xlinker "@rpath/libSwiftBindingsRuntime.dylib" \
        "$SOURCE" \
        "$collections_obj"

    # The collections object is now linked into the dylib — drop the
    # intermediate file so it doesn't end up in the published native tree.
    rm -f "$collections_obj"
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
        maccatalyst)
            # Universal binary (arm64 + x86_64) for Apple Silicon and Intel — uses macosx SDK with -macabi triples
            local tmp_arm64="$output_dir/libSwiftBindingsRuntime_arm64.dylib"
            local tmp_x64="$output_dir/libSwiftBindingsRuntime_x64.dylib"

            build_single_arch "$tmp_arm64" "macosx" "arm64-apple-ios15.0-macabi"
            build_single_arch "$tmp_x64"   "macosx" "x86_64-apple-ios15.0-macabi"

            lipo -create "$tmp_arm64" "$tmp_x64" -output "$output"
            rm -f "$tmp_arm64" "$tmp_x64"
            ;;
        tvos)
            # Single-arch (arm64 only) for tvOS device
            build_single_arch "$output" "appletvos" "arm64-apple-tvos15.0"
            ;;
        tvossimulator)
            # Universal binary (arm64 + x86_64) for Apple Silicon and Intel simulators
            local tmp_arm64="$output_dir/libSwiftBindingsRuntime_arm64.dylib"
            local tmp_x64="$output_dir/libSwiftBindingsRuntime_x64.dylib"

            build_single_arch "$tmp_arm64" "appletvsimulator" "arm64-apple-tvos15.0-simulator"
            build_single_arch "$tmp_x64"   "appletvsimulator" "x86_64-apple-tvos15.0-simulator"

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
    build_target maccatalyst
    build_target tvos
    build_target tvossimulator
    echo ""
    echo "All targets built successfully."
else
    build_target "$TARGET"
fi
