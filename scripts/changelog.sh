#!/usr/bin/env sh
# Writes release notes for the commits in a range, grouped by their conventional commit type: breaking changes, features, fixes, and other
# changes. Each line shows the commit's short hash. A commit is a breaking change when its type has a "!", or its body has a
# "BREAKING CHANGE:" footer.
#
#   scripts/changelog.sh [FROM] [TO]     the commits after FROM up to TO, or the whole history up to TO when FROM is empty. TO defaults to HEAD.

set -eu

FROM=${1:-}
TO=${2:-HEAD}
if [ -n "$FROM" ]; then
    RANGE="$FROM..$TO"
else
    RANGE="$TO"
fi

# Each commit is a record of its hash, subject and body, separated by unit and record separators, so a body can hold any text.
git log "$RANGE" --format='%h%x1f%s%x1f%b%x1e' | awk '
BEGIN { RS = "\036"; FS = "\037" }
{
    # Git puts a newline before every record but the first.
    sub(/^\n+/, "")
    if ($1 == "") next
    hash = $1
    subject = $2
    body = $3
    kind = subject
    sub(/[(!:].*$/, "", kind)
    breaking = (subject ~ /^[a-zA-Z]+(\([^)]*\))?!:/) || (body ~ /BREAKING CHANGE:/)
    description = subject
    sub(/^[^:]*: */, "", description)
    line = "- " description " (`" hash "`)\n"
    if (breaking) breaking_changes = breaking_changes line
    else if (kind == "feat") features = features line
    else if (kind == "fix" || kind == "perf") fixes = fixes line
    else other_changes = other_changes line
}
END {
    if (breaking_changes != "") printf "## Breaking changes\n\n%s\n", breaking_changes
    if (features != "") printf "## Features\n\n%s\n", features
    if (fixes != "") printf "## Fixes\n\n%s\n", fixes
    if (other_changes != "") printf "## Other changes\n\n%s\n", other_changes
}'
