#!/bin/bash
# Regenerate Alamofire bindings from xcframework

set -e

cd "$(dirname "$0")"
PROJECT_ROOT="../.."

dotnet run --project "$PROJECT_ROOT/src/Swift.Bindings/src" -- \
  --xcframework Alamofire.xcframework \
  -o "output-ios"
