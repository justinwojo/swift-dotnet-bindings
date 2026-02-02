#!/bin/bash
# Regenerate BlinkID bindings from xcframework

set -e

cd "$(dirname "$0")"
PROJECT_ROOT="../.."

dotnet run --project "$PROJECT_ROOT/src/Swift.Bindings/src" -- \
  -a "BlinkID.xcframework/ios-arm64_x86_64-simulator/BlinkID.framework/Modules/BlinkID.swiftmodule/arm64-apple-ios-simulator.abi.json" \
  -d "BlinkID.xcframework/ios-arm64_x86_64-simulator/BlinkID.framework/BlinkID" \
  -t "output-ios/BlinkID.tbd" \
  -o "output-ios" -l BlinkID --async-library SwiftBindings
