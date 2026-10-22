#!/usr/bin/env sh
# Builds the standard library as a shared object, liboxygenstd.so, with stdlib/build.sh. That script builds the runtime it links and publishes its
# own copy of the compiler, so the compiler from publish-compiler is not used here.

set -eu
. "$(dirname -- "$0")/common.sh"

"$REPOSITORY_ROOT/stdlib/build.sh" "$TRIPLE"

[ -f "$STANDARD_LIBRARY_DIRECTORY/liboxygenstd.so" ] || fail "The standard library is incomplete: '$STANDARD_LIBRARY_DIRECTORY/liboxygenstd.so' was not produced."

printf '%s\n' "Built the standard library in $STANDARD_LIBRARY_DIRECTORY"
