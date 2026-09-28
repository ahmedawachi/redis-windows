#!/usr/bin/env bash
# Benchmarks for a Redis build.
#
#   ci/bench.sh <bin-dir> <out.csv>
#       Runs the benchmark set against redis-server from <bin-dir> (127.0.0.1, random
#       high port, temp dir) and appends rows to <out.csv> (header written when the file
#       is new or empty). Columns: label,rep,test,params,metric,value
#
#   ci/bench.sh --compare <csv> <baseline-label> <candidate-label>
#       Prints a markdown table comparing the medians of the two labels.
#
# Tests (each run in its own redis-benchmark invocation so server CPU can be attributed):
#   small      SET, GET, HSET of 3-byte values at -P1 -c1 and at -P16 -c50
#   large      SET and GET of a BENCH_LARGE_BYTES value at -c4
#   hmget      HMGET key absexp sldexp data, data = BENCH_LARGE_BYTES, at -c1 and -c2
#              (the IDistributedCache read shape that failed in production)
#   hset       HSET key absexp sldexp data with a BENCH_LARGE_BYTES data field (stdin)
#   bgsave     DEBUG POPULATE to each size in BENCH_POPULATE_MB, then BENCH_BGSAVE_REPS
#              BGSAVEs: fork time (main-thread stall), BGSAVE wall time, child CPU
#
# Metrics: rps, avg_ms, p50_ms, p99_ms, max_ms (from redis-benchmark), server CPU as
# cpu_user_s, cpu_sys_s, cpu_total_s and cpu_us_per_op = (user + sys) / requests.
# User CPU is never reported alone.
#
# Environment (defaults in brackets):
#   BENCH_LABEL [basename of bin-dir]   BENCH_REPS [3]   BENCH_REP_START [1]
#   BENCH_N_SMALL [100000]  BENCH_N_PIPE [500000]  BENCH_N_LARGE [400]
#   BENCH_LARGE_BYTES [1900000]  BENCH_POPULATE_MB ["64 256"]  BENCH_BGSAVE_REPS [3]
#   BENCH_TESTS ["small large hmget hset bgsave"]
#   BENCH_ISOLATE_RUNTIME [auto]: see SMOKE_ISOLATE_RUNTIME in ci/smoke.sh.

set -Eeuo pipefail

log() { printf '[bench] %s\n' "$*" >&2; }
die() { printf '[bench] FAIL: %s\n' "$*" >&2; exit 1; }

# ------------------------------------------------------------------ compare mode
if [ "${1:-}" = "--compare" ]; then
    [ $# -eq 4 ] || { echo "usage: $0 --compare <csv> <baseline-label> <candidate-label>" >&2; exit 2; }
    [ -f "$2" ] || die "no such file: $2"
    awk -F, -v base="$3" -v cand="$4" '
        NR == 1 { next }
        $1 != base && $1 != cand { next }
        {
            key = $3 SUBSEP $4 SUBSEP $5
            if (!(key in seen)) { seen[key] = 1; order[++nkeys] = key }
            k = key SUBSEP $1
            vals[k, ++cnt[k]] = $6 + 0
        }
        function median(k,    n, i, j, t, a) {
            n = cnt[k]
            if (n == 0) return ""
            for (i = 1; i <= n; i++) a[i] = vals[k, i]
            for (i = 2; i <= n; i++) { t = a[i]; for (j = i - 1; j >= 1 && a[j] > t; j--) a[j + 1] = a[j]; a[j + 1] = t }
            return (n % 2) ? a[(n + 1) / 2] : (a[n / 2] + a[n / 2 + 1]) / 2
        }
        function fmt(v) {
            if (v == "") return "n/a"
            if (v >= 1000) return sprintf("%.0f", v)
            if (v >= 10) return sprintf("%.1f", v)
            return sprintf("%.3f", v)
        }
        END {
            printf "| Test | Parameters | Metric | %s | %s | %s / %s |\n", base, cand, cand, base
            print "|---|---|---|---:|---:|---:|"
            for (i = 1; i <= nkeys; i++) {
                split(order[i], p, SUBSEP)
                m = p[3]
                if (m !~ /^(rps|p50_ms|p99_ms|cpu_us_per_op|fork_ms|bgsave_wall_ms|child_cpu_s|used_memory_mb)$/) continue
                b = median(order[i] SUBSEP base); c = median(order[i] SUBSEP cand)
                ratio = "n/a"; verdict = ""
                if (b != "" && c != "" && b > 0) {
                    r = c / b
                    ratio = sprintf("%.2fx", r)
                    if (m != "used_memory_mb") {
                        better = (m == "rps") ? (r > 1.10) : (r < 0.90)
                        worse  = (m == "rps") ? (r < 0.90) : (r > 1.10)
                        if (better) verdict = " (better)"; else if (worse) verdict = " (worse)"
                    }
                }
                printf "| %s | %s | %s | %s | %s | %s%s |\n", p[1], p[2], m, fmt(b), fmt(c), ratio, verdict
            }
        }' "$2"
    exit 0
fi

# ------------------------------------------------------------------ benchmark mode
if [ $# -ne 2 ] || [ ! -d "$1" ]; then
    echo "usage: $0 <bin-dir> <out.csv>   |   $0 --compare <csv> <baseline-label> <candidate-label>" >&2
    exit 2
fi
BIN_DIR=$(cd "$1" && pwd)
OUT_CSV=$2
LABEL=${BENCH_LABEL:-$(basename "$BIN_DIR")}
REPS=${BENCH_REPS:-3}
REP_START=${BENCH_REP_START:-1}
N_SMALL=${BENCH_N_SMALL:-100000}
N_PIPE=${BENCH_N_PIPE:-500000}
N_LARGE=${BENCH_N_LARGE:-400}
LARGE_BYTES=${BENCH_LARGE_BYTES:-1900000}
POPULATE_MB=${BENCH_POPULATE_MB:-64 256}
BGSAVE_REPS=${BENCH_BGSAVE_REPS:-3}
TESTS=${BENCH_TESTS:-small large hmget hset bgsave}
case "$LABEL" in *,*) die "BENCH_LABEL must not contain a comma" ;; esac

IS_CYGWIN=0
case "$(uname -s)" in CYGWIN*|MSYS*|MINGW*) IS_CYGWIN=1 ;; esac
EXE=""
[ -f "$BIN_DIR/redis-server.exe" ] && EXE=".exe"
for b in redis-server redis-cli redis-benchmark; do
    [ -f "$BIN_DIR/$b$EXE" ] || { echo "$b$EXE not found in $BIN_DIR" >&2; exit 2; }
