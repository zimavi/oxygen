#!/usr/bin/env sh

set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
SOURCE_DIRECTORY="$SCRIPT_DIR/src"
INCLUDE_DIRECTORY="$SCRIPT_DIR/include"
OUTPUT_DIRECTORY=${OUTPUT_DIRECTORY:-"$SCRIPT_DIR/build"}
CLANG=${CLANG:-clang}
ARCHIVER=${ARCHIVER:-}

if [ -z "$ARCHIVER" ]; then
    if command -v llvm-ar >/dev/null 2>&1; then
        ARCHIVER=llvm-ar
    elif command -v ar >/dev/null 2>&1; then
        ARCHIVER=ar
    else
        printf '%s\n' "No compatible static-library archiver was found. Install llvm-ar or ar, or set ARCHIVER." >&2
        exit 1
    fi
fi

mkdir -p "$OUTPUT_DIRECTORY"
library="$OUTPUT_DIRECTORY/liboxygenrt.a"
objects=
for source in "$SOURCE_DIRECTORY"/*.c; do
    object="$OUTPUT_DIRECTORY/$(basename "${source%.c}").o"
    "$CLANG" -std=c17 -O2 -g -I "$INCLUDE_DIRECTORY" -c "$source" -o "$object"
    objects="$objects $object"
done
# shellcheck disable=SC2086
"$ARCHIVER" rcs "$library" $objects
printf '%s\n' "$library"
