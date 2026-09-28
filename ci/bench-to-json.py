#!/usr/bin/env python3
"""Turn ci/bench.sh CSV output into the benchmark data the project site reads.

Subcommands (python3 standard library only):

  convert  [metadata flags] -o dataset.json results.csv [more.csv ...]
      Reads one or more CSV files written by ci/bench.sh (columns
      label,rep,test,params,metric,value) and writes ONE dataset object.

  merge    -o bench.json dataset.json [more.json ...]
      Combines dataset files into the site's bench.json: datasets sorted newest
      first (windows-ci before proxy on the same day), plus a top-level "metrics"
      map describing every metric the datasets use. Unreadable or malformed
      inputs are reported on stderr and skipped, so one bad file never blocks
      the rest of the site.

  summary  bench.json|dataset.json
      Prints a markdown table of medians and fork/baseline ratios.

Metadata flags for convert (each overrides the same key from --meta):
  --kind windows-ci|proxy   --toolchain msys2|cygwin|host   --title   --platform
  --redis-version   --commit   --run-url   --date   --id   --note (repeatable)
  --arm LABEL=NAME (repeatable)   --optimization -O2
  --meta FILE     JSON object with any of: kind, toolchain, title, platform,
                  redis_version, commit (or head_sha), run_url, date (or
                  created_at), id, run_id, runner, optimization, notes
  --job-log FILE  a CI job log to read the Redis version and optimization level
                  from (the "INPUT_VERSION:" style lines GitHub prints)
  --default-redis-version-from FILE
                  a workflow file whose PATCH_BASE_VERSION is used when nothing
                  else records the version

Two details of the output beyond the site's schema-1 data contract:
  * each workload also carries "command", the raw ci/bench.sh test name (set,
    get, hset, hmget, hset-large, bgsave); the site reads it when present;
  * "values" holds one entry per CSV row, which is one per repetition except for
    the BGSAVE metrics fork_ms, bgsave_wall_ms and child_cpu_s: bench.sh records
    those once per save (BENCH_BGSAVE_REPS saves per repetition), so their
    median is over every save.
"""

import argparse
import csv
import datetime as _dt
import io
import json
import re
import statistics
import sys

SCHEMA = 1

# Every metric ci/bench.sh emits. Units and direction are fixed facts of the
# script; the titles are what the site shows.
METRICS = {
    "rps": {"unit": "req/s", "better": "higher", "title": "Throughput"},
    "avg_ms": {"unit": "ms", "better": "lower", "title": "Average latency"},
    "p50_ms": {"unit": "ms", "better": "lower", "title": "Median latency (p50)"},
    "p99_ms": {"unit": "ms", "better": "lower", "title": "Tail latency (p99)"},
    "max_ms": {"unit": "ms", "better": "lower", "title": "Worst-case latency"},
    "cpu_user_s": {"unit": "s", "better": "lower", "title": "Server CPU, user"},
    "cpu_sys_s": {"unit": "s", "better": "lower", "title": "Server CPU, system"},
    "cpu_total_s": {"unit": "s", "better": "lower", "title": "Server CPU, user + system"},
    "cpu_us_per_op": {"unit": "µs", "better": "lower", "title": "Server CPU per request"},
    "used_memory_mb": {"unit": "MB", "better": "lower", "title": "Memory used by the dataset"},
    "fork_ms": {"unit": "ms", "better": "lower", "title": "Fork time (main-thread stall)"},
    "bgsave_wall_ms": {"unit": "ms", "better": "lower", "title": "BGSAVE wall time"},
    "child_cpu_s": {"unit": "s", "better": "lower", "title": "BGSAVE child CPU"},
}

ARM_NAMES = {
    "baseline": "Stock build: upstream flags (-O0), no patches",
    "baseline-o2": "Stock source at -O2, no patches",
    "fork": "This fork: -O2 + patch series",
}
ARM_ORDER = ["baseline", "baseline-o2", "fork"]

