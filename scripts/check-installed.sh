#!/usr/bin/env sh
# Checks an installed prefix the way a user meets it: builds a program with the installed compiler from a folder of its own, with no
# OXYGEN_PATH and no LD_LIBRARY_PATH, runs it, and checks its output and exit status. CI runs it after install.sh.
#
#   scripts/check-installed.sh [--prefix DIR]

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

for file in "$PREFIX/bin/oxygenc" "$PREFIX/lib/liboxygenrt.so" "$PREFIX/lib/liboxygenstd.so" "$PREFIX/share/oxygen/stdlib/io.oxy"; do
    [ -f "$file" ] || fail "The installation is incomplete: '$file' is missing."
done

WORK=$(mktemp -d)
trap 'rm -rf -- "$WORK"' EXIT
cd "$WORK"
cat > hello.oxy <<'EOF'
import std.io;

i32 main() {
    io.println_char('i');
    return 7;
}
EOF

# The program is built and run with only the installed toolchain. The compiler records the prefix's lib folder in the program, so the
# program needs neither the variables nor the system links.
build_output=$(env -u OXYGEN_PATH "$PREFIX/bin/oxygenc" build hello.oxy -o hello 2>&1) ||
    fail "Building with the installed compiler failed: $build_output"
if printf '%s' "$build_output" | grep -q 'warning'; then
    fail "Building with the installed compiler printed a warning: $build_output"
fi

set +e
output=$(env -u LD_LIBRARY_PATH ./hello)
status=$?
set -e
[ "$output" = "i" ] || fail "The program printed '$output', expected 'i'."
[ "$status" -eq 7 ] || fail "The program exited with $status, expected 7."

printf '%s\n' "The installation at $PREFIX builds and runs a program from a separate folder."
