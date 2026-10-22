#!/usr/bin/env sh
# The PATH step for Linux is not implemented yet. A program built with the installed compiler loads liboxygenrt.so and liboxygenstd.so from
# <prefix>/lib, which the loader searches only through the library path or an rpath. The plan leaves that choice open (M4 item 16, U7),
# so this step fails rather than set up a partial install.

set -eu
. "$(dirname -- "$0")/common.sh"

fail "The path step is not implemented for Linux yet. Put the prefix's bin folder on PATH yourself, and until the rpath question is decided, \
set LD_LIBRARY_PATH to the prefix's lib folder for programs you build."