KINDS = ("windows-ci", "proxy")
KIND_RANK = {"windows-ci": 0, "proxy": 1}
TOOLCHAINS = ("msys2", "cygwin", "host")
TOOLCHAIN_TITLES = {"msys2": "MSYS2", "cygwin": "Cygwin", "host": "host"}

GROUPS = {"small": "Small values", "large": "Large values", "hmget": "Large values",
          "hset": "Large values", "bgsave": "Persistence"}
GROUP_ORDER = ["Small values", "Large values", "Persistence"]
TEST_ORDER = ["small", "large", "hmget", "hset", "bgsave"]
COMMAND_ORDER = ["set", "get", "hset", "hmget", "hset-large", "bgsave"]


def warn(msg):
    print("bench-to-json: warning: %s" % msg, file=sys.stderr)


# ----------------------------------------------------------------- workloads

def parse_params(params):
    """'c=50 P=16 d=3' -> {'c': '50', 'P': '16', 'd': '3'}; other tokens are ignored."""
    out = {}
    for tok in params.split():
        if "=" in tok:
            k, v = tok.split("=", 1)
            out[k] = v
    return out


def _as_int(text):
    try:
        return int(text)
    except (TypeError, ValueError):
        return None


def fmt_bytes(n):
    """Decimal units, as the project documents sizes (1900000 -> '1.9 MB')."""
    if n < 1000:
        return "%d B" % n
    for div, unit in ((1e9, "GB"), (1e6, "MB"), (1e3, "KB")):
        if n >= div:
            v = n / div
            text = ("%.1f" % v) if v < 100 else ("%.0f" % v)
            if text.endswith(".0"):
                text = text[:-2]
            return "%s %s" % (text, unit)
    return "%d B" % n


def fmt_clients(c):
    n = _as_int(c)
    if n is None:
        return "%s clients" % c
    return "1 client" if n == 1 else "%d clients" % n


def slug(text):
    s = re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")
    return s or "x"


def describe_workload(test, params):
    """Deterministic id, test family, title and group for one CSV test+params pair."""
    p = parse_params(params)
    cmd = test.strip()
    size = _as_int(p.get("d"))
    clients = p.get("c")
    pipe = _as_int(p.get("P"))

    if cmd == "bgsave":
        family = "bgsave"
        pop = p.get("populate", params.strip())
        m = re.match(r"^(\d+(?:\.\d+)?)\s*MB$", pop)
        title = "BGSAVE · %s MB dataset" % m.group(1) if m else "BGSAVE · %s" % pop
        wid = "bgsave-" + slug(pop)
    elif cmd == "hmget":
        family = "hmget"
        title = "HMGET · %s field" % (fmt_bytes(size) if size is not None else p.get("d", "?"))
        wid = "hmget"
    elif cmd == "hset-large":
        family = "hset"
        title = "HSET · %s field" % (fmt_bytes(size) if size is not None else p.get("d", "?"))
        wid = "hset-large"
    elif cmd in ("set", "get", "hset"):
        family = "small" if (size is not None and size < 1024) else "large"
        title = "%s · %s" % (cmd.upper(), fmt_bytes(size) if size is not None else p.get("d", "?"))
        wid = cmd
    else:
        family = cmd
        title = cmd.upper()
        wid = slug(cmd)

    if cmd != "bgsave":
        if clients is not None:
            title += " · " + fmt_clients(clients)
            if pipe is not None and pipe > 1:
                title += " × %d pipelined" % pipe
        for key in ("c", "P", "d"):
            if key in p:
                wid += "-%s%s" % (key.lower(), slug(p[key]))
        extra = [t for t in params.split() if t.split("=", 1)[0] not in ("c", "P", "d")]
        if extra:
            wid += "-" + slug(" ".join(extra))
    group = GROUPS.get(family, "Other")
    return {"id": wid, "test": family, "command": cmd, "params": params,
            "title": title, "group": group}


