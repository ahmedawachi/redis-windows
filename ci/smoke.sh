#!/usr/bin/env bash
# Smoke gate for a Redis build: ci/smoke.sh <bin-dir>
#
# Starts redis-server from <bin-dir> on 127.0.0.1 and a random high port, with its
# data in a temporary directory, and checks:
#   PING, SET/GET, HSET of a 2 MB field + HMGET byte equality, EXPIRE/TTL/PERSIST,
#   the fork's features (see SMOKE_FORK_FEATURES), BGSAVE followed by
#   DEBUG RELOAD NOSAVE (loads the file the fork child wrote) and DEBUG RELOAD, both
#   with DEBUG DIGEST equality, SHUTDOWN SAVE (exit code 0), a restart that loads the
#   saved file with the same digest, and finally that the port is free again.
#
# Runs in the MSYS2 and Cygwin bash shells and on macOS/Linux.
#
# Environment:
#   SMOKE_FORK_FEATURES   auto (default) | yes | no | a list of: meminfo softoom
#       meminfo: on Cygwin/MSYS2, INFO memory has process_private_bytes,
#                process_working_set, system_commit_total and system_commit_limit
#                (all > 0, total < limit) and used_memory_rss != used_memory.
#                Skipped on other platforms.
#       softoom: config oom-soft-client-buffers and INFO stats
#                client_oom_disconnections exist; with DEBUG SET-ALLOC-FAIL-THRESHOLD
#                65536 a 2 MB HMGET disconnects only that client (counted); at 1048576
#                a 2 MB HSET is refused without executing; the server keeps answering,
#                and everything works again at threshold 0.
#       yes = "meminfo softoom"; no = none (upstream/baseline builds);
#       auto = whichever of the two the server exposes.
#   SMOKE_ISOLATE_RUNTIME auto (default) | no
#       On Cygwin/MSYS2, when <bin-dir> carries its own msys-2.0.dll/cygwin1.dll,
#       copy the .exe files into the temp dir so they load the shell's runtime. A
#       child started from a Cygwin shell only accepts its parent's startup data when
#       both use the same runtime build; the packaged DLLs are copies of the build
#       shell's own, so this tests the same code.
#   SMOKE_KEEP_TMP=1      keep the temp dir (for debugging).
#   SMOKE_ARTIFACT_DIR    where to save both values when the 2 MB HMGET comparison fails.

set -Eeuo pipefail

log()  { printf '[smoke] %s\n' "$*"; }
die()  { printf '[smoke] FAIL: %s\n' "$*" >&2; dump_log; exit 1; }
step() { printf '[smoke] --- %s\n' "$*"; }

if [ $# -ne 1 ] || [ ! -d "$1" ]; then
    echo "usage: $0 <bin-dir>   (directory containing redis-server and redis-cli)" >&2
    exit 2
fi
BIN_DIR=$(cd "$1" && pwd)

# A missing tool must fail as such: without cmp, every byte comparison below would read as a mismatch.
for tool in cmp cksum base64 head tr wc mktemp; do
    command -v "$tool" >/dev/null 2>&1 ||
        { echo "[smoke] FAIL: required tool '$tool' is not installed (MSYS2: pacman -S diffutils coreutils)" >&2; exit 2; }
done

IS_CYGWIN=0
case "$(uname -s)" in
    CYGWIN*|MSYS*|MINGW*) IS_CYGWIN=1 ;;
esac

EXE=""
[ -f "$BIN_DIR/redis-server.exe" ] && EXE=".exe"
[ -f "$BIN_DIR/redis-server$EXE" ] || { echo "redis-server not found in $BIN_DIR" >&2; exit 2; }
[ -f "$BIN_DIR/redis-cli$EXE" ] || { echo "redis-cli not found in $BIN_DIR" >&2; exit 2; }

WORK=$(mktemp -d "${TMPDIR:-/tmp}/redis-smoke.XXXXXX")
mkdir -p "$WORK/data"
SERVER_PID=""
PORT=""

# Windows builds get Windows-style paths (C:/...): a packaged Cygwin runtime resolves
# POSIX paths against its own root, not the shell's.
native() {
    if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi
}

dump_log() {
    if [ -n "${WORK:-}" ] && [ -f "$WORK/redis.log" ]; then
        echo "----- last 60 lines of the redis log -----" >&2
        tail -n 60 "$WORK/redis.log" >&2 || true
        echo "------------------------------------------" >&2
    fi
}

