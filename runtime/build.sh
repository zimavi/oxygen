#!/usr/bin/env sh

set -eu

if [ "${1:-}" = "clean" ]; then
    SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
    OUTPUT_DIRECTORY=${OUTPUT_DIRECTORY:-"$SCRIPT_DIR/build"}
    rm -rf -- "$OUTPUT_DIRECTORY"
    printf '%s\n' "Cleaned $OUTPUT_DIRECTORY"
    exit 0
fi

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

for target in posix windows; do
    if [ "$target" = "windows" ]; then
        target_flags="--target=x86_64-pc-windows-msvc"
        library_name="liboxygenrt.a"
    else
        target_flags=""
        library_name="liboxygenrt.a"
    fi

    target_directory="$OUTPUT_DIRECTORY/$target"
    mkdir -p "$target_directory"
    library="$target_directory/$library_name"
    objects=
    for source in "$SOURCE_DIRECTORY"/*.c; do
        object="$target_directory/$(basename "${source%.c}").o"
        "$CLANG" $target_flags -std=c17 -O2 -g -I "$INCLUDE_DIRECTORY" -c "$source" -o "$object"
        objects="$objects $object"
    done
    # shellcheck disable=SC2086
    "$ARCHIVER" rcs "$library" $objects
    printf '%s\n' "$library"
done