def workload_sort_key(w):
    p = parse_params(w["params"])
    g = GROUP_ORDER.index(w["group"]) if w["group"] in GROUP_ORDER else len(GROUP_ORDER)
    t = TEST_ORDER.index(w["test"]) if w["test"] in TEST_ORDER else len(TEST_ORDER)
    c = COMMAND_ORDER.index(w["command"]) if w["command"] in COMMAND_ORDER else len(COMMAND_ORDER)
    num = [_as_int(p.get(k)) or 0 for k in ("c", "P", "d")]
    pop = re.match(r"^populate=(\d+)", w["params"].strip())
    return (g, t, c, num[0], num[1], num[2], int(pop.group(1)) if pop else 0, w["id"])


# ----------------------------------------------------------------- CSV input

def read_rows(paths):
    """Rows as dicts with label, rep (int or None), test, params, metric, value (float)."""
    rows = []
    for path in paths:
        with open(path, "r", encoding="utf-8-sig", newline="") as fh:
            text = fh.read().replace("\r", "")
        reader = csv.reader(io.StringIO(text))
        for lineno, rec in enumerate(reader, 1):
            if not rec or all(not f.strip() for f in rec):
                continue
            if rec[0].strip() == "label":
                continue
            if len(rec) != 6:
                warn("%s:%d: expected 6 columns, got %d; skipped" % (path, lineno, len(rec)))
                continue
            label, rep, test, params, metric, value = (f.strip() for f in rec)
            try:
                v = float(value)
            except ValueError:
                continue
            if v != v or v in (float("inf"), float("-inf")):
                continue
            if not label or not test or not metric:
                continue
            rows.append({"label": label, "rep": _as_int(rep), "test": test,
                         "params": params, "metric": metric, "value": v})
    return rows


def clean_number(v):
    if isinstance(v, float):
        if v.is_integer() and abs(v) < 1e15:
            return int(v)
        return round(v, 6)
    return v


# ----------------------------------------------------------------- metadata

def utc_now():
    return _dt.datetime.now(_dt.timezone.utc).replace(microsecond=0)


