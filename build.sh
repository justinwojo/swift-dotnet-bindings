#!/usr/bin/env bash
set -e

scriptroot="$( cd -P "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"

dotnet build "$scriptroot/SwiftBindings.sln" "$@"
