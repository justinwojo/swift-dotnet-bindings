#!/bin/bash
# Build the Swift wrapper library

set -e

cd "$(dirname "$0")/output-ios"

xcrun swiftc -emit-library -target arm64-apple-ios15.0-simulator \
  -sdk $(xcrun --sdk iphonesimulator --show-sdk-path) \
  -F ../Nuke.xcframework/ios-arm64_x86_64-simulator/ \
  -module-name SwiftBindings \
  -Xlinker -install_name -Xlinker @rpath/SwiftBindings.framework/SwiftBindings \
  -o SwiftBindings.framework/SwiftBindings Swift.Nuke.swift

# Also update the xcframework copy (test app uses this)
cp SwiftBindings.framework/SwiftBindings SwiftBindings.xcframework/ios-arm64-simulator/SwiftBindings.framework/SwiftBindings

echo "Swift wrapper built successfully"