def iso_utc(dt):
    return dt.astimezone(_dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def parse_date(text):
    """ISO-8601 date or date-time -> aware UTC datetime, or None."""
    if not text:
        return None
    s = str(text).strip()
    if re.match(r"^\d{4}-\d{2}-\d{2}$", s):
        return _dt.datetime.strptime(s, "%Y-%m-%d").replace(tzinfo=_dt.timezone.utc)
    s = s.replace("Z", "+00:00")
    try:
        d = _dt.datetime.fromisoformat(s)
    except ValueError:
        return None
    if d.tzinfo is None:
        d = d.replace(tzinfo=_dt.timezone.utc)
    return d.astimezone(_dt.timezone.utc)


def short_sha(sha):
    if not sha:
        return None
    s = str(sha).strip()
    if re.match(r"^[0-9a-fA-F]{8,40}$", s):
        return s[:7].lower()
    return s or None


def read_job_log(path):
    """Redis version and optimization level from a Verify 'prepare' job log.

    GitHub prints each step's evaluated env block, e.g. '  INPUT_VERSION: 8.10.2'.
    Returns a dict with any of redis_version, optimization.
    """
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            text = fh.read()
    except OSError:
        return {}
    found = {}
    for line in text.splitlines():
        m = re.match(r"^(?:\S+Z\s+)?\s*(INPUT_VERSION|INPUT_OPTIMIZATION|PATCH_BASE_VERSION):[ \t]*(\S*)\s*$", line)
        if m and m.group(1) not in found:
            found[m.group(1)] = m.group(2)
    out = {}
    version = found.get("INPUT_VERSION") or found.get("PATCH_BASE_VERSION")
    if version and re.match(r"^[0-9]+\.[0-9]+(\.[0-9]+)?(-[A-Za-z0-9.]+)?$", version):
        out["redis_version"] = version
    opt = found.get("INPUT_OPTIMIZATION")
    if opt and re.match(r"^-O[0-3s]$", opt):
        out["optimization"] = opt
    return out


def version_from_workflow(path):
    try:
        with open(path, "r", encoding="utf-8") as fh:
            for line in fh:
                m = re.match(r'^\s*PATCH_BASE_VERSION:\s*["\']?([0-9][0-9A-Za-z.\-]*)["\']?\s*$', line)
                if m:
                    return m.group(1)
    except OSError:
        pass
    return None


def load_meta(path):
    if not path:
        return {}
    try:
        with open(path, "r", encoding="utf-8") as fh:
            data = json.load(fh)
    except (OSError, ValueError) as exc:
        warn("cannot read metadata %s: %s" % (path, exc))
        return {}
    if not isinstance(data, dict):
        warn("metadata %s is not a JSON object" % path)
        return {}
    return data


# ----------------------------------------------------------------- convert

def build_dataset(rows, meta):
    """One dataset object per the site's data contract, from parsed CSV rows."""
    kind = meta.get("kind") or "proxy"
    toolchain = meta.get("toolchain") or ("host" if kind == "proxy" else "msys2")
    if kind not in KINDS:
        raise ValueError("kind must be one of %s, not %r" % (", ".join(KINDS), kind))
    if toolchain not in TOOLCHAINS:
        raise ValueError("toolchain must be one of %s, not %r" % (", ".join(TOOLCHAINS), toolchain))

    date = parse_date(meta.get("date"))
    if meta.get("date") and date is None:
        raise ValueError("date %r is not ISO-8601" % meta.get("date"))
    date = date or utc_now()
    commit = short_sha(meta.get("commit"))
    optimization = meta.get("optimization") or "-O2"

    notes = [str(n) for n in (meta.get("notes") or []) if str(n).strip()]

    # Arms: every label seen, known ones first in a fixed order.
    labels = []
    for r in rows:
        if r["label"] not in labels:
            labels.append(r["label"])
    labels.sort(key=lambda l: (ARM_ORDER.index(l) if l in ARM_ORDER else len(ARM_ORDER), l))
    arm_names = dict(ARM_NAMES)
    arm_names["fork"] = "This fork: %s + patch series" % optimization
    arm_names.update(meta.get("arm_names") or {})
    arms = [{"label": l, "name": arm_names.get(l, l)} for l in labels]
    for want in ("baseline", "fork"):
        if want not in labels:
            notes.append("No %s rows in this run; fork-vs-baseline ratios are unavailable." % want)

    # Workloads, deterministic and de-duplicated by id.
    workloads = {}
    wid_of = {}
    for r in rows:
        key = (r["test"], r["params"])
        if key in wid_of:
            continue
        w = describe_workload(*key)
        base = w["id"]
        n = 2
        while w["id"] in workloads and (workloads[w["id"]]["command"], workloads[w["id"]]["params"]) != key:
            w["id"] = "%s-%d" % (base, n)
            n += 1
        workloads[w["id"]] = w
        wid_of[key] = w["id"]
    ordered = sorted(workloads.values(), key=workload_sort_key)

    # Results: values in rep order (file order within a rep), median over all.
    buckets = {}
    for i, r in enumerate(rows):
        k = (wid_of[(r["test"], r["params"])], r["metric"], r["label"])
        buckets.setdefault(k, []).append((r["rep"] if r["rep"] is not None else 0, i, r["value"]))
    wpos = {w["id"]: i for i, w in enumerate(ordered)}
    mnames = list(METRICS)
    results = []
    for (wid, metric, label), vals in sorted(
            buckets.items(),
            key=lambda kv: (wpos[kv[0][0]],
                            mnames.index(kv[0][1]) if kv[0][1] in mnames else len(mnames),
                            kv[0][1],
                            ARM_ORDER.index(kv[0][2]) if kv[0][2] in ARM_ORDER else len(ARM_ORDER),
                            kv[0][2])):
        vals.sort(key=lambda t: (t[0], t[1]))
        values = [v for _, _, v in vals]
        results.append({"workload": wid, "metric": metric, "arm": label,
                        "median": clean_number(statistics.median(values)),
                        "values": [clean_number(v) for v in values]})

    reps = len({r["rep"] for r in rows if r["rep"] is not None})

    title = meta.get("title")
    if not title:
        if kind == "windows-ci":
            title = "Windows CI (%s): stock -O0 vs fork %s" % (TOOLCHAIN_TITLES[toolchain], optimization)
        else:
            title = "Proxy (%s): stock -O0 vs fork %s" % (toolchain, optimization)

    ident = meta.get("id")
    if not ident:
        parts = [kind, toolchain]
        if meta.get("run_id"):
            parts.append("run%s" % meta["run_id"])
        else:
            parts.append(date.strftime("%Y-%m-%d"))
            if commit:
                parts.append(commit)
        ident = "-".join(parts)

    if kind == "windows-ci" and not meta.get("notes"):
        notes.insert(0, "Baseline and fork ran on the same runner, interleaved; each figure is the "
                        "median of %d repetition(s). BGSAVE runs several saves per repetition, so "
                        "its figures are the median of every save." % reps)
        notes.insert(1, "Server CPU is user + sys. Differences under about 10% are within runner noise.")
    for item in (meta.get("extra_notes") or []):
        notes.append(str(item))

    return {
        "id": slug(str(ident)),
        "kind": kind,
        "title": str(title),
        "toolchain": toolchain,
        "platform": str(meta.get("platform") or meta.get("runner") or "not recorded"),
        "redis_version": str(meta.get("redis_version") or "unknown"),
        "commit": commit,
        "run_url": meta.get("run_url") or None,
        "date": iso_utc(date),
        "reps": reps,
        "arms": arms,
        "workloads": [{k: w[k] for k in ("id", "test", "command", "params", "title", "group")}
                      for w in ordered],
        "results": results,
        "notes": notes,
    }


def cmd_convert(args):
    meta = load_meta(args.meta)
    if "commit" not in meta and meta.get("head_sha"):
        meta["commit"] = meta["head_sha"]
    if "date" not in meta and meta.get("created_at"):
        meta["date"] = meta["created_at"]
    notes = list(meta.get("notes") or [])
    extra = []
    if args.job_log:
        for k, v in read_job_log(args.job_log).items():
            meta.setdefault(k, v)
    for key in ("kind", "toolchain", "title", "platform", "redis_version", "commit",
                "run_url", "date", "id", "optimization"):
        v = getattr(args, key)
        if v is not None:
            meta[key] = v
    if not meta.get("redis_version") and args.default_redis_version_from:
        v = version_from_workflow(args.default_redis_version_from)
        if v:
            meta["redis_version"] = v
            extra.append("The run did not record its Redis version; %s is the workflow default." % v)
    if meta.get("kind") == "windows-ci" and not meta.get("optimization"):
        extra.append("The run did not record the fork's optimization level; -O2 is the workflow default.")
    notes.extend(args.note or [])
    meta["notes"] = notes
    meta["extra_notes"] = extra
    arm_names = dict(meta.get("arm_names") or {})
    for spec in args.arm or []:
        if "=" not in spec:
            raise SystemExit("bench-to-json: --arm expects LABEL=NAME, got %r" % spec)
        k, v = spec.split("=", 1)
        arm_names[k.strip()] = v.strip()
    meta["arm_names"] = arm_names

    rows = read_rows(args.csv)
    if not rows:
        raise SystemExit("bench-to-json: no benchmark rows in %s" % ", ".join(args.csv))
    try:
        dataset = build_dataset(rows, meta)
    except ValueError as exc:
        raise SystemExit("bench-to-json: %s" % exc)
    write_json(dataset, args.output)
    return 0


# ----------------------------------------------------------------- merge

def validate_dataset(d):
    """Problems that make a dataset unusable by the site; empty when it is fine."""
    problems = []
    if not isinstance(d, dict):
        return ["not a JSON object"]
    for key, typ in (("id", str), ("kind", str), ("title", str), ("toolchain", str),
                     ("platform", str), ("redis_version", str), ("date", str),
                     ("reps", int), ("arms", list), ("workloads", list),
                     ("results", list), ("notes", list)):
        if not isinstance(d.get(key), typ) or isinstance(d.get(key), bool):
            problems.append("%s missing or not %s" % (key, typ.__name__))
    if problems:
        return problems
    if d["kind"] not in KINDS:
        problems.append("kind %r unknown" % d["kind"])
    if d["toolchain"] not in TOOLCHAINS:
        problems.append("toolchain %r unknown" % d["toolchain"])
    if parse_date(d["date"]) is None:
        problems.append("date %r is not ISO-8601" % d["date"])
    for key in ("commit", "run_url"):
        if key not in d or not (d[key] is None or isinstance(d[key], str)):
            problems.append("%s must be a string or null" % key)
    labels = set()
    for a in d["arms"]:
        if not isinstance(a, dict) or not isinstance(a.get("label"), str) or not isinstance(a.get("name"), str):
            problems.append("arm entries need label and name")
            break
        labels.add(a["label"])
    wids = set()
    for w in d["workloads"]:
        if not isinstance(w, dict) or not all(isinstance(w.get(k), str) for k in ("id", "test", "params", "title", "group")):
            problems.append("workload entries need id, test, params, title, group")
            break
        if w["id"] in wids:
            problems.append("duplicate workload id %r" % w["id"])
        wids.add(w["id"])
    for r in d["results"]:
        ok = (isinstance(r, dict) and r.get("workload") in wids and r.get("arm") in labels
              and isinstance(r.get("metric"), str)
              and isinstance(r.get("median"), (int, float)) and not isinstance(r.get("median"), bool)
              and isinstance(r.get("values"), list)
              and all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in r["values"]))
        if not ok:
            problems.append("result %r does not reference a known workload/arm or has non-numeric values" % (r,))
            break
    return problems


