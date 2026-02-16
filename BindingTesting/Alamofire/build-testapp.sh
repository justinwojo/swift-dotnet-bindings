#!/bin/bash
# Build the AlamofireTestApp

set -e

cd "$(dirname "$0")"

dotnet build AlamofireTestApp -c Debug
