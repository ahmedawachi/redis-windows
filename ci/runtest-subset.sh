#!/usr/bin/env bash
# Runs a subset of the Redis Tcl test suite against a built source tree:
#   ci/runtest-subset.sh <redis-source-dir>
#
# The tree must already be built (src/redis-server, src/redis-cli, src/redis-benchmark,
# src/redis-check-aof). Needs tclsh 8.5 or newer; in CI install the "tcl" and
# "procps-ng" packages of the MSYS2/Cygwin environment (procps-ng provides pgrep,
# which the suite uses to find BGSAVE children).
#
# Environment (defaults in brackets):
#   RUNTEST_UNITS     units to run [unit/type/hash unit/type/string unit/expire
#                     integration/rdb unit/info unit/networking unit/querybuf
#                     unit/win-soft-oom]. Units missing from the tree are skipped with a
#                     notice (unit/win-soft-oom only exists when the fork's patches are
#                     applied).
#   RUNTEST_SKIPLIST  skip list file [ci/runtest-skiplist.txt]
#   RUNTEST_APPLY_SKIPLIST  auto | yes | no [auto = only on Cygwin/MSYS2]
#   RUNTEST_CLIENTS   parallel test clients [4]
#   RUNTEST_TIMEOUT   per-test timeout in seconds [600]
#   RUNTEST_BASEPORT  first port for spawned servers [21111, the suite's default]; set a
#                     different value (1024-47535) when another test run shares the machine
#   RUNTEST_ARGS      extra arguments for tests/test_helper.tcl

set -Eeuo pipefail

if [ $# -ne 1 ] || [ ! -d "$1" ]; then
    echo "usage: $0 <redis-source-dir>" >&2
    exit 2
fi
SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
SRC=$(cd "$1" && pwd)

UNITS=${RUNTEST_UNITS:-unit/type/hash unit/type/string unit/expire integration/rdb unit/info unit/networking unit/querybuf unit/win-soft-oom}
SKIPLIST=${RUNTEST_SKIPLIST:-$SCRIPT_DIR/runtest-skiplist.txt}
CLIENTS=${RUNTEST_CLIENTS:-4}
TIMEOUT=${RUNTEST_TIMEOUT:-600}

IS_CYGWIN=0
case "$(uname -s)" in CYGWIN*|MSYS*|MINGW*) IS_CYGWIN=1 ;; esac
APPLY=${RUNTEST_APPLY_SKIPLIST:-auto}
if [ "$APPLY" = auto ]; then
    if [ "$IS_CYGWIN" = 1 ]; then APPLY=yes; else APPLY=no; fi
fi

EXE=""
[ -f "$SRC/src/redis-server.exe" ] && EXE=".exe"
for b in redis-server redis-cli redis-benchmark redis-check-aof; do
    [ -f "$SRC/src/$b$EXE" ] || { echo "$SRC/src/$b$EXE not found - build the tree first" >&2; exit 2; }
done
[ -f "$SRC/tests/test_helper.tcl" ] || { echo "$SRC/tests/test_helper.tcl not found" >&2; exit 2; }

TCLSH=""
for t in tclsh8.6 tclsh8.5 tclsh8.7 tclsh9.0 tclsh; do
    if command -v "$t" >/dev/null 2>&1; then TCLSH=$(command -v "$t"); break; fi
done
[ -n "$TCLSH" ] || { echo "tclsh not found: install Tcl 8.5 or newer" >&2; exit 2; }
tcl_version=$(echo 'puts [info patchlevel]' | "$TCLSH")
case "$tcl_version" in 8.[0-4]*|[0-7].*) echo "Tcl $tcl_version is too old (8.5 or newer needed)" >&2; exit 2 ;; esac

# The suite opens many sockets; mirror what ./runtest does.
limit=$(ulimit -n 2>/dev/null || echo unlimited)
if [ "$limit" != unlimited ] && [ "$limit" -lt 1024 ]; then ulimit -n 1024 2>/dev/null || true; fi

BASEPORT=${RUNTEST_BASEPORT:-21111}
# The suite spreads 8000 ports over its clients and also binds port+10000 (cluster bus).
case "$BASEPORT" in ''|*[!0-9]*) echo "RUNTEST_BASEPORT must be a number" >&2; exit 2 ;; esac
if [ "$BASEPORT" -lt 1024 ] || [ $(( BASEPORT + 8000 + 10000 )) -gt 65535 ]; then
    echo "RUNTEST_BASEPORT must be between 1024 and 47535 (the suite uses ports up to base+18000)" >&2
    exit 2
fi
args=(--clients "$CLIENTS" --timeout "$TIMEOUT" --dump-logs --baseport "$BASEPORT")
selected=()
for u in $UNITS; do
    if [ -f "$SRC/tests/$u.tcl" ]; then
        args+=(--single "$u")
        selected+=("$u")
    else
        echo "notice: tests/$u.tcl does not exist in this tree; skipping it"
    fi
done
[ ${#selected[@]} -gt 0 ] || { echo "none of the requested units exist" >&2; exit 2; }

TMP_SKIP=""
# shellcheck disable=SC2317,SC2329 # invoked by the EXIT trap (SC2317 before shellcheck 0.11)
cleanup() { if [ -n "$TMP_SKIP" ]; then rm -f "$TMP_SKIP"; fi; }
trap cleanup EXIT

if [ "$APPLY" = yes ] && [ -f "$SKIPLIST" ]; then
    # Format: one entry per line; '#' starts a comment. "unit:<name>" skips a whole
    # unit; anything else is a test name, or a regexp when it starts with '/'.
    TMP_SKIP=$(mktemp "${TMPDIR:-/tmp}/runtest-skip.XXXXXX")
    while IFS= read -r line || [ -n "$line" ]; do
        line=${line%$'\r'}
        line=${line%%#*}
        line=$(printf '%s' "$line" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')
        [ -n "$line" ] || continue
        case "$line" in
            unit:*) args+=(--skipunit "${line#unit:}") ;;
            *) printf '%s\n' "$line" >>"$TMP_SKIP" ;;
        esac
    done <"$SKIPLIST"
    args+=(--skipfile "$TMP_SKIP")
    echo "skip list: $SKIPLIST ($(wc -l <"$TMP_SKIP" | tr -d ' ') test patterns)"
elif [ "$APPLY" = yes ]; then
    echo "notice: skip list $SKIPLIST not found; running without it"
fi

# shellcheck disable=SC2206 # RUNTEST_ARGS is intentionally word-split
extra=(${RUNTEST_ARGS:-})
echo "tclsh: $TCLSH ($tcl_version)"
echo "units: ${selected[*]}"
cd "$SRC"
set +e
"$TCLSH" tests/test_helper.tcl "${args[@]}" ${extra[@]+"${extra[@]}"}
rc=$?
set -e
if [ "$rc" = 0 ]; then echo "runtest subset: PASS"; else echo "runtest subset: FAIL (exit code $rc)"; fi
exit "$rc"