cleanup() {
    local rc=$?
    if [ -n "$SERVER_PID" ] && kill -0 "$SERVER_PID" 2>/dev/null; then
        log "cleanup: killing leftover redis-server (pid $SERVER_PID)"
        kill -9 "$SERVER_PID" 2>/dev/null || true
        wait "$SERVER_PID" 2>/dev/null || true
    fi
    if [ "${SMOKE_KEEP_TMP:-0}" = "1" ]; then
        log "temp dir kept: $WORK"
    else
        rm -rf "$WORK"
    fi
    exit "$rc"
}
trap cleanup EXIT
trap 'exit 130' INT TERM
# Report unexpected failures at top level only; probes inside $(...) are allowed to fail.
trap 'if [ "$BASH_SUBSHELL" = 0 ]; then printf "[smoke] FAIL: command failed at line %s\n" "$LINENO" >&2; dump_log; fi' ERR

RUN_DIR="$BIN_DIR"
if [ "$IS_CYGWIN" = "1" ] && [ "${SMOKE_ISOLATE_RUNTIME:-auto}" != "no" ] &&
   { [ -f "$BIN_DIR/msys-2.0.dll" ] || [ -f "$BIN_DIR/cygwin1.dll" ]; }; then
    RUN_DIR="$WORK/bin"
    mkdir -p "$RUN_DIR"
    cp "$BIN_DIR"/*.exe "$RUN_DIR/"
    log "bin dir carries its own Cygwin runtime; running copies of its .exe files with the shell's runtime"
fi
SERVER="$RUN_DIR/redis-server$EXE"
CLI="$RUN_DIR/redis-cli$EXE"

rcli() { "$CLI" -h 127.0.0.1 -p "$PORT" "$@" | tr -d '\r'; }

expect_eq() { # <what> <expected> <actual>
    [ "$3" = "$2" ] || die "$1: expected '$2', got '$3'"
    log "ok: $1"
}

# 0 if something accepts TCP connections on 127.0.0.1:$1.
port_open() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }

pick_port() {
    local p i
    for i in 1 2 3 4 5 6 7 8 9 10; do
        p=$(( ( (RANDOM << 15) | RANDOM ) % 40000 + 20000 ))
        if ! port_open "$p"; then echo "$p"; return 0; fi
    done
    return 1
}

info_field() { # <section> <field>
    rcli INFO "$1" | sed -n "s/^$2://p"
}

start_server() {
    local attempt i
    for attempt in 1 2 3 4 5; do
        PORT=$(pick_port) || die "no free port found"
        log "starting $SERVER on 127.0.0.1:$PORT (attempt $attempt)"
        "$SERVER" --port "$PORT" --bind 127.0.0.1 --protected-mode yes \
            --daemonize no --save "" --appendonly no \
            --dir "$(native "$WORK/data")" --logfile "$(native "$WORK/redis.log")" \
            --enable-debug-command local >>"$WORK/stdout.log" 2>&1 &
        SERVER_PID=$!
        for i in $(seq 1 120); do
            if ! kill -0 "$SERVER_PID" 2>/dev/null; then
                wait "$SERVER_PID" 2>/dev/null || true
                SERVER_PID=""
                if grep -q "Address already in use" "$WORK/redis.log" 2>/dev/null; then
                    log "port $PORT was taken meanwhile, retrying"
                    continue 2
                fi
                cat "$WORK/stdout.log" >&2 || true
                die "redis-server exited during start-up"
            fi
            if [ "$("$CLI" -h 127.0.0.1 -p "$PORT" PING 2>/dev/null | tr -d '\r')" = "PONG" ]; then
                log "server is up (pid $SERVER_PID) after ~$(( i / 4 )) s"
                return 0
            fi
            sleep 0.25
        done
        die "server did not answer PING within 30 s"
    done
    die "could not start redis-server after 5 attempts"
}

# SHUTDOWN <mode>, then require exit code 0 and a free port.
stop_server() {
    local mode=$1 rc=0 i
    rcli SHUTDOWN "$mode" >/dev/null 2>&1 || true
    for i in $(seq 1 240); do
        kill -0 "$SERVER_PID" 2>/dev/null || break
        sleep 0.25
    done
    kill -0 "$SERVER_PID" 2>/dev/null && die "server still running 60 s after SHUTDOWN $mode"
    wait "$SERVER_PID" || rc=$?
    SERVER_PID=""
    expect_eq "exit code after SHUTDOWN $mode" 0 "$rc"
    for i in $(seq 1 40); do
        port_open "$PORT" || { log "ok: port $PORT is free"; return 0; }
        sleep 0.25
    done
    die "port $PORT still accepts connections after shutdown"
}

# rdb_saves is incremented when BGSAVE starts; rdb_bgsave_in_progress drops back to 0
# once the child has finished and the parent has processed its exit.
wait_bgsave() { # <rdb_saves before>
    local i
    for i in $(seq 1 480); do
        if [ "$(info_field persistence rdb_saves)" -gt "$1" ] &&
           [ "$(info_field persistence rdb_bgsave_in_progress)" = "0" ]; then
            expect_eq "rdb_last_bgsave_status" ok "$(info_field persistence rdb_last_bgsave_status)"
            return 0
        fi
        sleep 0.25
    done
    die "BGSAVE did not finish within 120 s"
}

log "binaries: $BIN_DIR"
"$SERVER" --version

start_server

step "PING"
expect_eq "PING" PONG "$(rcli PING)"

step "SET/GET"
expect_eq "SET" OK "$(rcli SET smoke:str hello-windows)"
expect_eq "GET" hello-windows "$(rcli GET smoke:str)"

# On an HMGET mismatch, record how the values differ and whether the fork's reply options change the
# outcome, so a single CI run tells a reply-path bug apart from a client or harness problem.
diagnose_hmget_mismatch() { # <expected-file> <actual-file>
    local exp=$1 act=$2 setting again
    log "expected $(wc -c <"$exp" | tr -d ' ') bytes (cksum $(cksum <"$exp" | cut -d' ' -f1)), got $(wc -c <"$act" | tr -d ' ') bytes (cksum $(cksum <"$act" | cut -d' ' -f1))"
    log "first difference: $(cmp "$exp" "$act" 2>&1 | head -1)"
    log "got, first 120 bytes: $(head -c 120 "$act" | LC_ALL=C tr -c '[:print:]' '.')"
    log "server stats: total_writes_processed=$(info_field stats total_writes_processed) client_oom_disconnections=$(info_field stats client_oom_disconnections)"
    for setting in "reply-node-max-bytes 0" "oom-soft-client-buffers no"; do
        # shellcheck disable=SC2086 # setting is "name value" on purpose
        if [ "$(rcli CONFIG SET $setting 2>/dev/null)" = OK ]; then
            again="$WORK/hmget-retry.out"
            "$CLI" -h 127.0.0.1 -p "$PORT" HMGET smoke:hash data >"$again" 2>&1 || true
            if cmp -s "$exp" "$again"; then log "retry with $setting: the value is correct"; else log "retry with $setting: still different"; fi
        else
            log "retry with $setting: not supported by this build"
        fi
    done
    if [ -n "${SMOKE_ARTIFACT_DIR:-}" ] && mkdir -p "$SMOKE_ARTIFACT_DIR"; then
        cp "$exp" "$SMOKE_ARTIFACT_DIR/hmget.expected" && cp "$act" "$SMOKE_ARTIFACT_DIR/hmget.out" &&
            log "saved both values to $SMOKE_ARTIFACT_DIR"
    fi
}

step "HSET 2 MB field + HMGET"
BLOB_BYTES=2097152
# 1572864 random bytes are exactly 2097152 base64 characters (no truncation, no SIGPIPE).
head -c 1572864 /dev/urandom | base64 | tr -d '\r\n' >"$WORK/blob"
expect_eq "payload size" "$BLOB_BYTES" "$(wc -c <"$WORK/blob" | tr -d ' ')"
expect_eq "HSET absexp/sldexp" 2 "$(rcli HSET smoke:hash absexp -1 sldexp 1200000000)"
expect_eq "HSET data (2 MB, via stdin)" 1 "$(rcli -x HSET smoke:hash data <"$WORK/blob")"
expect_eq "HSTRLEN data" "$BLOB_BYTES" "$(rcli HSTRLEN smoke:hash data)"
"$CLI" -h 127.0.0.1 -p "$PORT" HMGET smoke:hash data >"$WORK/hmget.out"
{ cat "$WORK/blob"; printf '\n'; } >"$WORK/hmget.expected"
cmp -s "$WORK/hmget.expected" "$WORK/hmget.out" ||
    { diagnose_hmget_mismatch "$WORK/hmget.expected" "$WORK/hmget.out"; die "HMGET returned a different 2 MB value"; }
log "ok: HMGET returned the same 2 MB value"
expect_eq "HMGET small fields" "$(printf -- '-1\n1200000000')" "$(rcli HMGET smoke:hash absexp sldexp)"

step "EXPIRE/TTL"
rcli SET smoke:ttl v >/dev/null
expect_eq "EXPIRE" 1 "$(rcli EXPIRE smoke:ttl 100)"
ttl=$(rcli TTL smoke:ttl)
{ [ "$ttl" -ge 1 ] && [ "$ttl" -le 100 ]; } || die "TTL out of range: $ttl"
log "ok: TTL=$ttl"
expect_eq "PERSIST" 1 "$(rcli PERSIST smoke:ttl)"
expect_eq "TTL after PERSIST" -1 "$(rcli TTL smoke:ttl)"
rcli SET smoke:gone v >/dev/null
expect_eq "PEXPIRE" 1 "$(rcli PEXPIRE smoke:gone 200)"
sleep 1
expect_eq "key expired" 0 "$(rcli EXISTS smoke:gone)"

step "fork features"
soft_oom_cfg=$(rcli CONFIG GET oom-soft-client-buffers | sed -n 2p)
features=${SMOKE_FORK_FEATURES:-auto}
case "$features" in
    auto)
        features=""
        [ -n "$(info_field memory process_private_bytes)" ] && features="meminfo"
        [ -n "$soft_oom_cfg" ] && features="$features softoom"
        log "SMOKE_FORK_FEATURES=auto resolved to '${features# }'"
        ;;
    yes) features="meminfo softoom" ;;
    no) features="" ;;
esac
for f in $features; do
    case "$f" in meminfo|softoom) ;; *) die "unknown SMOKE_FORK_FEATURES entry '$f' (auto, yes, no, meminfo, softoom)" ;; esac
done
has_feature() { case " $features " in *" $1 "*) return 0 ;; esac; return 1; }

if has_feature meminfo; then
    if [ "$IS_CYGWIN" = "1" ]; then
        for f in process_private_bytes process_working_set system_commit_total system_commit_limit; do
            v=$(info_field memory "$f")
            case "$v" in ''|*[!0-9]*) die "INFO memory $f missing or not a number: '$v'" ;; esac
            [ "$v" -gt 0 ] || die "INFO memory $f is 0"
            log "ok: $f=$v"
        done
        [ "$(info_field memory system_commit_total)" -lt "$(info_field memory system_commit_limit)" ] ||
            die "system_commit_total is not below system_commit_limit"
        rss=$(info_field memory used_memory_rss)
        [ "$rss" != "$(info_field memory used_memory)" ] || die "used_memory_rss still equals used_memory"
        log "ok: used_memory_rss=$rss differs from used_memory"
    else
        log "skip: INFO memory Windows fields (not a Cygwin/MSYS2 build)"
    fi
fi

if has_feature softoom; then
    case "$soft_oom_cfg" in
        yes|no) log "ok: oom-soft-client-buffers=$soft_oom_cfg" ;;
        *) die "config oom-soft-client-buffers missing or invalid: '$soft_oom_cfg'" ;;
    esac
    oom_count() {
        local v
        v=$(info_field stats client_oom_disconnections)
        case "$v" in ''|*[!0-9]*) die "INFO stats client_oom_disconnections missing or not a number: '$v'" ;; esac
        echo "$v"
    }
    log "ok: client_oom_disconnections=$(oom_count)"
    # Fault injection: soft (try-path) allocations of the threshold size or more fail.
    expect_eq "CONFIG SET oom-soft-client-buffers yes" OK "$(rcli CONFIG SET oom-soft-client-buffers yes)"
    # Reply path: HMGET of the 2 MB field. On Windows builds reply list nodes are capped
    # below 256 KB (patch 0006, reply-node-max-bytes), so the threshold must sit below
    # that cap for the reply to hit it; 64 KB is what unit/win-soft-oom uses, and it
    # fails the uncapped 2 MB node of other platforms too. Only that client may be closed.
    expect_eq "DEBUG SET-ALLOC-FAIL-THRESHOLD 65536" OK "$(rcli DEBUG SET-ALLOC-FAIL-THRESHOLD 65536)"
    before=$(oom_count)
    "$CLI" -h 127.0.0.1 -p "$PORT" HMGET smoke:hash data >"$WORK/hmget-oom.out" 2>&1 || true
    ! cmp -s "$WORK/hmget.expected" "$WORK/hmget-oom.out" ||
        die "the 2 MB HMGET reply arrived in full although allocations of 64 KB or more were set to fail"
    expect_eq "PING from another client after the reply-path OOM" PONG "$(rcli PING)"
    after=$(oom_count)
    [ "$after" -gt "$before" ] || die "client_oom_disconnections did not increase ($before -> $after) after a reply-path OOM"
    log "ok: reply-path OOM closed only that client (client_oom_disconnections $before -> $after)"
    expect_eq "DEBUG SET-ALLOC-FAIL-THRESHOLD 1048576" OK "$(rcli DEBUG SET-ALLOC-FAIL-THRESHOLD 1048576)"
    # Query-buffer path: a 2 MB HSET must be refused without executing.
    before=$(oom_count)
    out=$("$CLI" -h 127.0.0.1 -p "$PORT" -x HSET smoke:oom data <"$WORK/blob" 2>&1 | tr -d '\r' || true)
    [ "$out" != "1" ] || die "a 2 MB HSET succeeded although allocations over 1 MB were set to fail"
    log "ok: 2 MB HSET refused under fault injection: $(printf '%s' "$out" | cut -c1-100)"
    expect_eq "PING from another client after the query-buffer OOM" PONG "$(rcli PING)"
    expect_eq "DEBUG SET-ALLOC-FAIL-THRESHOLD 0" OK "$(rcli DEBUG SET-ALLOC-FAIL-THRESHOLD 0)"
    expect_eq "refused HSET did not execute" 0 "$(rcli EXISTS smoke:oom)"
    log "client_oom_disconnections after the query-buffer OOM: $before -> $(oom_count)"
    expect_eq "2 MB HSET with fault injection off" 1 "$(rcli -x HSET smoke:oom data <"$WORK/blob")"
    rcli DEL smoke:oom >/dev/null
    expect_eq "restore oom-soft-client-buffers" OK "$(rcli CONFIG SET oom-soft-client-buffers "$soft_oom_cfg")"
fi
[ -n "$features" ] || log "skip: fork feature checks (none expected)"

step "BGSAVE + DEBUG RELOAD with DEBUG DIGEST"
expect_eq "DEBUG POPULATE" OK "$(rcli DEBUG POPULATE 20000 smoke:pop 64)"
rcli RPUSH smoke:list a b c >/dev/null
rcli SADD smoke:set x y z >/dev/null
rcli ZADD smoke:zset 1 one 2 two >/dev/null
rcli XADD smoke:stream '*' f v >/dev/null
digest=$(rcli DEBUG DIGEST)
case "$digest" in ''|0000000000000000000000000000000000000000) die "unexpected DEBUG DIGEST '$digest'" ;; esac
saves=$(info_field persistence rdb_saves)
expect_eq "BGSAVE" "Background saving started" "$(rcli BGSAVE)"
wait_bgsave "$saves"
log "BGSAVE took $(info_field persistence rdb_last_bgsave_time_sec) s, fork $(info_field stats latest_fork_usec) us"
expect_eq "DEBUG RELOAD NOSAVE (loads the BGSAVE file)" OK "$(rcli DEBUG RELOAD NOSAVE)"
expect_eq "DEBUG DIGEST after loading the BGSAVE file" "$digest" "$(rcli DEBUG DIGEST)"
expect_eq "DEBUG RELOAD" OK "$(rcli DEBUG RELOAD)"
expect_eq "DEBUG DIGEST after DEBUG RELOAD" "$digest" "$(rcli DEBUG DIGEST)"

step "SHUTDOWN SAVE, restart, reload from disk"
stop_server SAVE
start_server
expect_eq "DEBUG DIGEST after restart" "$digest" "$(rcli DEBUG DIGEST)"
expect_eq "HSTRLEN after restart" "$BLOB_BYTES" "$(rcli HSTRLEN smoke:hash data)"
stop_server NOSAVE

log "PASS"
