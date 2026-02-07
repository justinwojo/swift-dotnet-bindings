#!/bin/bash
# Regenerate CryptoSwift bindings from xcframework

set -e

cd "$(dirname "$0")"
PROJECT_ROOT="../.."

dotnet run --project "$PROJECT_ROOT/src/Swift.Bindings/src" -- \
  -a "CryptoSwift.xcframework/ios-arm64_x86_64-simulator/CryptoSwift.framework/Modules/CryptoSwift.swiftmodule/arm64-apple-ios-simulator.abi.json" \
  -d "CryptoSwift.xcframework/ios-arm64_x86_64-simulator/CryptoSwift.framework/CryptoSwift" \
  -t "output-ios/CryptoSwift.tbd" \
  -o "output-ios" -l CryptoSwift --async-library SwiftBindings
