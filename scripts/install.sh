#!/usr/bin/env sh
# Installs the Oxygen toolchain for Linux x86_64 into a prefix (default ~/.local/oxygen). It runs the steps in scripts/ in order:
# check-dotnet, publish-compiler, build-runtime, build-stdlib and bundle. It also runs link-libraries, which needs sudo, when you say
# yes to its question (or pass --sudo), and path when --add-to-path is given.
#
#   scripts/install.sh [--prefix DIR] [--stage DIR] [--only STEP[,STEP...]] [--skip-build] [--add-to-path] [--sudo] [--update]
#
# --skip-build reuses the outputs that already exist. Each step is also a script of its own, so any step can be rerun alone.
# The sudo question is asked only on a terminal, and only for a default run (without --only). Without a yes, nothing is linked into
# /usr/local/lib.
# --update rebuilds the installation that is already at the prefix, and fails when there is none. It does not ask about sudo: it
# refreshes the system links only when they already point at the prefix, and --sudo still links them.
#
# The exit status is 1 when the libraries were asked for and could not be linked into /usr/local/lib. The install itself is complete then,
# with the prefix's own libraries, which need LD_LIBRARY_PATH.

set -eu
. "$(dirname -- "$0")/common.sh"

PREFIX=$DEFAULT_PREFIX
STAGE=$DEFAULT_STAGE
ONLY=
SKIP_BUILD=0
ADD_TO_PATH=0
SUDO=ask
UPDATE=0
while [ "$#" -gt 0 ]; do
    case "$1" in
        --prefix)
            [ "$#" -ge 2 ] || fail "--prefix needs a folder."
            PREFIX=$2
            shift 2
            ;;
        --stage)
            [ "$#" -ge 2 ] || fail "--stage needs a folder."
            STAGE=$2
            shift 2
            ;;
        --only)
            [ "$#" -ge 2 ] || fail "--only needs a list of steps."
            ONLY=$2
            shift 2
            ;;
        --skip-build)
            SKIP_BUILD=1
            shift
            ;;
        --add-to-path)
            ADD_TO_PATH=1
            shift
            ;;
        --sudo)
            SUDO=yes
            shift
            ;;
        --update)
            UPDATE=1
            shift
            ;;
        *) fail "Unknown option '$1'." ;;
    esac
done
PREFIX=$(absolute_path "$PREFIX")
STAGE=$(absolute_path "$STAGE")
check_prefix "$PREFIX"

# The version of the compiler installed at the prefix, or nothing when there is none.
installed_version() {
    if [ -x "$PREFIX/bin/oxygenc" ]; then
        "$PREFIX/bin/oxygenc" --version 2>/dev/null | head -n 1
    fi
}

# True when the system links exist and point at this prefix's libraries.
system_links_point_here() {
    [ "$(readlink "$SYSTEM_LIBRARY_DIRECTORY/liboxygenrt.so" 2>/dev/null)" = "$PREFIX/lib/liboxygenrt.so" ] &&
        [ "$(readlink "$SYSTEM_LIBRARY_DIRECTORY/liboxygenstd.so" 2>/dev/null)" = "$PREFIX/lib/liboxygenstd.so" ]
}

OLD_VERSION=
if [ "$UPDATE" = 1 ]; then
    [ -x "$PREFIX/bin/oxygenc" ] || fail "No installation at '$PREFIX' to update. Run without --update to install there."
    [ "$SKIP_BUILD" = 0 ] || fail "--update rebuilds every step, so it cannot be combined with --skip-build."
    OLD_VERSION=$(installed_version)
fi

ALL_STEPS="check-dotnet publish-compiler build-runtime build-stdlib bundle link-libraries path"
if [ -n "$ONLY" ]; then
    SELECTED=$(printf '%s' "$ONLY" | tr ',' ' ')
