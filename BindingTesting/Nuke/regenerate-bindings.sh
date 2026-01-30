#!/bin/bash
# Regenerate Nuke bindings from xcframework

set -e

cd "$(dirname "$0")"
PROJECT_ROOT="../.."

dotnet run --project "$PROJECT_ROOT/src/Swift.Bindings/src" -- \
  -a "Nuke.xcframework/ios-arm64_x86_64-simulator/Nuke.framework/Modules/Nuke.swiftmodule/arm64-apple-ios-simulator.abi.json" \
  -d "Nuke.xcframework/ios-arm64_x86_64-simulator/Nuke.framework/Nuke" \
  -t "output-ios/Nuke.tbd" \
  -o "output-ios" -l Nuke
