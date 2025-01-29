#!/usr/bin/env bash
set -e
set -o pipefail

usage()
{
  echo "Common settings:"
  echo "  --platform <value>         Platform: MacOSX, iPhoneOS, iPhoneSimulator, AppleTVOS, AppleTVSimulator"
  echo "  --arch <value>             Architecture: arm64e-apple-macos, x86_64-apple-macos"
  echo "  --framework <value>        Framework to generate bindings for"
  echo "  --configuration <value>    Configuration: Debug, Release"
  echo "  --help                     Print help and exit (short: -h)"
  echo ""

  echo "Actions:"
  echo "  --experimental             Generates only Runtime.Swift namespace when bindings for frameworks are not complete"
}

source="${BASH_SOURCE[0]}"

# resolve $SOURCE until the file is no longer a symlink
while [[ -h $source ]]; do
  scriptroot="$( cd -P "$( dirname "$source" )" && pwd )"
  source="$(readlink "$source")"

  # if $source was a relative symlink, we need to resolve it relative to the path where the
  # symlink file was located
  [[ $source != /* ]] && source="$scriptroot/$source"
done

scriptroot="$( cd -P "$( dirname "$source" )" && pwd )"

$scriptroot/dotnet.sh format --exclude artifacts --verify-no-changes
