#!/bin/bash
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.
#
# Build the BridgeParamTestApp

set -e
cd "$(dirname "$0")"

dotnet build BridgeParamTestApp -c Debug
