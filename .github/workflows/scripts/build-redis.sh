#!/usr/bin/env bash
# Build and package Redis for Windows inside the MSYS2 or Cygwin bash shell.
# Used by build-redis.yml (releases) and verify.yml (baseline vs fork comparison).
#
# Configuration comes from the environment:
#   TOOLCHAIN        msys2 | cygwin | host  ("host" = local macOS/Linux dry run: no DLLs)
#   REDIS_VERSION    e.g. 8.10.2 (redis-server --version must report it)
#   REDIS_TARBALL    path to the upstream source tarball
#   DIST_NAME        package base name, e.g. Redis-8.10.2-fork.1-Windows-x64-msys2
#   OPTIMIZATION     passed as make OPTIMIZATION=... (e.g. -O2). Empty = do not pass it
#                    (upstream default -O3 + LTO; used only by the baseline build).
#   MAKE_CFLAGS      make CFLAGS=... (default -Wno-char-subscripts). Must not carry an
#                    -O level while OPTIMIZATION is set: CFLAGS comes last and would win.
#   BUILD_TLS        yes | no (default yes)
#   APPLY_PATCHES    yes | no (default yes): scripts/apply-patches.sh on the extracted tree
#   DLFCN_COMPAT     yes | no (default no): unpatched sources only. Compiles against a
#                    private copy of dlfcn.h with Dl_info/dladdr visible, instead of
#                    editing the toolchain's header in place (what upstream does).
#   FORK_LABEL       label for the start-up banner (default fork)
#   SERVICE_DIR      optional: directory with the published RedisService.exe; adds a
#                    <DIST_NAME>-with-Service package
#   WORK_DIR         build directory (default ./build)
#   OUT_DIR          where packages, build-info and hashes go (default .)
#   JOBS             make -j value (default: number of CPUs)
#
# Produces in OUT_DIR:
#   <DIST_NAME>/ and <DIST_NAME>.zip                stripped binaries (the shipped package)
#   <DIST_NAME>-with-Service/ and .zip              when SERVICE_DIR is set
#   <DIST_NAME>-debug.zip                           unstripped binaries for post-mortems
#   <DIST_NAME>-build-info.txt, hashes-<DIST_NAME>.txt
# and leaves the built source tree in WORK_DIR/redis-<version> (for the Tcl test suite).

set -Eeuo pipefail
trap 'if [ "$BASH_SUBSHELL" = 0 ]; then echo "::error::build-redis.sh failed at line $LINENO" >&2; fi' ERR

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/../../.." && pwd)

: "${TOOLCHAIN:?TOOLCHAIN is required (msys2|cygwin|host)}"
: "${REDIS_VERSION:?REDIS_VERSION is required}"
: "${REDIS_TARBALL:?REDIS_TARBALL is required}"
: "${DIST_NAME:?DIST_NAME is required}"
OPTIMIZATION=${OPTIMIZATION-}
MAKE_CFLAGS=${MAKE_CFLAGS:--Wno-char-subscripts}
BUILD_TLS=${BUILD_TLS:-yes}
APPLY_PATCHES=${APPLY_PATCHES:-yes}
DLFCN_COMPAT=${DLFCN_COMPAT:-no}
FORK_LABEL=${FORK_LABEL:-fork}
SERVICE_DIR=${SERVICE_DIR-}
WORK_DIR=${WORK_DIR:-$PWD/build}
OUT_DIR=${OUT_DIR:-$PWD}

log()  { printf '==> %s\n' "$*"; }
fail() { printf '::error::%s\n' "$*" >&2; exit 1; }
summary() { if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then printf '%s\n' "$*" >>"$GITHUB_STEP_SUMMARY"; fi; }

