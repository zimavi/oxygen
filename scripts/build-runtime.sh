#!/usr/bin/env sh
# Builds the runtime for Linux x86_64: the static archive and the shared object, which runtime/build.sh builds for the target.

set -eu
. "$(dirname -- "$0")/common.sh"

"$REPOSITORY_ROOT/runtime/build.sh" "$TRIPLE"

for file in "$RUNTIME_DIRECTORY/liboxygenrt.a" "$RUNTIME_DIRECTORY/liboxygenrt.so"; do
    [ -f "$file" ] || fail "The runtime is incomplete: '$file' was not produced."
done

printf '%s\n' "Built the runtime in $RUNTIME_DIRECTORY"
