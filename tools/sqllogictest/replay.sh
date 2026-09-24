#!/usr/bin/env bash
# In-process replay of a captured reference: the routine sweep.
#
# No SqlClient, no server, no per-script DROP/CREATE DATABASE: the simulator runs
# against the frozen per-record reference a capture wrote, with the scripts spread
# over threads inside the one process. Proves self-consistency (simulator
# regressions), not fidelity — the differential sweep stays the periodic check
# that real has not moved.
#
#   ./tools/sqllogictest/replay.sh [ref-dir] [out-prefix] [file-list] [parallel]
#
# Paths are relative to the data directory ($SLT_DATA, or .vs/sqllogictest under
# the repo root), except that a file list may also name one under lists/ beside
# this script, which is where the default, lists/sweep-random.txt, lives. Clean
# means the run's findings.jsonl is empty.
set -uo pipefail
TOOL="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$TOOL/../.." && pwd)"
DATA="${SLT_DATA:-$REPO/.vs/sqllogictest}"
cd "$DATA" || { echo "no data directory $DATA — run fetch-corpus.sh" >&2; exit 1; }

REF="${1:-refs/random}"
PREFIX="${2:-results-replay}"
LIST="${3:-$TOOL/lists/sweep-random.txt}"
PAR="${4:-$(nproc)}"
RUNNER="$TOOL/runner/bin/Release/net10.0/SltRunner.dll"

[ -d "$REF" ] || { echo "no such reference directory: $DATA/$REF — capture one with sweep-parallel.sh" >&2; exit 1; }
# A relative list not found in the data directory is one of lists/ here.
[ -f "$LIST" ] || [ ! -f "$TOOL/$LIST" ] || LIST="$TOOL/$LIST"
[ -f "$LIST" ] || { echo "no such file list: $LIST" >&2; exit 1; }

# The runner's project reference rebuilds the simulator along with it, so the
# replay always measures the working tree.
export MSBUILDDISABLENODEREUSE=1
echo "rebuilding runner + simulator (Release)"
dotnet build "$TOOL/runner" -c Release -m:1 2>&1 | grep -E "error|Error\(s\)" | tail -1

# Server GC won the pilot measurement at --parallel 16 (3.3 s against 4.2 s for
# workstation GC). The csproj pins workstation GC for the differential run (16
# shard processes, one core each); the environment variable wins over
# runtimeconfig.json, so the two modes differ without a rebuild.
export DOTNET_gcServer=1

echo "replaying $(grep -hcv '^#' "$LIST") files from $REF at --parallel $PAR"
START=$(date +%s)
dotnet "$RUNNER" \
    --files "$LIST" \
    --out "$PREFIX" \
    --replay "$REF" \
    --parallel "$PAR" \
    --emit-cap 6 > "${PREFIX}.log" 2>&1
STATUS=$?
echo "replay finished in $(( $(date +%s) - START ))s (exit=$STATUS)"
# Exit 2 means at least one reference no longer describes its script.
[ "$STATUS" = 2 ] && echo "STALE REFERENCE(S) -- re-capture; see ${PREFIX}.log" >&2
sed -n '/^mode=/,/^--- both-error/p' "${PREFIX}/summary.txt"
echo "full summary: $DATA/${PREFIX}/summary.txt   findings: $DATA/${PREFIX}/findings.jsonl"
exit $STATUS
