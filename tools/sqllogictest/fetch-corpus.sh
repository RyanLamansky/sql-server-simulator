#!/usr/bin/env bash
# Downloads SQLite's sqllogictest corpus into the data directory and extracts it,
# giving test/ (the .test scripts every file list names), about.wiki (the format)
# and src/ (the original C runner, reference only).
#
#   ./tools/sqllogictest/fetch-corpus.sh
#
# The data directory is $SLT_DATA, or .vs/sqllogictest under the repo root —
# gitignored, since the extracted corpus is over a gigabyte. The archive is kept
# beside it (corpus.tar.gz) so a re-extraction needs no download.
set -euo pipefail
TOOL="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$TOOL/../.." && pwd)"
DATA="${SLT_DATA:-$REPO/.vs/sqllogictest}"
URL="https://github.com/gregrahn/sqllogictest/archive/refs/heads/master.tar.gz"

mkdir -p "$DATA"
cd "$DATA"
if [ ! -f corpus.tar.gz ]; then
    echo "downloading $URL"
    curl -fL -o corpus.tar.gz.part "$URL"
    mv corpus.tar.gz.part corpus.tar.gz
fi
tar -xzf corpus.tar.gz --strip-components=1
echo "$(find test -name '*.test' | wc -l) scripts under $DATA/test"