else
    if [ "$SUDO" = ask ]; then
        SUDO=no
        if [ "$UPDATE" = 1 ]; then
            # The links the install made are refreshed, so an update does not ask again. Without a terminal, sudo cannot ask for its password,
            # so the links are left as they are and the update says so.
            if system_links_point_here; then
                if [ -t 0 ]; then
                    SUDO=yes
                else
                    printf '%s\n' "The system links point at this prefix, but there is no terminal to refresh them with sudo. Run the update in a terminal, or pass --sudo, to refresh them."
                fi
            fi
        elif [ -t 0 ]; then
            printf '%s' "Link the libraries into /usr/local/lib with sudo, so programs find them without LD_LIBRARY_PATH? [y/N] "
            read -r answer || answer=
            case "$answer" in
                y | Y | yes | YES | Yes) SUDO=yes ;;
            esac
        fi
    fi
    SELECTED="check-dotnet publish-compiler build-runtime build-stdlib bundle"
    if [ "$SUDO" = yes ]; then
        SELECTED="$SELECTED link-libraries"
    fi
    if [ "$ADD_TO_PATH" = 1 ]; then
        SELECTED="$SELECTED path"
    fi
fi
for step in $SELECTED; do
    case " $ALL_STEPS " in
        *" $step "*) ;;
        *) fail "Unknown step '$step'. The steps are: $ALL_STEPS." ;;
    esac
done
# The steps run in pipeline order, whatever order --only names them in.
ORDERED=
for step in $ALL_STEPS; do
    case " $SELECTED " in
        *" $step "*) ORDERED="$ORDERED $step" ;;
    esac
done
SELECTED=$ORDERED

# Whether a step's outputs already exist, which --skip-build reuses instead of building them again.
step_has_output() {
    case "$1" in
        publish-compiler) [ -f "$STAGE/compiler/oxygenc" ] ;;
        build-runtime) [ -f "$RUNTIME_DIRECTORY/liboxygenrt.a" ] && [ -f "$RUNTIME_DIRECTORY/liboxygenrt.so" ] ;;
        build-stdlib) [ -f "$STANDARD_LIBRARY_DIRECTORY/liboxygenstd.so" ] ;;
        *) false ;;
    esac
}

LINKS_FAILED=0
for step in $SELECTED; do
    if [ "$SKIP_BUILD" = 1 ] && step_has_output "$step"; then
        printf '%s\n' "Skipping $step: its outputs exist."
        continue
    fi
    printf '%s\n' "== $step"
    case "$step" in
        check-dotnet) "$SCRIPTS_DIRECTORY/check-dotnet.sh" ;;
        publish-compiler) "$SCRIPTS_DIRECTORY/publish-compiler.sh" --stage "$STAGE" ;;
        build-runtime) "$SCRIPTS_DIRECTORY/build-runtime.sh" ;;
        build-stdlib) "$SCRIPTS_DIRECTORY/build-stdlib.sh" ;;
        bundle) "$SCRIPTS_DIRECTORY/bundle.sh" --prefix "$PREFIX" --stage "$STAGE" ;;
        link-libraries)
            if ! "$SCRIPTS_DIRECTORY/link-libraries.sh" --prefix "$PREFIX"; then
                LINKS_FAILED=1
                printf '%s\n' "Continuing with the libraries of $PREFIX. Programs need LD_LIBRARY_PATH=$PREFIX/lib to run." >&2
            fi
            ;;
        path) "$SCRIPTS_DIRECTORY/path.sh" --prefix "$PREFIX" ;;
    esac
done

case " $SELECTED " in
    *" bundle "*)
        if [ "$UPDATE" = 1 ]; then
            printf '%s\n' "Updated Oxygen in $PREFIX from ${OLD_VERSION:-an unknown version} to $(installed_version)."
        else
            printf '%s\n' "Installed Oxygen to $PREFIX. Put $PREFIX/bin on PATH to use oxygenc."
        fi
        ;;
esac

if [ "$LINKS_FAILED" = 1 ]; then
    printf '%s\n' "The libraries were not linked into $SYSTEM_LIBRARY_DIRECTORY, so this install exits with status 1." >&2
    exit 1
fi
