#!/bin/bash
# Prints the result of Unity's most recent script compile, read from the project's Editor.log.
# Safe to run while Unity is open. Usage: bash tools/compile-status.sh
LOG="$(dirname "$0")/../DW1Remake/Logs/Editor.log"
[ -f "$LOG" ] || { echo "No Editor.log found at $LOG"; exit 2; }

# Start of the last compile = last line that starts a Tundra/Bee build.
START=$(grep -nE "Csc Library/Bee" "$LOG" | tail -1 | cut -d: -f1)
[ -z "$START" ] && { echo "No compile found in log yet."; exit 0; }

# Walk back to the first Csc line of that same build (builds list Assembly-CSharp first).
BEGIN=$(head -n "$START" "$LOG" | grep -nE "requires additional run|Tundra build|Begin MonoManager" | tail -1 | cut -d: -f1)
BEGIN=${BEGIN:-1}
SEG=$(tail -n +"$BEGIN" "$LOG")

echo "$SEG" | grep -E "error CS[0-9]+" | sort -u | cut -c1-300
ERRS=$(echo "$SEG" | grep -cE "error CS[0-9]+")
if echo "$SEG" | grep -qE "Tundra build success|Reloading assemblies after finishing script compilation" && [ "$ERRS" -eq 0 ]; then
  echo "RESULT: compile OK ($(echo "$SEG" | grep -E 'warning CS' | sort -u | wc -l) distinct warnings)"
elif [ "$ERRS" -gt 0 ]; then
  echo "RESULT: compile FAILED ($ERRS error lines)"
else
  echo "RESULT: compile still running or unclear - run again in a few seconds"
fi
