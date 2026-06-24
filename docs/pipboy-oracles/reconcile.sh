#!/usr/bin/env bash
# Reconcile the computed Pip-Boy (CLI `pipboy`) against a ground-truth oracle file.
# Usage: reconcile.sh <oracle-file> [save.fos]
#   <oracle-file>  one of docs/pipboy-oracles/*.oracle (active=/completed= lines + a save= line)
#   [save.fos]     optional override for the oracle's `save =` path
#
# Persists the §6 #16 measurement that used to live in the ephemeral /tmp/gt/compare.sh.
# Run from the repo root so `dotnet run` resolves the CLI project.
set -euo pipefail

oracle="${1:?usage: reconcile.sh <oracle-file> [save.fos]}"
save="${2:-$(sed -n 's/^save *= *//p' "$oracle")}"
[ -f "$save" ] || { echo "save not found: $save" >&2; exit 1; }

tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

# Ground truth (sorted, state-split).
sed -n 's/^active *= *//p'    "$oracle" | sort > "$tmp/g_active"
sed -n 's/^completed *= *//p' "$oracle" | sort > "$tmp/g_completed"
cat "$tmp/g_active" "$tmp/g_completed" | sort > "$tmp/g_all"

# Computed (strip the `[state]`, the trailing 0x… FormID and any `(SGE)` tag).
out="$(dotnet run --project src/FnvSaveExplorer.Cli -- pipboy "$save" 2>/dev/null)"
strip='s/^\[[a-z]*\] +//; s/ +0x[0-9A-F]+.*$//; s/ +$//'
echo "$out" | grep -E '^\[active\]'    | sed -E "$strip" | sort > "$tmp/c_active"
echo "$out" | grep -E '^\[completed\]' | sed -E "$strip" | sort > "$tmp/c_completed"
cat "$tmp/c_active" "$tmp/c_completed" | sort > "$tmp/c_all"

ca=$(comm -12 "$tmp/c_active" "$tmp/g_active" | grep -c . || true)
cc=$(comm -12 "$tmp/c_completed" "$tmp/g_completed" | grep -c . || true)
both=$(comm -12 "$tmp/c_all" "$tmp/g_all" | grep -c . || true)
mis=$((both - ca - cc))                 # in Pip-Boy + computed, but wrong active/completed state
fp=$(comm -23 "$tmp/c_all" "$tmp/g_all" | grep -c . || true)   # computed, not in Pip-Boy
miss=$(comm -13 "$tmp/c_all" "$tmp/g_all" | grep -c . || true) # in Pip-Boy, not computed
truth=$(grep -c . "$tmp/g_all"); comp=$(grep -c . "$tmp/c_all")
prec=$(awk "BEGIN{ if($comp) printf \"%.0f\", 100*($comp-$fp)/$comp; else print 0 }")

echo "$(basename "$oracle"): correct=$((ca+cc))  mislabelled=$mis  falsePos=$fp  missed=$miss   [computed=$comp, truth=$truth, precision=${prec}%]"
echo "--- FALSE POSITIVES (computed, not in Pip-Boy) ---"; comm -23 "$tmp/c_all" "$tmp/g_all"
echo "--- MISSED (in Pip-Boy, not computed) ---";          comm -13 "$tmp/c_all" "$tmp/g_all"
