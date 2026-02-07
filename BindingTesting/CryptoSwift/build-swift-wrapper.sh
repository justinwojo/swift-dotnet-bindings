#!/bin/bash
# Build the Swift wrapper library for CryptoSwift bindings

set -e

cd "$(dirname "$0")/output-ios"

if [ ! -f "SwiftBindings.swift" ]; then
    echo "SwiftBindings.swift not found - run regenerate-bindings.sh first"
    exit 1
fi

# Note: Swift.CryptoSwift.swift (generated) has compilation issues with
# EveryProtocol conformances, internal-access methods, and argument labels.
# SwiftBindings.swift contains the working subset (ArraySlice wrappers)
# needed for runtime testing. Generator fixes tracked separately.
xcrun swiftc -emit-library -target arm64-apple-ios15.0-simulator \
  -sdk $(xcrun --sdk iphonesimulator --show-sdk-path) \
  -F ../CryptoSwift.xcframework/ios-arm64_x86_64-simulator/ \
  -module-name SwiftBindings \
  -Xlinker -install_name -Xlinker @rpath/SwiftBindings.framework/SwiftBindings \
  -o SwiftBindings.framework/SwiftBindings \
  SwiftBindings.swift

# Also update the xcframework copy (test app uses this)
cp SwiftBindings.framework/SwiftBindings SwiftBindings.xcframework/ios-arm64-simulator/SwiftBindings.framework/SwiftBindings
cp SwiftBindings.framework/Info.plist SwiftBindings.xcframework/ios-arm64-simulator/SwiftBindings.framework/Info.plist

echo "Swift wrapper built successfully"
