#!/usr/bin/env sh
# Builds the Oxygen runtime archive for each target, into build/<triple>/liboxygenrt.a.
#
#   ./build.sh                                  the host target
#   ./build.sh x86_64-unknown-linux-gnu         the named targets
#   ./build.sh clean                            removes the build directory
#
# A cross build needs the target's C headers, which the host may not have.

set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
OUTPUT_DIRECTORY=${OUTPUT_DIRECTORY:-"$SCRIPT_DIR/build"}

supported_targets="x86_64-pc-windows-msvc x86_64-unknown-linux-gnu"

if [ "${1:-}" = "clean" ]; then
    rm -rf -- "$OUTPUT_DIRECTORY"
    printf '%s\n' "Cleaned $OUTPUT_DIRECTORY"
    exit 0
fi

if [ "$#" -eq 0 ]; then
    if [ "$(uname -s)" = "Linux" ] && [ "$(uname -m)" = "x86_64" ]; then
        set -- x86_64-unknown-linux-gnu
    else
        printf '%s\n' "This host has no default runtime target. Pass one of: $supported_targets." >&2
        exit 1
    fi
fi

for target in "$@"; do
    case " $supported_targets " in
        *" $target "*) ;;
        *)
            printf '%s\n' "Unsupported runtime target '$target'. Supported targets are: $supported_targets." >&2
            exit 1
            ;;
    esac
done

SOURCE_DIRECTORY="$SCRIPT_DIR/src"
INCLUDE_DIRECTORY="$SCRIPT_DIR/include"
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

for target in "$@"; do
    target_directory="$OUTPUT_DIRECTORY/$target"
    mkdir -p "$target_directory"
    library="$target_directory/liboxygenrt.a"
    objects=
    for source in "$SOURCE_DIRECTORY"/*.c; do
        object="$target_directory/$(basename "${source%.c}").o"
        # -fPIC is an ELF option: COFF code is position independent already, and clang rejects the flag for the Windows target.
        case "$target" in
            *-windows-*) pic_flags= ;;
            *) pic_flags=-fPIC ;;
        esac
        # shellcheck disable=SC2086
        "$CLANG" "--target=$target" -std=c17 -O2 -g $pic_flags -Wall -Wextra -I "$INCLUDE_DIRECTORY" -c "$source" -o "$object"
        objects="$objects $object"
    done
    # shellcheck disable=SC2086
    "$ARCHIVER" rcs "$library" $objects
    printf '%s\n' "$library"
done