case "$TOOLCHAIN" in msys2|cygwin|host) ;; *) fail "TOOLCHAIN must be msys2, cygwin or host" ;; esac
case "$BUILD_TLS" in yes|no) ;; *) fail "BUILD_TLS must be yes or no" ;; esac
case "$FORK_LABEL" in *[!A-Za-z0-9._-]*|'') fail "FORK_LABEL may only contain A-Z a-z 0-9 . _ -" ;; esac
if [ -n "$OPTIMIZATION" ]; then
    case "$OPTIMIZATION" in -O0|-O1|-O2|-O3|-Os|-Og) ;; *) fail "OPTIMIZATION must be one of -O0 -O1 -O2 -O3 -Os -Og" ;; esac
    case " $MAKE_CFLAGS " in *" -O"*) fail "MAKE_CFLAGS carries an -O level; pass it through OPTIMIZATION instead" ;; esac
fi

mkdir -p "$WORK_DIR" "$OUT_DIR"
WORK_DIR=$(cd "$WORK_DIR" && pwd)
OUT_DIR=$(cd "$OUT_DIR" && pwd)
REDIS_TARBALL=$(cd "$(dirname "$REDIS_TARBALL")" && pwd)/$(basename "$REDIS_TARBALL")
[ -f "$REDIS_TARBALL" ] || fail "REDIS_TARBALL not found: $REDIS_TARBALL"
if [ -n "$SERVICE_DIR" ]; then
    [ -f "$SERVICE_DIR/RedisService.exe" ] || fail "SERVICE_DIR has no RedisService.exe: $SERVICE_DIR"
    SERVICE_DIR=$(cd "$SERVICE_DIR" && pwd)
fi

EXE=""
[ "$TOOLCHAIN" = host ] || EXE=".exe"
JOBS=${JOBS:-$(nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 2)}
sha256() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

INFO="$OUT_DIR/$DIST_NAME-build-info.txt"
: >"$INFO"
info() { printf '%s\n' "$*" >>"$INFO"; }
info_cmd() { # <title> <command...>: record a command and its output
    { printf '\n## %s\n$ %s\n' "$1" "${*:2}"; "${@:2}" 2>&1 || echo "(exit code $?)"; } >>"$INFO"
}

# ---------------------------------------------------------------- toolchain facts
log "Toolchain facts"
info "# Build information for $DIST_NAME"
info ""
info "redis_version:  $REDIS_VERSION"
info "fork_label:     $FORK_LABEL"
info "toolchain:      $TOOLCHAIN"
info "optimization:   ${OPTIMIZATION:-(not passed: upstream default -O3 + LTO)}"
info "make_cflags:    $MAKE_CFLAGS"
info "build_tls:      $BUILD_TLS"
info "patches:        $APPLY_PATCHES"
info "repo_sha:       ${GITHUB_SHA:-$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || echo unknown)}"
if [ -n "${GITHUB_RUN_ID:-}" ]; then
    info "workflow_run:   ${GITHUB_SERVER_URL:-https://github.com}/${GITHUB_REPOSITORY:-}/actions/runs/$GITHUB_RUN_ID"
fi
info "source_tarball: $(basename "$REDIS_TARBALL") sha256=$(sha256 "$REDIS_TARBALL")"
info "built_at_utc:   $(date -u '+%Y-%m-%dT%H:%M:%SZ')"
info_cmd "uname" uname -a
info_cmd "gcc -v" gcc -v
case "$TOOLCHAIN" in
    msys2)
        info_cmd "packages" pacman -Q gcc binutils make msys2-runtime openssl
        runtime=$(pacman -Q msys2-runtime | awk '{print $2}')
        case "$runtime" in
            3.6.*) log "msys2-runtime $runtime (matches the 3.6.x analysis baseline)" ;;
            *)
                msg="msys2-runtime is $runtime, not 3.6.x. The fork, select() and malloc analysis behind this build was done against msys2-runtime 3.6.10; re-check those findings before shipping."
                echo "::warning title=MSYS2 runtime is not 3.6.x::$msg"
                summary "> [!WARNING]"
                summary "> $msg"
                info "WARNING: $msg"
                ;;
        esac
        ;;
    cygwin)
        info_cmd "packages" cygcheck -c gcc-core binutils make cygwin libssl-devel
        runtime=$(uname -r)
        case "$runtime" in
            3.6.*) ;;
            *) echo "::notice title=Cygwin runtime::Cygwin runtime is $runtime; the analysis baseline is the 3.6.x runtime family." ;;
        esac
        ;;
