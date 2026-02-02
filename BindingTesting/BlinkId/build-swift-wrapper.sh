#!/bin/bash
# Build the Swift wrapper library

set -e

cd "$(dirname "$0")/output-ios"

# Check if Swift files exist
if [ ! -f "Swift.BlinkID.swift" ]; then
    echo "Error: Swift.BlinkID.swift not found. Run regenerate-bindings.sh first."
    exit 1
fi

# Create framework directory structure if needed
mkdir -p SwiftBindings.framework
mkdir -p SwiftBindings.xcframework/ios-arm64-simulator/SwiftBindings.framework

xcrun swiftc -emit-library -target arm64-apple-ios15.0-simulator \
  -sdk $(xcrun --sdk iphonesimulator --show-sdk-path) \
  -F ../BlinkID.xcframework/ios-arm64_x86_64-simulator/ \
  -module-name SwiftBindings \
  -Xlinker -install_name -Xlinker @rpath/SwiftBindings.framework/SwiftBindings \
  -o SwiftBindings.framework/SwiftBindings \
  Swift.BlinkID.swift SwiftBindings.swift

# Also update the xcframework copy (test app uses this)
cp SwiftBindings.framework/SwiftBindings SwiftBindings.xcframework/ios-arm64-simulator/SwiftBindings.framework/SwiftBindings

echo "Swift wrapper built successfully"
