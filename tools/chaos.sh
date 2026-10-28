#!/usr/bin/env bash
# Runs ChaosScenarios' seeds split over several test processes at once (the harness takes over
# static state, so seeds cannot run in parallel inside one process). Each process gets its own
# seed range and its own PostgreSQL schemas.
#
#   enf-sql subprod --run -- tools/chaos.sh [runs=500] [shards] [first seed=1000]
#
# shards defaults to ENFORCER_CHAOS_SHARDS, else 2: each process uses up to 5 connections and
# subprod's role is capped at 10 (3 processes already fail with "too many connections").
# Progress and logs go to ENFORCER_CHAOS_OUT (default: a new temp directory); a failing seed
# replays alone with ENFORCER_CHAOS_SEED=<seed>. Exits non-zero if any seed failed.
set -uo pipefail

runs=${1:-500}
shards=${2:-${ENFORCER_CHAOS_SHARDS:-2}}
from=${3:-1000}
out=${ENFORCER_CHAOS_OUT:-$(mktemp -d -t enforcer-chaos.XXXXXX)}

if [ -z "${ENFORCER_TEST_POSTGRES:-}" ]; then
  echo "ENFORCER_TEST_POSTGRES is not set: every seed would be skipped. Run under enf-sql subprod --run --." >&2
  exit 2
fi
[ "$shards" -gt "$runs" ] && shards=$runs

cd "$(dirname "$0")/.."
mkdir -p "$out"
rm -f "$out"/progress-*.txt "$out"/log-*.txt

echo "Building..."
dotnet build Enforcer5.slnx -v q -nologo >"$out/build.log" 2>&1 || { echo "Build failed, see $out/build.log" >&2; exit 2; }

echo "$runs seeds from $from in $shards processes; progress in $out"
start=$(date +%s)
pids=()
per=$(( (runs + shards - 1) / shards ))
for ((i = 0; i < shards; i++)); do
  first=$((from + i * per))
  count=$(( runs - i * per < per ? runs - i * per : per ))
  [ "$count" -le 0 ] && break
  ENFORCER_CHAOS_FROM=$first ENFORCER_CHAOS_RUNS=$count ENFORCER_CHAOS_PROGRESS="$out/progress-$i.txt" \
    dotnet test Enforcer5.slnx --no-build --filter FullyQualifiedName~ChaosScenarios >"$out/log-$i.txt" 2>&1 &
  pids+=($!)
done

# Waits on the processes started above only (never pgrep, which can match this script itself).
status=0
for pid in "${pids[@]}"; do wait "$pid" || status=1; done

ok=$(cat "$out"/progress-*.txt 2>/dev/null | grep -c ' ok$')
failed=$(cat "$out"/progress-*.txt 2>/dev/null | grep -o '^Chaos seed [0-9]* failed' | awk '{print $3}' | sort -n | tr '\n' ' ')
echo "Done in $(( $(date +%s) - start ))s: $ok of $runs seeds ok."
if [ -n "$failed" ]; then
  echo "Failed seeds: $failed(details in $out/progress-*.txt)"
  status=1
elif [ "$ok" -ne "$runs" ]; then
  echo "Only $ok of $runs seeds reported; a test process stopped early, see $out/log-*.txt"
  status=1
fi
exit $status
