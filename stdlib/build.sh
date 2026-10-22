#!/usr/bin/env sh
# Builds the Oxygen runtime, then the standard library as one shared library, into build/<triple>/ next to these sources:
# std.so on the Linux target.
#
#   ./build.sh                                  the host target
#   ./build.sh x86_64-unknown-linux-gnu         the named target
#   ./build.sh clean                            removes the build directory
#
# The standard library is built with --no-std, since it is the library that the other programs load.

set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPOSITORY_ROOT=$(dirname -- "$SCRIPT_DIR")
OUTPUT_DIRECTORY=${OUTPUT_DIRECTORY:-"$SCRIPT_DIR/build"}
CONFIGURATION=${CONFIGURATION:-Release}

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
        printf '%s\n' "This host has no default target. Pass one of: $supported_targets." >&2
        exit 1
    fi
fi
TARGET=$1

case " $supported_targets " in
    *" $TARGET "*) ;;
    *)
        printf '%s\n' "Unsupported target '$TARGET'. Supported targets are: $supported_targets." >&2
        exit 1
        ;;
esac

# The runtime comes first: the standard library links it, so the shared runtime must exist.
"$REPOSITORY_ROOT/runtime/build.sh" "$TARGET"
RUNTIME="$REPOSITORY_ROOT/runtime/build/$TARGET/liboxygenrt.so"
if [ ! -f "$RUNTIME" ]; then
    printf '%s\n' "The shared runtime was not produced at '$RUNTIME'." >&2
    exit 1
fi

# The compiler is published for the host, which the LLVM library's native file belongs to. A build does not copy that file.
COMPILER_DIRECTORY="$OUTPUT_DIRECTORY/$TARGET/compiler"
dotnet publish "$REPOSITORY_ROOT/src/Oxygen.Cli/Oxygen.Cli.csproj" --configuration "$CONFIGURATION" --runtime linux-x64 \
    --self-contained false --output "$COMPILER_DIRECTORY" --nologo --verbosity quiet -p:PublishAot=false

# --target takes the triple with underscores, as the CLI documents it.
CLI_TARGET=$(printf '%s' "$TARGET" | tr - _)
mkdir -p -- "$OUTPUT_DIRECTORY/$TARGET"
set -- "$SCRIPT_DIR"/*.oxy
"$COMPILER_DIRECTORY/oxygenc" build "$@" --library --dynamic --no-std --target "$CLI_TARGET" -r "$RUNTIME" \
    -o "$OUTPUT_DIRECTORY/$TARGET/std"

printf '%s\n' "Built the standard library: $OUTPUT_DIRECTORY/$TARGET/std.so"
