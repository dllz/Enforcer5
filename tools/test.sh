#!/usr/bin/env bash
# Runs the whole test suite split over several test processes at once, by test class (the harness takes
# over static state, so its scenarios cannot run in parallel inside one process). Each process gets its
# own PostgreSQL schemas.
#
#   enf-sql subprod --run -- tools/test.sh [shards] [configuration=Debug]
#
# shards defaults to ENFORCER_TEST_SHARDS, else 2: each process uses up to 5 connections and subprod's
# role is capped at 10. Without ENFORCER_TEST_POSTGRES it still runs, the PostgreSQL tests skipped, as
# plain `dotnet test` does. Logs go to ENFORCER_TEST_OUT (default: a new temp directory). Exits non-zero
# if any test failed or a process stopped early.
set -uo pipefail

shards=${1:-${ENFORCER_TEST_SHARDS:-2}}
config=${2:-Debug}
out=${ENFORCER_TEST_OUT:-$(mktemp -d -t enforcer-test.XXXXXX)}

cd "$(dirname "$0")/.."
mkdir -p "$out"
rm -f "$out"/log-*.txt

[ -z "${ENFORCER_TEST_POSTGRES:-}" ] && echo "ENFORCER_TEST_POSTGRES is not set: the PostgreSQL tests are skipped." >&2

echo "Building $config..."
dotnet build Enforcer5.slnx -c "$config" -v q -nologo >"$out/build.log" 2>&1 || { echo "Build failed, see $out/build.log" >&2; exit 2; }

# Every [TestClass], heaviest first (the harness scenarios, which use the database), dealt out in turn so
# each process gets a share of them.
classes=$(
  for f in $(grep -rl --include='*.cs' '\[TestClass\]' tests); do
    weight=$(grep -c '\[TestMethod\]' "$f")
    grep -q 'TestCategory("Harness")\|TestPostgres' "$f" && weight=$((weight * 20))
    awk -v w="$weight" '/\[TestClass\]/ { want = 1 } want && match($0, /class [A-Za-z0-9_]+/) {
      print w, substr($0, RSTART + 6, RLENGTH - 6); want = 0 }' "$f"
  done | sort -k1,1nr -k2 | awk '{ print $2 }'
)

filters=()
i=0
for class in $classes; do
  shard=$((i % shards))
  filters[$shard]="${filters[$shard]:+${filters[$shard]}|}FullyQualifiedName~.$class."
  i=$((i + 1))
done

echo "$i test classes in $shards processes; logs in $out"
start=$(date +%s)
pids=()
for ((s = 0; s < shards; s++)); do
  [ -z "${filters[$s]:-}" ] && continue
  dotnet test Enforcer5.slnx -c "$config" --no-build --filter "${filters[$s]}" >"$out/log-$s.txt" 2>&1 &
  pids+=($!)
done

# Waits on the processes started above only (never pgrep, which can match this script itself).
status=0
for pid in "${pids[@]}"; do wait "$pid" || status=1; done

passed=0; failed=0; skipped=0; reported=0
for log in "$out"/log-*.txt; do
  line=$(grep -E '^(Passed|Failed)!' "$log" | tail -1)
  [ -z "$line" ] && continue
  reported=$((reported + 1))
  passed=$((passed + $(sed -E 's/.*Passed: *([0-9]+).*/\1/' <<<"$line")))
  failed=$((failed + $(sed -E 's/.*Failed: *([0-9]+).*/\1/' <<<"$line")))
  skipped=$((skipped + $(sed -E 's/.*Skipped: *([0-9]+).*/\1/' <<<"$line")))
done
echo "Done in $(( $(date +%s) - start ))s: $passed passed, $failed failed, $skipped skipped."
grep -h '^  Failed ' "$out"/log-*.txt | sed 's/^  //' | sort -u
if [ "$reported" -ne "${#pids[@]}" ]; then
  echo "Only $reported of ${#pids[@]} processes reported; one stopped early, see $out/log-*.txt"
  status=1
fi
[ "$failed" -gt 0 ] && status=1
exit $status
