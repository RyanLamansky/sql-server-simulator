#!/usr/bin/env bash
# Sharded sqllogictest differential sweep, optionally capturing a replay reference.
#
# Splits a file list into N shards and runs one runner process per shard, each
# against its own work database on the live server (--db), so the shards cannot
# drop each other's tables. Needs SLT_SERVER (see README.md).
#
#   ./tools/sqllogictest/sweep-parallel.sh <file-list> <out-prefix> [shards] [capture-ref-dir]
#
# Paths are relative to the data directory ($SLT_DATA, or .vs/sqllogictest under
# the repo root), except that a file list may also name one under lists/ beside
# this script, which is where the default, lists/sweep-random.txt, lives.
#
# With a capture-ref-dir, every shard also writes one reference file per script
# under it (mirroring the script's relative path). The shards share the directory
# safely because their script lists are disjoint, so no two write the same file;
# only the known-divergent register is per-shard (known-divergent-<i>.jsonl),
# concatenated into known-divergent.jsonl at merge time.
#
# Don't run it while another build is running: several dotnet processes racing
# MSBuild node reuse fabricate mass CS0246 errors in concurrent builds.
set -uo pipefail
TOOL="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$TOOL/../.." && pwd)"
DATA="${SLT_DATA:-$REPO/.vs/sqllogictest}"
cd "$DATA" || { echo "no data directory $DATA — run fetch-corpus.sh" >&2; exit 1; }

LIST="${1:-$TOOL/lists/sweep-random.txt}"
PREFIX="${2:-results-par}"
# Default to the machine width; the sweep is CPU-bound on the client side.
SHARDS="${3:-$(nproc)}"
CAPTURE="${4:-}"
RUNNER="$TOOL/runner/bin/Release/net10.0/SltRunner.dll"

# A relative list not found in the data directory is one of lists/ here.
[ -f "$LIST" ] || [ ! -f "$TOOL/$LIST" ] || LIST="$TOOL/$LIST"
[ -f "$LIST" ] || { echo "no such file list: $LIST" >&2; exit 1; }
[ -n "${SLT_SERVER:-}" ] || { echo "set SLT_SERVER to the reference server's connection string (see README.md)" >&2; exit 1; }

# The runner's project reference rebuilds the simulator along with it, so the
# sweep always measures the working tree.
export MSBUILDDISABLENODEREUSE=1
echo "rebuilding runner + simulator (Release)"
dotnet build "$TOOL/runner" -c Release -m:1 2>&1 | grep -E "error|Error\(s\)" | tail -1

rm -rf "${PREFIX}"-* "${PREFIX}".log
mkdir -p "${PREFIX}-shards"

# Round-robin the list so slow scripts (join-heavy ones cluster together in
# sorted order) spread across shards instead of landing in one.
awk -v n="$SHARDS" -v p="${PREFIX}-shards" 'NF && $0 !~ /^#/ { print > (p "/shard-" (NR % n) ".txt") }' "$LIST"

echo "sweeping $(grep -hcv '^#' "$LIST") files across $SHARDS shards${CAPTURE:+, capturing to $CAPTURE}"
[ -n "$CAPTURE" ] && mkdir -p "$CAPTURE"
START=$(date +%s)
pids=()
for i in $(seq 0 $((SHARDS - 1))); do
    SHARD_LIST="${PREFIX}-shards/shard-${i}.txt"
    [ -s "$SHARD_LIST" ] || continue
    CAP_ARGS=()
    [ -n "$CAPTURE" ] && CAP_ARGS=(--capture "$CAPTURE" --shard-tag "$i")
    nice -n 10 dotnet "$RUNNER" \
        --files "$SHARD_LIST" \
        --out "${PREFIX}-${i}" \
        --db "slt_work_${i}" \
        "${CAP_ARGS[@]}" \
        --ignore-halt --emit-cap 6 > "${PREFIX}-${i}.log" 2>&1 &
    pids+=($!)
    echo "  shard $i -> pid ${pids[-1]} ($(wc -l < "$SHARD_LIST") files, db slt_work_${i})"
done

fail=0
for pid in "${pids[@]}"; do wait "$pid" || fail=1; done
echo "all shards finished in $(( $(date +%s) - START ))s (fail=$fail)"

# One register for the whole capture: the per-shard parts are only there because
# several processes cannot append to one file safely.
if [ -n "$CAPTURE" ]; then
    cat "$CAPTURE"/known-divergent-*.jsonl > "$CAPTURE/known-divergent.jsonl" 2>/dev/null
    rm -f "$CAPTURE"/known-divergent-*.jsonl
    echo "captured $(find "$CAPTURE" -name '*.ref' | wc -l) references, $(wc -l < "$CAPTURE/known-divergent.jsonl") known-divergent records -> $CAPTURE/known-divergent.jsonl"
fi

# Merge: sum every counter line across shard summaries, and concatenate findings.
python3 - "$PREFIX" "$SHARDS" <<'PY'
import re, sys, glob, os, collections
prefix, shards = sys.argv[1], int(sys.argv[2])
totals, classes = collections.Counter(), collections.Counter()
section = collections.defaultdict(collections.Counter)
for i in range(shards):
    f = f"{prefix}-{i}/summary.txt"
    if not os.path.exists(f):
        continue
    cur = None
    for line in open(f):
        line = line.rstrip("\n")
        if line.startswith("---"):
            cur = line.strip("- ")
            continue
        m = re.match(r"^(\w+)=(\d+)", line.strip())
        if m and cur is None:
            # elapsed / records-per-second are per-shard rates: summing them is
            # meaningless, and a merged header that looks authoritative is worse
            # than one that omits the field.
            for k, v in re.findall(r"([a-z()A-Z_]+)=(\d+)", line):
                if k in ("elapsed", "sec"):
                    continue
                totals[k] += int(v)
            continue
        m = re.match(r"^\s*(\d+)\s+(.*)$", line)
        if m and cur:
            section[cur][m.group(2)] += int(m.group(1))
with open(f"{prefix}-summary.txt", "w") as out:
    out.write(" ".join(f"{k}={v}" for k, v in sorted(totals.items())) + "  (wall clock: see the shard runner output)\n")
    for name, counts in section.items():
        out.write(f"--- {name} ---\n")
        for k, v in counts.most_common():
            out.write(f"{v:10d}  {k}\n")
with open(f"{prefix}-findings.jsonl", "w") as out:
    for i in range(shards):
        f = f"{prefix}-{i}/findings.jsonl"
        if os.path.exists(f):
            out.write(open(f).read())
print(f"merged -> {prefix}-summary.txt / {prefix}-findings.jsonl")
PY
