#!/usr/bin/env sh
# Checks that the .NET 10 SDK, which the compiler projects target, is installed. It does not install it.

set -eu
. "$(dirname -- "$0")/common.sh"

if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    fail "The .NET 10 SDK is required, and it was not found. Install it from https://learn.microsoft.com/dotnet/core/install/linux"
fi

printf '%s\n' "Found the .NET 10 SDK."