esac

# ---------------------------------------------------------------- source
log "Extracting $(basename "$REDIS_TARBALL")"
top=$(tar -tzf "$REDIS_TARBALL" | sed -n 1p)
top=${top%%/*}
rm -rf "${WORK_DIR:?}/$top"
tar -xzf "$REDIS_TARBALL" -C "$WORK_DIR"
SRC="$WORK_DIR/$top"
[ -f "$SRC/src/server.c" ] || fail "unexpected tarball layout: $SRC/src/server.c not found"

if [ "$APPLY_PATCHES" = yes ]; then
    log "Applying the patch series"
    [ -f "$REPO_ROOT/scripts/apply-patches.sh" ] || fail "scripts/apply-patches.sh not found"
    if ! bash "$REPO_ROOT/scripts/apply-patches.sh" "$SRC" 2>&1 | tee "$OUT_DIR/$DIST_NAME-patches.log"; then
        fail "Patch series failed to apply to Redis $REDIS_VERSION - see the log above for the rejected patch"
    fi
    info ""
    info "## Applied patches (lexical order)"
    found=0
    for p in "$REPO_ROOT"/patches/redis/*.patch; do
        [ -f "$p" ] || continue
        found=1
        info "$(basename "$p")  sha256=$(sha256 "$p")"
    done
    [ "$found" = 1 ] || info "(none: patches/redis/ holds no .patch files)"
else
    info ""
    info "## Applied patches"
    info "(none: unpatched upstream source)"
fi

cd "$SRC/src"
./mkreleasehdr.sh >/dev/null

# Replace the last two lines of the start-up logo with a line naming this build.
[ "$(grep -c '";' asciilogo.h)" = 1 ] || fail 'asciilogo.h no longer has exactly one line ending in ";'
sed -i.bak 's/";$/"/' asciilogo.h && rm -f asciilogo.h.bak
printf '"   Redis for Windows, %s build (https://github.com/redis-windows/redis-windows)\\n\\n";\n' "$FORK_LABEL" >>asciilogo.h
# Test modules need a Linux-style link step; do not build them.
sed -i.bak 's/ module_tests//' Makefile && rm -f Makefile.bak
if grep -q '^all:.* module_tests' Makefile; then fail "could not drop module_tests from src/Makefile"; fi
cd "$SRC"

cflags="$MAKE_CFLAGS"
if [ "$DLFCN_COMPAT" = yes ]; then
    compat="$WORK_DIR/compat-include"
    mkdir -p "$compat"
    sed 's/__GNU_VISIBLE/1/' /usr/include/dlfcn.h >"$compat/dlfcn.h"
    cflags="$cflags -I$compat"
    info "dlfcn_compat:   private dlfcn.h copy in $compat (toolchain header untouched)"
fi

# ---------------------------------------------------------------- build
make_args=(BUILD_TLS="$BUILD_TLS" CFLAGS="$cflags" -j"$JOBS")
[ -n "$OPTIMIZATION" ] && make_args=(OPTIMIZATION="$OPTIMIZATION" "${make_args[@]}")
info "make_command:   make ${make_args[*]}"
log "make ${make_args[*]}"
if ! make "${make_args[@]}" 2>&1 | tee "$OUT_DIR/$DIST_NAME-make.log"; then
    fail "make failed - see the output above ($DIST_NAME-make.log)"
fi

BINARIES=(redis-server redis-cli redis-benchmark redis-check-rdb redis-check-aof redis-sentinel)
for b in "${BINARIES[@]}"; do
    [ -f "src/$b$EXE" ] || fail "make did not produce src/$b$EXE"
done

version_out=$("src/redis-server$EXE" --version | tr -d '\r')
log "$version_out"
case "$version_out" in
    *"v=$REDIS_VERSION "*) ;;
    *) fail "version check failed: expected v=$REDIS_VERSION, got: $version_out" ;;
esac
info "version:        $version_out"

# ---------------------------------------------------------------- codegen facts
# DWARF producer strings record the options each unit was compiled with. GCC leaves every
# -W option out of them (opts.cc gen_command_line_string), so Redis's own units are found
# by their DW_AT_comp_dir, which lies inside the source tree; the toolchain runtime units
# linked in (crt0.c, premain...) sit elsewhere. The -O0 defect this build exists to fix
# shows up as Redis units whose last -O level is -O0; GCC's default is -O0, so a unit with
# no -O level counts as -O0 too. An LTO build shows up as "GNU GIMPLE ... -fltrans" units.
info ""
info "## Code generation"
last_o() { { grep -o -E -e '-O[0-3sg]( |$)' || true; } | tr -d ' ' | sed -n '$p'; }
# Prints "<comp_dir><TAB><producer>" for every compile unit, parsed from GNU objdump.
dwarf_units() {
    { objdump --dwarf=info --dwarf-depth=1 "$1" 2>/dev/null || true; } | tr -d '\r' | awk '
        function val(s) {
            sub(/^[^:]*DW_AT_[a-z_]+[ \t]*:[ \t]*/, "", s)
            sub(/^\((indirect|indexed)[^)]*\): /, "", s)
            return s
        }
        /DW_TAG_(compile|partial)_unit/ { if (n) print dir "\t" prod; n = 1; dir = ""; prod = ""; next }
        /DW_AT_producer/ { prod = val($0) }
        /DW_AT_comp_dir/ { dir = val($0) }
        END { if (n) print dir "\t" prod }'
}
src_root=$(cd "$SRC" && pwd -P)
all_units=$(dwarf_units "src/redis-server$EXE")
units=$(printf '%s\n' "$all_units" | awk -F'\t' -v root="$src_root" \
    '$2 ~ /^GNU / && ($1 == root || index($1, root "/") == 1)' || true)
