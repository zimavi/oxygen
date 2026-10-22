#!/usr/bin/env sh
# Links the prefix's shared libraries into /usr/local/lib with sudo: a symlink for each library, then ldconfig. Programs then find them
# without LD_LIBRARY_PATH. If sudo cannot run, the step says so and exits with 1, and install.sh carries on with the prefix's own libraries.
#
#   scripts/link-libraries.sh [--prefix DIR]

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

for file in "$PREFIX/lib/liboxygenrt.so" "$PREFIX/lib/liboxygenstd.so"; do
    [ -f "$file" ] || fail "Missing '$file'. Run build-runtime and build-stdlib, then bundle, before linking the libraries."
done

sudo_failed() {
    printf '%s\n' "But you promised ;(" >&2
    printf '%s\n' "sudo could not get permission, so the libraries were not linked into $SYSTEM_LIBRARY_DIRECTORY." >&2
    exit 1
}

# sudo asks for the password on the terminal. Without a terminal it cannot ask, and it must fail at once rather than wait for one.
sudo_run() {
    if [ -t 0 ]; then
        sudo "$@"
    else
        sudo -n "$@"
    fi
}

command -v sudo >/dev/null 2>&1 || sudo_failed
sudo_run -v || sudo_failed
# Some systems have no /usr/local/lib, so it is made first. An existing folder is left as it is.
sudo_run mkdir -p -- "$SYSTEM_LIBRARY_DIRECTORY" || sudo_failed
for file in "$PREFIX/lib/liboxygenrt.so" "$PREFIX/lib/liboxygenstd.so"; do
    sudo_run ln -sfn -- "$file" "$SYSTEM_LIBRARY_DIRECTORY/$(basename -- "$file")" || sudo_failed
done
sudo_run ldconfig || sudo_failed

printf '%s\n' "Linked the libraries of $PREFIX into $SYSTEM_LIBRARY_DIRECTORY, and updated the loader cache."
