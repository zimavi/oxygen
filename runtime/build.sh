#!/usr/bin/env sh

set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
SOURCE="$SCRIPT_DIR/src/main.c"
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
for variant in release debug; do
    suffix=""
    flags="-O2"
    if [ "$variant" = "debug" ]; then
        suffix="_debug"
        flags="-O0 -g"
    fi

    object="$OUTPUT_DIRECTORY/oxygen_runtime${suffix}.o"
    library="$OUTPUT_DIRECTORY/liboxygen_runtime${suffix}.a"
    # shellcheck disable=SC2086
    "$CLANG" -std=c17 $flags -I "$INCLUDE_DIRECTORY" -c "$SOURCE" -o "$object"
    "$ARCHIVER" rcs "$library" "$object"
    printf '%s\n' "$library"
done
