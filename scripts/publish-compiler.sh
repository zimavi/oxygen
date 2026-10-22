#!/usr/bin/env sh
# Publishes the compiler for Linux into <stage>/compiler. The compiler is published, not built, because its LLVM library is a native
# file that publish copies beside the executable.
#
#   scripts/publish-compiler.sh [--stage DIR]

set -eu
. "$(dirname -- "$0")/common.sh"

STAGE=$DEFAULT_STAGE
while [ "$#" -gt 0 ]; do
    case "$1" in
        --stage)
            [ "$#" -ge 2 ] || fail "--stage needs a folder."
            STAGE=$(absolute_path "$2")
            shift 2
            ;;
        *) fail "Unknown option '$1'." ;;
    esac
done

COMPILER_DIRECTORY="$STAGE/compiler"
# Publish keeps the files of an earlier publish, so the folder is rebuilt.
rm -rf -- "$COMPILER_DIRECTORY"
dotnet publish "$REPOSITORY_ROOT/src/Oxygen.Cli/Oxygen.Cli.csproj" --configuration Release --runtime linux-x64 \
    --self-contained false --output "$COMPILER_DIRECTORY" --nologo --verbosity quiet -p:PublishAot=false

printf '%s\n' "Published the compiler to $COMPILER_DIRECTORY"
