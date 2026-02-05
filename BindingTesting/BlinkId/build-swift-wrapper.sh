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

# Create Info.plist for the framework (required by iOS)
cat > SwiftBindings.framework/Info.plist << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key>
    <string>com.swiftbindings.SwiftBindings</string>
    <key>CFBundleName</key>
    <string>SwiftBindings</string>
    <key>CFBundleExecutable</key>
    <string>SwiftBindings</string>
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

echo "Swift wrapper built successfully"
