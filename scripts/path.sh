#!/usr/bin/env sh
# Links the installed tools into the user's folders, without sudo: oxygenc into ~/.local/bin, and the shared libraries into ~/.local/lib.
# Programs do not depend on these links. The compiler records the prefix's lib folder in each program it builds, so the programs load the
# libraries from there. The links are for the convention and for tools.
#
#   scripts/path.sh [--prefix DIR]

set -eu
. "$(dirname -- "$0")/common.sh"

PREFIX=$DEFAULT_PREFIX
while [ "$#" -gt 0 ]; do
    case "$1" in
        --prefix)
            [ "$#" -ge 2 ] || fail "--prefix needs a folder."
            PREFIX=$(absolute_path "$2")
            shift 2
            ;;
        *) fail "Unknown option '$1'." ;;
    esac
done
check_prefix "$PREFIX"

[ -x "$PREFIX/bin/oxygenc" ] || fail "No compiler at '$PREFIX/bin/oxygenc'. Run bundle first."

USER_BIN_DIRECTORY="$HOME/.local/bin"
USER_LIB_DIRECTORY="$HOME/.local/lib"
mkdir -p -- "$USER_BIN_DIRECTORY" "$USER_LIB_DIRECTORY"
ln -sfn -- "$PREFIX/bin/oxygenc" "$USER_BIN_DIRECTORY/oxygenc"
for file in liboxygenrt.so liboxygenstd.so; do
    ln -sfn -- "$PREFIX/lib/$file" "$USER_LIB_DIRECTORY/$file"
done

printf '%s\n' "Linked oxygenc into $USER_BIN_DIRECTORY and the libraries into $USER_LIB_DIRECTORY."
case ":$PATH:" in
    *":$USER_BIN_DIRECTORY:"*) ;;
    *) printf '%s\n' "$USER_BIN_DIRECTORY is not on PATH. Add it to your shell profile to run oxygenc from any folder." ;;
esac
