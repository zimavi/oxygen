#!/usr/bin/env sh
# Installs, updates or uninstalls Oxygen, from the archive that this script came in. The archive keeps bin, lib and share beside this
# script. Install copies them into a prefix, which is ~/.local/oxygen by default, and copies this script to share/oxygen/install.sh, so
# the prefix can be uninstalled after the archive is deleted. It does not build anything, so it needs no sources or toolchain.
#
#   ./install.sh [--prefix DIR] [--update] [--add-to-path] [--sudo]
#   <prefix>/share/oxygen/install.sh --uninstall [--prefix DIR] [--sudo]
#
# Install refuses a prefix that already has an installation. --update replaces an installation, and prints the version it replaced.
# --uninstall removes what install put there: the folders it owns, the links it made, and the prefix when nothing else is left in it.
# Other files in the prefix are kept. --add-to-path links oxygenc into ~/.local/bin. --sudo links the libraries into /usr/local/lib
# with sudo, so programs find them without LD_LIBRARY_PATH, and removes those links on uninstall; it asks for the password on a terminal.

set -eu

fail() {
    printf '%s\n' "$*" >&2
    exit 1
}

# What an installation owns. Only these are replaced or removed.
OWNED="bin lib share/oxygen/stdlib share/oxygen/install.sh"

HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
PREFIX="$HOME/.local/oxygen"
MODE=install
ADD_TO_PATH=0
SUDO=0
while [ "$#" -gt 0 ]; do
    case "$1" in
        --prefix)
            [ "$#" -ge 2 ] || fail "--prefix needs a folder."
            PREFIX=$2
            shift 2
            ;;
        --update|--uninstall)
            [ "$MODE" = install ] || fail "Use only one of --update and --uninstall."
            MODE=${1#--}
            shift
            ;;
        --add-to-path)
            ADD_TO_PATH=1
            shift
            ;;
        --sudo)
            SUDO=1
            shift
            ;;
        *) fail "Unknown option '$1'." ;;
    esac
done