def dataset_sort_key(d):
    dt = parse_date(d["date"])
    day = dt.strftime("%Y-%m-%d")
    # Newest day first; on the same day Windows CI first; then newest time, then id.
    return (tuple(-ord(c) for c in day), KIND_RANK.get(d["kind"], 9), -dt.timestamp(), d["id"])


def merge_datasets(datasets, generated_at=None):
    seen = {}
    for d in datasets:
        if d["id"] in seen:
            warn("duplicate dataset id %r; keeping the first" % d["id"])
            continue
        seen[d["id"]] = d
    ordered = sorted(seen.values(), key=dataset_sort_key)
    used = []
    for d in ordered:
        for r in d["results"]:
            if r["metric"] not in used:
                used.append(r["metric"])
    names = list(METRICS)
    used.sort(key=lambda m: (names.index(m) if m in names else len(names), m))
    metrics = {}
    for m in used:
        metrics[m] = dict(METRICS.get(m) or guess_metric(m))
    return {
        "schema": SCHEMA,
        "generated_at": generated_at or iso_utc(utc_now()),
        "metrics": metrics,
        "datasets": ordered,
    }


def guess_metric(name):
    if name.endswith("_ms"):
        unit = "ms"
    elif name.endswith("_us"):
        unit = "µs"
    elif name.endswith("_s"):
        unit = "s"
    elif name.endswith("_mb"):
        unit = "MB"
    else:
        unit = ""
    better = "higher" if name in ("rps", "ops") or name.endswith("_rps") else "lower"
    return {"unit": unit, "better": better, "title": name.replace("_", " ")}