if [ -n "$units" ]; then
    producers=$(printf '%s\n' "$units" | cut -f2-)
    n_ours=$(printf '%s\n' "$producers" | wc -l | tr -d ' ')
    n_all=$(printf '%s\n' "$all_units" | awk -F'\t' '$2 ~ /^GNU /' | wc -l | tr -d ' ')
    info "Compile units under $src_root: $n_ours (plus $((n_all - n_ours)) toolchain runtime units, not checked)"
else
    # objdump output not understood, or comp_dir recorded in another path form: check
    # every producer string. The runtime units are built at -O2 without LTO, so they cannot
    # cause a false failure.
    # -o without ^: strings(1) can glue a preceding printable byte onto the producer.
    producers=$(strings -a "src/redis-server$EXE" | grep -o -E 'GNU (C[0-9x+]*|GIMPLE) [0-9]+\.[0-9.]+( .*)?$' || true)
    if [ -n "$producers" ]; then
        echo "::notice title=Codegen check::no compile unit matched $src_root in objdump --dwarf=info; checking every DWARF producer string instead"
        info "Compile units (all DWARF producer strings; comp_dir matching found none): $(printf '%s\n' "$producers" | wc -l | tr -d ' ')"
    fi
fi
if [ -n "$producers" ]; then
    c_units=$(printf '%s\n' "$producers" | grep -E '^GNU C' || true)
    info "C units by effective (last) -O level (none = GCC default -O0):"
    printf '%s\n' "$c_units" | while IFS= read -r line; do
        [ -n "$line" ] || continue
        lvl=$(printf '%s\n' "$line" | last_o)
        echo "${lvl:--O0 (none)}"
    done | sort | uniq -c | sed 's/^/  /' >>"$INFO"
    lto=$(printf '%s\n' "$producers" | grep -c -E -e '-flto|-fltrans|^GNU GIMPLE' || true)
    info "Units compiled with or produced by LTO: $lto"
    info "Example producer: $(printf '%s\n' "$producers" | sed -n 1p)"
    if [ -n "$OPTIMIZATION" ] && [ "$OPTIMIZATION" != -O0 ]; then
        o0=$(printf '%s\n' "$c_units" | while IFS= read -r line; do
            [ -n "$line" ] || continue
            lvl=$(printf '%s\n' "$line" | last_o)
            echo "${lvl:--O0}"
        done | grep -c -x -e '-O0' || true)
        [ "$o0" = 0 ] || fail "$o0 Redis compile units are still built at -O0 (last -O level, or none) although OPTIMIZATION=$OPTIMIZATION"
        if [ "$OPTIMIZATION" != -O3 ] && [ "$lto" != 0 ]; then
            fail "$lto Redis compile units were built with LTO although OPTIMIZATION=$OPTIMIZATION"
        fi
        log "codegen check passed: nothing compiled at -O0 or with LTO"
    fi