done

WORK=$(mktemp -d "${TMPDIR:-/tmp}/redis-bench.XXXXXX")
mkdir -p "$WORK/data"
SERVER_PID=""
PORT=""
native() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi; }

cleanup() {
    local rc=$?
    if [ -n "$SERVER_PID" ] && kill -0 "$SERVER_PID" 2>/dev/null; then
        "$CLI" -h 127.0.0.1 -p "$PORT" SHUTDOWN NOSAVE >/dev/null 2>&1 || true
        sleep 1
        kill -9 "$SERVER_PID" 2>/dev/null || true
        wait "$SERVER_PID" 2>/dev/null || true
    fi
    if [ "$rc" != 0 ] && [ -f "$WORK/redis.log" ]; then tail -n 40 "$WORK/redis.log" >&2 || true; fi
    rm -rf "$WORK"
    exit "$rc"
}
trap cleanup EXIT
trap 'exit 130' INT TERM
trap 'if [ "$BASH_SUBSHELL" = 0 ]; then printf "[bench] FAIL: command failed at line %s\n" "$LINENO" >&2; fi' ERR

RUN_DIR="$BIN_DIR"
if [ "$IS_CYGWIN" = "1" ] && [ "${BENCH_ISOLATE_RUNTIME:-auto}" != "no" ] &&
   { [ -f "$BIN_DIR/msys-2.0.dll" ] || [ -f "$BIN_DIR/cygwin1.dll" ]; }; then
    RUN_DIR="$WORK/bin"
    mkdir -p "$RUN_DIR"
    cp "$BIN_DIR"/*.exe "$RUN_DIR/"
fi
SERVER="$RUN_DIR/redis-server$EXE"
CLI="$RUN_DIR/redis-cli$EXE"
BENCH="$RUN_DIR/redis-benchmark$EXE"

rcli() { "$CLI" -h 127.0.0.1 -p "$PORT" "$@" | tr -d '\r'; }
info_field() { rcli INFO "$1" | sed -n "s/^$2://p"; }
port_open() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }
now_us() { rcli TIME | awk 'NR == 1 { s = $1 } NR == 2 { printf "%d%06d\n", s, $1 }'; }

if [ ! -s "$OUT_CSV" ]; then echo "label,rep,test,params,metric,value" >"$OUT_CSV"; fi
REP=0
row() { echo "$LABEL,$REP,$1,$2,$3,$4" >>"$OUT_CSV"; }

start_server() {
    local i
    for _ in 1 2 3 4 5; do
        PORT=$(( ( (RANDOM << 15) | RANDOM ) % 29000 + 20000 ))  # below the dynamic range
        port_open "$PORT" && continue
        "$SERVER" --port "$PORT" --bind 127.0.0.1 --daemonize no --save "" --appendonly no \
            --dir "$(native "$WORK/data")" --logfile "$(native "$WORK/redis.log")" \
            --enable-debug-command local >/dev/null 2>&1 &
        SERVER_PID=$!
        for i in $(seq 1 120); do
            kill -0 "$SERVER_PID" 2>/dev/null || { wait "$SERVER_PID" 2>/dev/null || true; SERVER_PID=""; continue 2; }
            [ "$("$CLI" -h 127.0.0.1 -p "$PORT" PING 2>/dev/null | tr -d '\r')" = "PONG" ] && return 0
            sleep 0.25
        done
        die "server did not answer PING within 30 s"
    done
    die "could not start redis-server"
}

stop_server() {
    local i
    rcli SHUTDOWN NOSAVE >/dev/null 2>&1 || true
    for i in $(seq 1 120); do kill -0 "$SERVER_PID" 2>/dev/null || break; sleep 0.25; done
    kill -0 "$SERVER_PID" 2>/dev/null && die "server did not stop"
    wait "$SERVER_PID" 2>/dev/null || true
    SERVER_PID=""
    for i in $(seq 1 40); do port_open "$PORT" || return 0; sleep 0.25; done
    die "port $PORT still in use after shutdown"
}

cpu_snapshot() { # prints "user sys" of the server process (children excluded)
    rcli INFO cpu | awk -F: '$1 == "used_cpu_user" { u = $2 } $1 == "used_cpu_sys" { s = $2 } END { print u + 0, s + 0 }'
}

# run_bench <test> <params> <requests> [stdin-file|-] <redis-benchmark args...>
run_bench() {
    local test=$1 params=$2 n=$3 input=$4 before after out
    shift 4
    before=$(cpu_snapshot)
    if [ "$input" = "-" ]; then
        out=$("$BENCH" -h 127.0.0.1 -p "$PORT" --csv -n "$n" "$@" | tr -d '\r')
    else
        out=$("$BENCH" -h 127.0.0.1 -p "$PORT" --csv -n "$n" "$@" <"$input" | tr -d '\r')
    fi
    after=$(cpu_snapshot)
    # CSV: "test","rps","avg_latency_ms","min_latency_ms","p50_latency_ms","p95_latency_ms","p99_latency_ms","max_latency_ms"
    local line
    line=$(printf '%s\n' "$out" | grep -v '^"test"' | grep '^"' | sed -n '$p' | tr -d '"')
    [ -n "$line" ] || die "no CSV output from redis-benchmark for $test ($params): $out"
    printf '%s\n' "$line" | awk -F, -v t="$test" -v p="$params" -v lbl="$LABEL" -v rep="$REP" \
        -v n="$n" -v b="$before" -v a="$after" '{
            split(b, cb, " "); split(a, ca, " ")
            u = ca[1] - cb[1]; s = ca[2] - cb[2]
            printf "%s,%s,%s,%s,rps,%s\n", lbl, rep, t, p, $2
            printf "%s,%s,%s,%s,avg_ms,%s\n", lbl, rep, t, p, $3
            printf "%s,%s,%s,%s,p50_ms,%s\n", lbl, rep, t, p, $5
            printf "%s,%s,%s,%s,p99_ms,%s\n", lbl, rep, t, p, $7
            printf "%s,%s,%s,%s,max_ms,%s\n", lbl, rep, t, p, $8
            printf "%s,%s,%s,%s,cpu_user_s,%.6f\n", lbl, rep, t, p, u
            printf "%s,%s,%s,%s,cpu_sys_s,%.6f\n", lbl, rep, t, p, s
            printf "%s,%s,%s,%s,cpu_total_s,%.6f\n", lbl, rep, t, p, u + s
            printf "%s,%s,%s,%s,cpu_us_per_op,%.3f\n", lbl, rep, t, p, (u + s) * 1000000 / n
        }' >>"$OUT_CSV"
    log "$test [$params]: $(printf '%s' "$line" | cut -d, -f2) rps, p99 $(printf '%s' "$line" | cut -d, -f7) ms"
}

wait_bgsave() { # <rdb_saves before>
    local i
    for i in $(seq 1 6000); do
        if [ "$(info_field persistence rdb_saves)" -gt "$1" ] &&
           [ "$(info_field persistence rdb_bgsave_in_progress)" = "0" ]; then
            [ "$(info_field persistence rdb_last_bgsave_status)" = ok ] || die "BGSAVE failed"
            return 0
        fi
        sleep 0.05
    done
    die "BGSAVE did not finish within ~5 minutes"
}

bench_bgsave() {
    local mb keys used i saves t0 t1 c0 c1
    for mb in $POPULATE_MB; do
        rcli FLUSHALL >/dev/null
        # ~1150 bytes of used_memory per key with 1024-byte values.
        keys=$(( mb * 1024 * 1024 / 1150 ))
        [ "$(rcli DEBUG POPULATE "$keys" bench:pop 1024)" = OK ] || die "DEBUG POPULATE failed"
        used=$(info_field memory used_memory)
        row bgsave "populate=${mb}MB" used_memory_mb "$(awk -v u="$used" 'BEGIN { printf "%.1f", u / 1048576 }')"
        for i in $(seq 1 "$BGSAVE_REPS"); do
            saves=$(info_field persistence rdb_saves)
            c0=$(rcli INFO cpu | awk -F: '$1 ~ /^used_cpu_(user|sys)_children$/ { t += $2 } END { print t + 0 }')
            t0=$(now_us)
            [ "$(rcli BGSAVE)" = "Background saving started" ] || die "BGSAVE refused"
            wait_bgsave "$saves"
            t1=$(now_us)
            c1=$(rcli INFO cpu | awk -F: '$1 ~ /^used_cpu_(user|sys)_children$/ { t += $2 } END { print t + 0 }')
            row bgsave "populate=${mb}MB" fork_ms "$(awk -v f="$(info_field stats latest_fork_usec)" 'BEGIN { printf "%.3f", f / 1000 }')"
            row bgsave "populate=${mb}MB" bgsave_wall_ms "$(awk -v a="$t0" -v b="$t1" 'BEGIN { printf "%.1f", (b - a) / 1000 }')"
            row bgsave "populate=${mb}MB" child_cpu_s "$(awk -v a="$c0" -v b="$c1" 'BEGIN { printf "%.3f", b - a }')"
            log "bgsave [${mb}MB] #$i: fork $(info_field stats latest_fork_usec) us, wall $(( (t1 - t0) / 1000 )) ms"
        done
        rm -f "$WORK/data/dump.rdb"
    done
    rcli FLUSHALL >/dev/null
}

bytes=$(( LARGE_BYTES * 3 / 4 ))
head -c "$bytes" /dev/urandom | base64 | tr -d '\r\n' >"$WORK/blob"
LARGE_ACTUAL=$(wc -c <"$WORK/blob" | tr -d ' ')

log "label=$LABEL reps=$REPS tests=[$TESTS] large=$LARGE_ACTUAL bytes"
"$SERVER" --version >&2

for REP in $(seq "$REP_START" $(( REP_START + REPS - 1 ))); do
    log "=== repetition $REP"
    start_server
    for t in $TESTS; do
        case "$t" in
            small)
                for cmd in set get hset; do
                    run_bench "$cmd" "c=1 P=1 d=3" "$N_SMALL" - -t "$cmd" -c 1 -P 1
                    run_bench "$cmd" "c=50 P=16 d=3" "$N_PIPE" - -t "$cmd" -c 50 -P 16
                done
                ;;
            large)
                run_bench set "c=4 P=1 d=$LARGE_ACTUAL" "$N_LARGE" - -t set -c 4 -d "$LARGE_ACTUAL"
                run_bench get "c=4 P=1 d=$LARGE_ACTUAL" "$N_LARGE" - -t get -c 4 -d "$LARGE_ACTUAL"
                rcli DEL "key:__rand_int__" >/dev/null
                ;;
            hmget)
                rcli HSET bench:cache absexp -1 sldexp 1200000000 >/dev/null
                rcli -x HSET bench:cache data <"$WORK/blob" >/dev/null
                [ "$(rcli HSTRLEN bench:cache data)" = "$LARGE_ACTUAL" ] || die "HMGET fixture was not stored"
                run_bench hmget "c=1 d=$LARGE_ACTUAL" "$N_LARGE" - -c 1 HMGET bench:cache absexp sldexp data
                run_bench hmget "c=2 d=$LARGE_ACTUAL" "$N_LARGE" - -c 2 HMGET bench:cache absexp sldexp data
                rcli DEL bench:cache >/dev/null
                ;;
            hset)
                run_bench hset-large "c=1 d=$LARGE_ACTUAL" "$N_LARGE" "$WORK/blob" \
                    -c 1 -x HSET bench:hset absexp -1 sldexp 1200000000 data
                rcli DEL bench:hset >/dev/null
                ;;
            bgsave) bench_bgsave ;;
            *) die "unknown test '$t' in BENCH_TESTS" ;;
        esac
    done
    stop_server
done
log "rows written to $OUT_CSV"
