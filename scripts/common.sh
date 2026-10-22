# Settings shared by the install steps in this folder. Each step sources this file, so any step runs alone.
# These scripts install the Linux x86_64 toolchain. The Windows equivalents are the .ps1 files.

SCRIPTS_DIRECTORY=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPOSITORY_ROOT=$(dirname -- "$SCRIPTS_DIRECTORY")
TRIPLE=x86_64-unknown-linux-gnu
DEFAULT_PREFIX="$HOME/.local/oxygen"
DEFAULT_STAGE="$REPOSITORY_ROOT/artifacts/install/stage"
RUNTIME_DIRECTORY="$REPOSITORY_ROOT/runtime/build/$TRIPLE"
STANDARD_LIBRARY_DIRECTORY="$REPOSITORY_ROOT/stdlib/build/$TRIPLE"
# Where link-libraries puts symlinks to the prefix's libraries, so programs find them without LD_LIBRARY_PATH. The variable is for tests.
SYSTEM_LIBRARY_DIRECTORY=${OXYGEN_SYSTEM_LIBRARY_DIRECTORY:-/usr/local/lib}

fail() {
    printf '%s\n' "$*" >&2
    exit 1
}

# The path as an absolute one, so a relative prefix names the same folder in every step.
absolute_path() {
    case "$1" in
        /*) printf '%s\n' "$1" ;;
        *) printf '%s/%s\n' "$PWD" "$1" ;;
    esac
}

check_prefix() {
    case "$1" in
        "" | /) fail "The prefix '$1' cannot be used. Choose a folder of its own." ;;
    esac
}

if [ "$(uname -s)" != "Linux" ] || [ "$(uname -m)" != "x86_64" ]; then
    fail "These .sh scripts install the Linux x86_64 toolchain. On Windows, use the .ps1 scripts in scripts/."
fi
