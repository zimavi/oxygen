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
OBJECT="$OUTPUT_DIRECTORY/oxygen_runtime.o"
LIBRARY="$OUTPUT_DIRECTORY/liboxygen_runtime.a"

"$CLANG" -std=c17 -O2 -I "$INCLUDE_DIRECTORY" -c "$SOURCE" -o "$OBJECT"
"$ARCHIVER" rcs "$LIBRARY" "$OBJECT"

printf '%s\n' "$LIBRARY"
