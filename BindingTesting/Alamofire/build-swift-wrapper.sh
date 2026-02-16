#!/bin/bash
# Verify the Swift wrapper library for Alamofire bindings exists
# (--xcframework mode compiles it automatically)

set -e

cd "$(dirname "$0")"

if [ ! -d "output-ios/AlamofireSwiftBindings.xcframework" ]; then
  echo "ERROR: AlamofireSwiftBindings.xcframework not found in output-ios/"
  echo "Run regenerate-bindings.sh first"
  exit 1
fi
echo "Swift wrapper framework found."