def load_datasets(paths):
    out = []
    for path in paths:
        try:
            with open(path, "r", encoding="utf-8") as fh:
                data = json.load(fh)
        except (OSError, ValueError) as exc:
            warn("skipping %s: %s" % (path, exc))
            continue
        items = data.get("datasets") if isinstance(data, dict) and "datasets" in data else [data]
        for d in items if isinstance(items, list) else []:
            problems = validate_dataset(d)
            if problems:
                warn("skipping a dataset in %s: %s" % (path, "; ".join(problems[:3])))
                continue
            out.append(d)
    return out


def generated_at_default():
    import os
    epoch = os.environ.get("SOURCE_DATE_EPOCH")
    if epoch and epoch.isdigit():
        return iso_utc(_dt.datetime.fromtimestamp(int(epoch), _dt.timezone.utc))
    return None


def cmd_merge(args):
    datasets = load_datasets(args.inputs)
    bench = merge_datasets(datasets, args.generated_at or generated_at_default())
    write_json(bench, args.output)
    print("bench-to-json: %d dataset(s) merged into %s" % (len(bench["datasets"]), args.output or "stdout"),
          file=sys.stderr)
    return 0


# ----------------------------------------------------------------- summary

def ratio(fork, base, better):
    """Improvement factor oriented so that > 1 always means the fork is better."""
    if base is None or fork is None or base <= 0 or fork <= 0:
        return None
    return fork / base if better == "higher" else base / fork


