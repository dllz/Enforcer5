#!/usr/bin/env bash
# Runs the whole test suite split over several test processes at once, by test class (the harness takes
# over static state, so its scenarios cannot run in parallel inside one process). Each process gets its
# own PostgreSQL schemas.
#
#   enf-sql subprod --run -- tools/test.sh [shards] [configuration=Debug]
#
# shards defaults to ENFORCER_TEST_SHARDS, else 6: each process uses up to 5 connections, subprod's role
# allows 64 (2026-11-04). Measured then: 2 processes 176 s, 4 146 s, 6 116-122 s, 8 115 s.
# Without ENFORCER_TEST_POSTGRES it still runs, the PostgreSQL tests skipped, as plain `dotnet test` does.
# Logs go to ENFORCER_TEST_OUT (default: a new temp directory). Each class's time in the last run with the
# database is kept in ENFORCER_TEST_TIMES (default ~/.cache/enforcer-test-times.txt) to split the next
# run evenly. Exits non-zero if any test failed or a process stopped early.
set -uo pipefail

shards=${1:-${ENFORCER_TEST_SHARDS:-6}}
config=${2:-Debug}
out=${ENFORCER_TEST_OUT:-$(mktemp -d -t enforcer-test.XXXXXX)}
times=${ENFORCER_TEST_TIMES:-${XDG_CACHE_HOME:-$HOME/.cache}/enforcer-test-times.txt}

cd "$(dirname "$0")/.."
mkdir -p "$out"
rm -f "$out"/log-*.txt "$out"/results-*.trx

[ -z "${ENFORCER_TEST_POSTGRES:-}" ] && echo "ENFORCER_TEST_POSTGRES is not set: the PostgreSQL tests are skipped." >&2

echo "Building $config..."
dotnet build Enforcer5.slnx -c "$config" -v q -nologo >"$out/build.log" 2>&1 || { echo "Build failed, see $out/build.log" >&2; exit 2; }

# Every [TestClass] with its weight: its seconds in the last run, or for a class not timed yet a guess from
# its number of tests (a harness or PostgreSQL test about a second, the rest next to nothing). Handed out
# heaviest first, each to the process with the least so far, so the processes end at about the same time.
# Dealing them out in turn by the guess left one process a minute behind the others.
plan=$(
  for f in $(grep -rl --include='*.cs' '\[TestClass\]' tests); do
    guess=$(grep -c '\[TestMethod\]' "$f")
    grep -q 'TestCategory("Harness")\|TestPostgres' "$f" || guess=$(awk -v g="$guess" 'BEGIN { print g / 50 }')
    awk -v g="$guess" '/\[TestClass\]/ { want = 1 } want && match($0, /class [A-Za-z0-9_]+/) {
      print substr($0, RSTART + 6, RLENGTH - 6), g; want = 0 }' "$f"
  done | awk -v times="$times" '
    BEGIN { while ((getline line < times) > 0) { split(line, p, " "); known[p[1]] = p[2] } }
    { print $1, (($1 in known) ? known[$1] : $2) }' \
  | sort -k2,2gr -k1,1 \
  | awk -v shards="$shards" '{
      best = 0; for (s = 1; s < shards; s++) if (load[s] + 0 < load[best] + 0) best = s
      load[best] += $2; print best, $1 }'
)

filters=()
i=0
while read -r shard class; do
  [ -z "${class:-}" ] && continue
  filters[$shard]="${filters[$shard]:+${filters[$shard]}|}FullyQualifiedName~.$class."
  i=$((i + 1))
done <<<"$plan"

echo "$i test classes in $shards processes; logs in $out"
start=$(date +%s)
pids=()
for ((s = 0; s < shards; s++)); do
  [ -z "${filters[$s]:-}" ] && continue
  dotnet test Enforcer5.slnx -c "$config" --no-build --filter "${filters[$s]}" \
    --logger "trx;LogFileName=$out/results-$s.trx" >"$out/log-$s.txt" 2>&1 &
  pids+=($!)
done

# Waits on the processes started above only (never pgrep, which can match this script itself).
status=0
for pid in "${pids[@]}"; do wait "$pid" || status=1; done

# Seconds per class from this run, for the next run's split. Only with the database: without it the
# PostgreSQL tests are skipped, and their time says nothing.
if [ -n "${ENFORCER_TEST_POSTGRES:-}" ] && ls "$out"/results-*.trx >/dev/null 2>&1; then
  mkdir -p "$(dirname "$times")"
  cat "$out"/results-*.trx | tr '>' '\n' | awk '
    /<UnitTest / && match($0, / id="[^"]+"/) { id = substr($0, RSTART + 5, RLENGTH - 6) }
    /<TestMethod / && match($0, /className="[^"]+"/) { c = substr($0, RSTART + 11, RLENGTH - 12); sub(/.*\./, "", c); cls[id] = c }
    /<UnitTestResult / && match($0, /testId="[^"]+"/) {
      t = substr($0, RSTART + 8, RLENGTH - 9)
      if (match($0, /duration="[^"]+"/)) { split(substr($0, RSTART + 10, RLENGTH - 11), d, ":"); dur[t] += d[1] * 3600 + d[2] * 60 + d[3] }
    }
    END { for (t in dur) if (t in cls) sum[cls[t]] += dur[t]; for (c in sum) printf "%s %.2f\n", c, sum[c] }' \
    | sort >"$times.new"
  if [ -s "$times.new" ]; then mv "$times.new" "$times"; else rm -f "$times.new"; fi
fi

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
