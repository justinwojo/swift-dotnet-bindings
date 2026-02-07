#!/bin/bash
# Build the CryptoSwiftTestApp

set -e

cd "$(dirname "$0")"

dotnet build CryptoSwiftTestApp -c Debug
