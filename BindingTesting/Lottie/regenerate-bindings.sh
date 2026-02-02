#!/bin/bash
# Regenerate Lottie bindings from xcframework

set -e

cd "$(dirname "$0")"
PROJECT_ROOT="../.."

# Generate TBD file if it doesn't exist or is empty
TBD_FILE="output-ios/Lottie.tbd"
DYLIB_PATH="Lottie.xcframework/ios-arm64_x86_64-simulator/Lottie.framework/Lottie"

if [ ! -s "$TBD_FILE" ]; then
    echo "Generating TBD file..."
    xcrun tapi stubify --filetype=tbd-v4 "$DYLIB_PATH" -o "$TBD_FILE"
fi

dotnet run --project "$PROJECT_ROOT/src/Swift.Bindings/src" -- \
  -a "Lottie.xcframework/ios-arm64_x86_64-simulator/Lottie.framework/Modules/Lottie.swiftmodule/arm64-apple-ios-simulator.abi.json" \
  -d "$DYLIB_PATH" \
  -t "$TBD_FILE" \
  -o "output-ios" -l Lottie --async-library SwiftBindings
