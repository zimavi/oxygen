#!/usr/bin/env sh
# Copies the built toolchain into the prefix. It replaces the prefix's bin, lib and share/oxygen/stdlib folders and leaves other files in
# the prefix alone.
#
#   scripts/bundle.sh [--prefix DIR] [--stage DIR]

set -eu
. "$(dirname -- "$0")/common.sh"

PREFIX=$DEFAULT_PREFIX
STAGE=$DEFAULT_STAGE
while [ "$#" -gt 0 ]; do
    case "$1" in
        --prefix)
            [ "$#" -ge 2 ] || fail "--prefix needs a folder."
            PREFIX=$(absolute_path "$2")
            shift 2
            ;;
        --stage)
            [ "$#" -ge 2 ] || fail "--stage needs a folder."
            STAGE=$(absolute_path "$2")
            shift 2
            ;;
        *) fail "Unknown option '$1'." ;;
    esac
done
check_prefix "$PREFIX"

for file in "$STAGE/compiler/oxygenc" "$RUNTIME_DIRECTORY/liboxygenrt.so" "$RUNTIME_DIRECTORY/liboxygenrt.a" \
    "$STANDARD_LIBRARY_DIRECTORY/liboxygenstd.so"; do
    [ -f "$file" ] || fail "Missing '$file'. Run publish-compiler, build-runtime and build-stdlib first."
done
set -- "$REPOSITORY_ROOT"/stdlib/*.oxy
[ -f "$1" ] || fail "The standard library has no sources in '$REPOSITORY_ROOT/stdlib'."

rm -rf -- "$PREFIX/bin" "$PREFIX/lib" "$PREFIX/share/oxygen/stdlib"
mkdir -p -- "$PREFIX/bin" "$PREFIX/lib" "$PREFIX/share/oxygen/stdlib"
cp -R "$STAGE/compiler/." "$PREFIX/bin/"
cp "$RUNTIME_DIRECTORY/liboxygenrt.so" "$RUNTIME_DIRECTORY/liboxygenrt.a" "$STANDARD_LIBRARY_DIRECTORY/liboxygenstd.so" "$PREFIX/lib/"
cp "$@" "$PREFIX/share/oxygen/stdlib/"

printf '%s\n' "Installed the toolchain to $PREFIX"