def fmt_num(v):
    if v is None:
        return "n/a"
    a = abs(v)
    if a >= 1000:
        return "{:,.0f}".format(v)
    if a >= 10:
        return "%.1f" % v
    return "%.3f" % v


def summarize(bench, metrics=None):
    lines = []
    mdefs = bench.get("metrics") or METRICS
    for d in bench["datasets"]:
        lines.append("### %s" % d["title"])
        lines.append("")
        lines.append("%s · %s · Redis %s · %d repetition(s) · %s" % (
            d["kind"], d["platform"], d["redis_version"], d["reps"], d["date"]))
        lines.append("")
        lines.append("| Workload | Metric | baseline | fork | fork vs baseline |")
        lines.append("|---|---|---:|---:|---:|")
        med = {(r["workload"], r["metric"], r["arm"]): r["median"] for r in d["results"]}
        for w in d["workloads"]:
            for m in (metrics or list(mdefs)):
                b = med.get((w["id"], m, "baseline"))
                f = med.get((w["id"], m, "fork"))
                if b is None and f is None:
                    continue
                md = mdefs.get(m) or guess_metric(m)
                r = ratio(f, b, md["better"])
                if r is None:
                    rtxt = "n/a"
                elif "%.2f" % r == "1.00":
                    rtxt = "1.00× (same)"
                else:
                    rtxt = "%.2f× %s" % (r, "better" if r > 1 else "worse")
                lines.append("| %s | %s (%s) | %s | %s | %s |" % (
                    w["title"], m, md["unit"], fmt_num(b), fmt_num(f), rtxt))
        lines.append("")
    return "\n".join(lines)


def cmd_summary(args):
    with open(args.input, "r", encoding="utf-8") as fh:
        data = json.load(fh)
    if "datasets" not in data:
        data = merge_datasets([data])
    metrics = args.metric or None
    print(summarize(data, metrics))
    return 0


# ----------------------------------------------------------------- main

def write_json(obj, path):
    text = json.dumps(obj, indent=2, ensure_ascii=False) + "\n"
    if path and path != "-":
        with open(path, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
    else:
        sys.stdout.write(text)


def main(argv=None):
    ap = argparse.ArgumentParser(description="Convert ci/bench.sh CSV into site benchmark data.")
    sub = ap.add_subparsers(dest="cmd")

    c = sub.add_parser("convert", help="CSV file(s) -> one dataset JSON")
    c.add_argument("csv", nargs="+")
    c.add_argument("-o", "--output")
    c.add_argument("--meta")
    c.add_argument("--job-log")
    c.add_argument("--default-redis-version-from")
    c.add_argument("--kind", choices=KINDS)
    c.add_argument("--toolchain", choices=TOOLCHAINS)
    c.add_argument("--title")
    c.add_argument("--platform")
    c.add_argument("--redis-version")
    c.add_argument("--commit")
    c.add_argument("--run-url")
    c.add_argument("--date")
    c.add_argument("--id")
    c.add_argument("--optimization")
    c.add_argument("--note", action="append")
    c.add_argument("--arm", action="append", help="LABEL=NAME")

    m = sub.add_parser("merge", help="dataset JSON files -> bench.json")
    m.add_argument("inputs", nargs="*")
    m.add_argument("-o", "--output")
    m.add_argument("--generated-at")

    s = sub.add_parser("summary", help="markdown table of medians and ratios")
    s.add_argument("input")
    s.add_argument("--metric", action="append")

    args = ap.parse_args(argv)
    if args.cmd == "convert":
        return cmd_convert(args)
    if args.cmd == "merge":
        return cmd_merge(args)
    if args.cmd == "summary":
        return cmd_summary(args)
    ap.print_help(sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
