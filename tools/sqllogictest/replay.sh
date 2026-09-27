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
# With no arguments it replays refs/random over lists/sweep-random.txt and then
# refs/evidence over lists/sweep-evidence.txt; naming a ref-dir replays that one
# alone, over lists/sweep-random.txt unless a file list is given.
#
# Paths are relative to the data directory ($SLT_DATA, or .vs/sqllogictest under
# the repo root), except that a file list may also name one under lists/ beside
# this script, which is where the lists live. Clean means each run's
# findings.jsonl is empty.
set -uo pipefail
TOOL="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$TOOL/../.." && pwd)"
DATA="${SLT_DATA:-$REPO/.vs/sqllogictest}"
cd "$DATA" || { echo "no data directory $DATA — run fetch-corpus.sh" >&2; exit 1; }

PAR="${4:-$(nproc)}"
RUNNER="$TOOL/runner/bin/Release/net10.0/SltRunner.dll"

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

# replay <ref-dir> <out-prefix> <file-list>
replay() {
    local ref="$1" prefix="$2" list="$3" status
    [ -d "$ref" ] || { echo "no such reference directory: $DATA/$ref — capture one with sweep-parallel.sh" >&2; return 1; }
    # A relative list not found in the data directory is one of lists/ here.
    [ -f "$list" ] || [ ! -f "$TOOL/$list" ] || list="$TOOL/$list"
    [ -f "$list" ] || { echo "no such file list: $list" >&2; return 1; }

    echo "replaying $(grep -hcv '^#' "$list") files from $ref at --parallel $PAR"
    local start=$(date +%s)
    dotnet "$RUNNER" \
        --files "$list" \
        --out "$prefix" \
        --replay "$ref" \
        --parallel "$PAR" \
        --emit-cap 6 > "${prefix}.log" 2>&1
    status=$?
    echo "replay finished in $(( $(date +%s) - start ))s (exit=$status)"
    # Exit 2 means at least one reference no longer describes its script.
    [ "$status" = 2 ] && echo "STALE REFERENCE(S) -- re-capture; see ${prefix}.log" >&2
    sed -n '/^mode=/,/^--- both-error/p' "${prefix}/summary.txt"
    echo "full summary: $DATA/${prefix}/summary.txt   findings: $DATA/${prefix}/findings.jsonl"
    return $status
}

if [ $# -eq 0 ]; then
    # The routine run: random/, then evidence/, which adds under a second.
    replay refs/random results-replay "$TOOL/lists/sweep-random.txt"
    STATUS=$?
    replay refs/evidence results-evidence-replay "$TOOL/lists/sweep-evidence.txt" || STATUS=$?
    exit $STATUS
fi
replay "$1" "${2:-results-replay}" "${3:-$TOOL/lists/sweep-random.txt}"