elif [ "$TOOLCHAIN" = host ]; then
    info "(no DWARF producer strings in the host binary; skipped)"
else
    fail "no DWARF producer strings found in redis-server$EXE"
fi
info "Out-of-line sdslen copies (nm): $(nm "src/redis-server$EXE" 2>/dev/null | grep -c -E ' [tT] _?sdslen' || true)"

# Prints the /usr/bin DLLs that the given PE files import, transitively.
pe_dll_closure() {
    local dir=${DLL_DIR:-/usr/bin} queue=() seen=" " f name
    for f in "$@"; do queue+=("$f$EXE"); done
    while [ ${#queue[@]} -gt 0 ]; do
        f=${queue[0]}
        queue=("${queue[@]:1}")
        for name in $(objdump -p "$f" | awk '/DLL Name:/ {print $3}' | tr -d '\r'); do
            [ -f "$dir/$name" ] || continue
            case "$seen" in *" $name "*) continue ;; esac
            seen="$seen$name "
            echo "$dir/$name"
            queue+=("$dir/$name")
        done
    done
}

# ---------------------------------------------------------------- package
log "Packaging $DIST_NAME"
DIST="$OUT_DIR/$DIST_NAME"
DEBUG_DIST="$OUT_DIR/$DIST_NAME-debug"
rm -rf "$DIST" "$DEBUG_DIST" "$DIST-with-Service"
mkdir -p "$DIST" "$DEBUG_DIST"

# redis-check-rdb, redis-check-aof and redis-sentinel are copies of redis-server: Redis
# picks its mode from argv[0] (server.c), so all three names must ship.
for b in "${BINARIES[@]}"; do cp "src/$b$EXE" "$DIST/"; done
cp redis.conf sentinel.conf "$DIST/"
sed -i.bak 's|^pidfile /var/run|pidfile .|' "$DIST/redis.conf" && rm -f "$DIST/redis.conf.bak"
cp LICENSE.txt "$DIST/LICENSE-redis.txt"
for f in README.md README.zh_CN.md LICENSE start.bat; do
    if [ -f "$REPO_ROOT/$f" ]; then cp "$REPO_ROOT/$f" "$DIST/"; fi
done
for f in "$REPO_ROOT"/conf/*.conf; do
    if [ -f "$f" ]; then cp "$f" "$DIST/"; fi
done

if [ "$TOOLCHAIN" != host ]; then
    # Ship exactly the Cygwin-side DLLs the binaries import, directly or through other
    # DLLs (no TLS => no ssl/crypto). Read statically from the PE import tables; Windows
    # system DLLs are not in /usr/bin and are skipped.
    dlls=$(pe_dll_closure "${BINARIES[@]/#/src/}")
    [ -n "$dlls" ] || fail "no runtime DLLs found in the import tables"
    for d in $dlls; do cp "$d" "$DIST/"; done
    runtime_dll=msys-2.0.dll
    [ "$TOOLCHAIN" = cygwin ] && runtime_dll=cygwin1.dll
    [ -f "$DIST/$runtime_dll" ] || fail "$runtime_dll is missing from the package"
    if [ "$BUILD_TLS" = yes ]; then
        ssl=no
        for d in "$DIST"/msys-ssl-*.dll "$DIST"/cygssl-*.dll; do [ -f "$d" ] && ssl=yes; done
        [ "$ssl" = yes ] || fail "BUILD_TLS=yes but no OpenSSL DLL was collected"
    fi
    info ""
    info "## Bundled DLLs"
    for d in $dlls; do info "$(basename "$d")"; done
fi

# Debug package: unstripped copies. The three mode aliases are identical to
# redis-server, so only distinct files are kept.
for b in redis-server redis-cli redis-benchmark redis-check-rdb redis-check-aof redis-sentinel; do
    if [ "$b" != redis-server ] && cmp -s "src/$b$EXE" "src/redis-server$EXE"; then continue; fi
    cp "src/$b$EXE" "$DEBUG_DIST/"
done

log "Stripping debug information"
for b in "${BINARIES[@]}"; do
    before=$(wc -c <"$DIST/$b$EXE" | tr -d ' ')
    if [ "$TOOLCHAIN" = host ]; then strip -S "$DIST/$b$EXE"; else strip --strip-debug "$DIST/$b$EXE"; fi
    after=$(wc -c <"$DIST/$b$EXE" | tr -d ' ')
    [ "$b" = redis-server ] && info "" && info "redis-server size: $before bytes unstripped, $after bytes stripped"
done
# Run a copy: executed next to the bundled runtime DLL from inside the MSYS2/Cygwin shell, it would load a
# second copy of the runtime. build-windows.yml runs the package as shipped, from PowerShell.
strip_check=$(mktemp -d)
cp "$DIST/redis-server$EXE" "$strip_check/"
"$strip_check/redis-server$EXE" --version >/dev/null || fail "stripped redis-server does not run"
rm -rf "$strip_check"

cp "$INFO" "$DIST/build-info.txt"
cp "$INFO" "$DEBUG_DIST/build-info.txt"
cat >"$DEBUG_DIST/README-debug.txt" <<EOF
Unstripped binaries of $DIST_NAME, for post-mortem debugging with gdb.
They are the same builds as in $DIST_NAME.zip before strip --strip-debug, so code
addresses match. redis-check-rdb, redis-check-aof and redis-sentinel are byte-identical
to redis-server and are therefore not repeated here.
EOF

cd "$OUT_DIR"
rm -f "$DIST_NAME.zip" "$DIST_NAME-debug.zip" "$DIST_NAME-with-Service.zip"
zip -q -r "$DIST_NAME.zip" "$DIST_NAME"
zip -q -r "$DIST_NAME-debug.zip" "$DIST_NAME-debug"
zips=("$DIST_NAME.zip")
if [ -n "$SERVICE_DIR" ]; then
    [ -f "$SERVICE_DIR/RedisService.exe" ] || fail "SERVICE_DIR has no RedisService.exe"
    cp -r "$DIST_NAME" "$DIST_NAME-with-Service"
    cp -r "$SERVICE_DIR"/. "$DIST_NAME-with-Service/"
    # Double-click installers; they need RedisService.exe, so only this package has them.
    for f in install-service.bat uninstall-service.bat; do
        [ -f "$REPO_ROOT/$f" ] || fail "missing $f in the repository root"
        cp "$REPO_ROOT/$f" "$DIST_NAME-with-Service/"
    done
    zip -q -r "$DIST_NAME-with-Service.zip" "$DIST_NAME-with-Service"
    zips+=("$DIST_NAME-with-Service.zip")
fi
zips+=("$DIST_NAME-debug.zip")

{
    echo "$DIST_NAME (SHA256)"
    for z in "${zips[@]}"; do echo "$(sha256 "$z")  $z"; done
} >"hashes-$DIST_NAME.txt"
cat "hashes-$DIST_NAME.txt"

summary "### $DIST_NAME"
summary ""
summary "| | |"
summary "|---|---|"
summary "| Version | \`$version_out\` |"
summary "| OPTIMIZATION | \`${OPTIMIZATION:-(upstream default)}\` |"
summary "| CFLAGS | \`$MAKE_CFLAGS\` |"
summary "| TLS | $BUILD_TLS |"
summary "| Patches | $APPLY_PATCHES |"
summary "| Compiler | \`$(gcc --version | sed -n 1p)\` |"
summary ""
log "Done: ${zips[*]}"