case "$PREFIX" in
    /*) ;;
    *) PREFIX="$PWD/$PREFIX" ;;
esac
[ "$PREFIX" != / ] || fail "The prefix cannot be the root folder. Choose a folder of its own."
if [ "$MODE" = uninstall ] && [ "$ADD_TO_PATH" = 1 ]; then
    fail "--add-to-path cannot be used with --uninstall."
fi

# sudo asks for the password on a terminal. Without one it must fail at once rather than wait.
sudo_run() {
    if [ -t 0 ]; then
        sudo "$@"
    else
        sudo -n "$@"
    fi
}

# True when the file is a link that points to the given target.
links_to() {
    [ -L "$1" ] && [ "$(readlink -- "$1")" = "$2" ]
}

# The libraries that install linked into /usr/local/lib, for uninstall.
SYSTEM_LINKS=""
for file in liboxygenrt.so liboxygenstd.so; do
    if links_to "/usr/local/lib/$file" "$PREFIX/lib/$file"; then
        SYSTEM_LINKS="$SYSTEM_LINKS /usr/local/lib/$file"
    fi
done

link_system_libraries() {
    command -v sudo >/dev/null 2>&1 || return 1
    sudo_run -v || return 1
    sudo_run mkdir -p -- /usr/local/lib || return 1
    for file in liboxygenrt.so liboxygenstd.so; do
        sudo_run ln -sfn -- "$PREFIX/lib/$file" "/usr/local/lib/$file" || return 1
    done
    sudo_run ldconfig || return 1
}

unlink_system_libraries() {
    command -v sudo >/dev/null 2>&1 || return 1
    sudo_run -v || return 1
    for link in $SYSTEM_LINKS; do
        sudo_run rm -f -- "$link" || return 1
    done
    sudo_run ldconfig || return 1
}

LINKS_FAILED=0

if [ "$MODE" = uninstall ]; then
    [ -x "$PREFIX/bin/oxygenc" ] || fail "No Oxygen installation at $PREFIX to uninstall."

    if links_to "$HOME/.local/bin/oxygenc" "$PREFIX/bin/oxygenc"; then
        rm -f -- "$HOME/.local/bin/oxygenc"
    fi

    if [ -n "$SYSTEM_LINKS" ]; then
        if [ "$SUDO" = 1 ]; then
            if unlink_system_libraries; then
                printf '%s\n' "Removed the links in /usr/local/lib."
            else
                printf '%s\n' "But you promised ;(" >&2
                printf '%s\n' "sudo could not get permission, so the links in /usr/local/lib were not removed." >&2
                LINKS_FAILED=1
            fi
        else
            printf '%s\n' "The libraries are still linked in /usr/local/lib. Run with --sudo to remove the links."
        fi
    fi

    for part in $OWNED; do
        rm -rf -- "$PREFIX/$part"
    done
    # Folders are removed only when nothing else is in them, so a folder that holds other files stays, and so does the prefix.
    for folder in "$PREFIX/share/oxygen" "$PREFIX/share" "$PREFIX/bin" "$PREFIX/lib" "$PREFIX"; do
        rmdir -- "$folder" 2>/dev/null || true
    done

    if [ -d "$PREFIX" ]; then
        printf '%s\n' "Uninstalled Oxygen from $PREFIX. Other files in it were kept."
    else
        printf '%s\n' "Uninstalled Oxygen from $PREFIX."
    fi
    [ "$LINKS_FAILED" = 0 ] || exit 1
    exit 0
fi

for part in bin lib share/oxygen/stdlib install.sh; do
    [ -e "$HERE/$part" ] || fail "The archive is incomplete: '$HERE/$part' is missing. Run this script from the unpacked archive."
done

OLD_VERSION=
if [ -x "$PREFIX/bin/oxygenc" ]; then
    [ "$MODE" = update ] || fail "$PREFIX already has an installation. Use --update to replace it."
    OLD_VERSION=$("$PREFIX/bin/oxygenc" --version 2>/dev/null | head -n 1 || true)
elif [ "$MODE" = update ]; then
    fail "No installation at $PREFIX to update. Run without --update to install there."
fi

for part in $OWNED; do
    rm -rf -- "$PREFIX/$part"
done
mkdir -p -- "$PREFIX/bin" "$PREFIX/lib" "$PREFIX/share/oxygen/stdlib"
cp -R "$HERE/bin/." "$PREFIX/bin/"
cp -R "$HERE/lib/." "$PREFIX/lib/"
cp -R "$HERE/share/oxygen/stdlib/." "$PREFIX/share/oxygen/stdlib/"
cp "$HERE/install.sh" "$PREFIX/share/oxygen/install.sh"
chmod 755 "$PREFIX/share/oxygen/install.sh"

if [ "$ADD_TO_PATH" = 1 ]; then
    mkdir -p -- "$HOME/.local/bin"
    ln -sfn -- "$PREFIX/bin/oxygenc" "$HOME/.local/bin/oxygenc"
    case ":$PATH:" in
        *":$HOME/.local/bin:"*) ;;
        *) printf '%s\n' "$HOME/.local/bin is not on PATH. Add it to your shell profile to run oxygenc from any folder." ;;
    esac
fi

if [ "$SUDO" = 1 ]; then
    if link_system_libraries; then
        printf '%s\n' "Linked the libraries into /usr/local/lib."
    else
        printf '%s\n' "But you promised ;(" >&2
        printf '%s\n' "sudo could not get permission, so the libraries were not linked into /usr/local/lib. Programs need LD_LIBRARY_PATH=$PREFIX/lib." >&2
        LINKS_FAILED=1
    fi
fi

if [ "$MODE" = update ]; then
    printf '%s\n' "Updated Oxygen in $PREFIX from ${OLD_VERSION:-an unknown version} to $("$PREFIX/bin/oxygenc" --version | head -n 1)."
else
    printf '%s\n' "Installed Oxygen to $PREFIX. Put $PREFIX/bin on PATH to use oxygenc."
fi

[ "$LINKS_FAILED" = 0 ] || exit 1
