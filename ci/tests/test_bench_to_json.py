"""Tests for ci/bench-to-json.py and scripts/build-site.sh.

Run from the repository root:  python3 -m unittest discover -s ci/tests
The fixture is a trimmed copy of a real ci/bench.sh run (macOS, stock vs fork).
"""

import csv
import importlib.util
import json
import os
import re
import shutil
import statistics
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
SCRIPT = os.path.join(REPO, "ci", "bench-to-json.py")
BUILD_SITE = os.path.join(REPO, "scripts", "build-site.sh")
FIXTURE = os.path.join(HERE, "fixtures", "bench-macos-sample.csv")
DATASETS_DIR = os.path.join(REPO, "site", "data", "datasets")

_spec = importlib.util.spec_from_file_location("bench_to_json", SCRIPT)
b2j = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(b2j)

CSV_HEADER = "label,rep,test,params,metric,value\n"


def fixture_rows():
    with open(FIXTURE, newline="") as fh:
        return [r for r in csv.DictReader(fh)]


def run(args, **kw):
    return subprocess.run([sys.executable, SCRIPT] + args, capture_output=True, text=True, **kw)


class TempDirCase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="b2j-test-")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def write(self, name, text):
        path = os.path.join(self.tmp, name)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", newline="") as fh:
            fh.write(text)
        return path

    def convert(self, csv_paths, meta):
        return b2j.build_dataset(b2j.read_rows(csv_paths), meta)


class WorkloadNaming(unittest.TestCase):
    def test_every_bench_sh_shape(self):
        cases = [
            ("set", "c=1 P=1 d=3", "set-c1-p1-d3", "small", "SET · 3 B · 1 client", "Small values"),
            ("get", "c=50 P=16 d=3", "get-c50-p16-d3", "small",
             "GET · 3 B · 50 clients × 16 pipelined", "Small values"),
            ("hset", "c=50 P=16 d=3", "hset-c50-p16-d3", "small",
             "HSET · 3 B · 50 clients × 16 pipelined", "Small values"),
            ("set", "c=4 P=1 d=1900000", "set-c4-p1-d1900000", "large",
             "SET · 1.9 MB · 4 clients", "Large values"),
            ("get", "c=4 P=1 d=1900000", "get-c4-p1-d1900000", "large",
             "GET · 1.9 MB · 4 clients", "Large values"),
            ("hmget", "c=1 d=1900000", "hmget-c1-d1900000", "hmget",
             "HMGET · 1.9 MB field · 1 client", "Large values"),
            ("hmget", "c=2 d=1900000", "hmget-c2-d1900000", "hmget",
             "HMGET · 1.9 MB field · 2 clients", "Large values"),
            ("hset-large", "c=1 d=1900000", "hset-large-c1-d1900000", "hset",
             "HSET · 1.9 MB field · 1 client", "Large values"),
            ("bgsave", "populate=64MB", "bgsave-64mb", "bgsave", "BGSAVE · 64 MB dataset", "Persistence"),
            ("bgsave", "populate=256MB", "bgsave-256mb", "bgsave", "BGSAVE · 256 MB dataset", "Persistence"),
        ]
        for test, params, wid, family, title, group in cases:
            with self.subTest(test=test, params=params):
                w = b2j.describe_workload(test, params)
                self.assertEqual(w["id"], wid)
                self.assertEqual(w["test"], family)
                self.assertEqual(w["title"], title)
                self.assertEqual(w["group"], group)
                self.assertEqual(w["params"], params)

    def test_deterministic(self):
        self.assertEqual(b2j.describe_workload("get", "c=50 P=16 d=3"),
                         b2j.describe_workload("get", "c=50 P=16 d=3"))

    def test_unknown_test_still_named(self):
        w = b2j.describe_workload("lpush", "c=8")
        self.assertEqual(w["id"], "lpush-c8")
        self.assertEqual(w["group"], "Other")

    def test_fmt_bytes(self):
        self.assertEqual(b2j.fmt_bytes(3), "3 B")
        self.assertEqual(b2j.fmt_bytes(1500), "1.5 KB")
        self.assertEqual(b2j.fmt_bytes(262144), "262 KB")
        self.assertEqual(b2j.fmt_bytes(1900000), "1.9 MB")
        self.assertEqual(b2j.fmt_bytes(2000000), "2 MB")


class MetricsCoverBenchScript(unittest.TestCase):
    def test_every_emitted_metric_is_described(self):
        with open(os.path.join(REPO, "ci", "bench.sh")) as fh:
            text = fh.read()
        emitted = set(re.findall(r'printf "%s,%s,%s,%s,([a-z0-9_]+),', text))
        emitted |= set(re.findall(r'row bgsave "[^"]*" ([a-z0-9_]+) ', text))
        self.assertTrue({"rps", "cpu_us_per_op", "fork_ms", "bgsave_wall_ms"} <= emitted, emitted)
        self.assertEqual(emitted - set(b2j.METRICS), set())
        for name, m in b2j.METRICS.items():
            self.assertIn(m["better"], ("higher", "lower"), name)
            self.assertTrue(m["unit"] and m["title"], name)


class ConvertFixture(TempDirCase):
    def setUp(self):
        super().setUp()
        self.ds = self.convert([FIXTURE], {"kind": "proxy", "toolchain": "host", "title": "t",
                                           "platform": "p", "redis_version": "8.10.2",
                                           "date": "2026-09-28T09:16:19Z", "id": "fixture"})

    def test_contract_shape(self):
        self.assertEqual(b2j.validate_dataset(self.ds), [])
        self.assertEqual(self.ds["reps"], 3)
        self.assertEqual([a["label"] for a in self.ds["arms"]], ["baseline", "fork"])
        self.assertEqual(self.ds["arms"][0]["name"], "Stock build: upstream flags (-O0), no patches")
        self.assertEqual(self.ds["arms"][1]["name"], "This fork: -O2 + patch series")
        self.assertEqual(self.ds["date"], "2026-09-28T09:16:19Z")
        self.assertIsNone(self.ds["commit"])
        self.assertIsNone(self.ds["run_url"])
        self.assertEqual([w["id"] for w in self.ds["workloads"]],
                         ["set-c1-p1-d3", "get-c50-p16-d3", "hmget-c1-d1900000",
                          "hset-large-c1-d1900000", "bgsave-64mb"])

    def test_medians_match_the_csv(self):
        groups = {}
        for r in fixture_rows():
            w = b2j.describe_workload(r["test"], r["params"])["id"]
            groups.setdefault((w, r["metric"], r["label"]), []).append(float(r["value"]))
        got = {(r["workload"], r["metric"], r["arm"]): r for r in self.ds["results"]}
        self.assertEqual(set(got), set(groups))
        for key, vals in groups.items():
            with self.subTest(key=key):
                self.assertAlmostEqual(got[key]["median"], statistics.median(vals), places=6)
                self.assertEqual(sorted(got[key]["values"]), sorted(vals))

    def test_values_are_in_rep_order(self):
        by_rep = {}
        for r in fixture_rows():
            if r["test"] == "set" and r["metric"] == "rps" and r["label"] == "fork":
                by_rep[int(r["rep"])] = float(r["value"])
        res = [r for r in self.ds["results"]
               if r["workload"] == "set-c1-p1-d3" and r["metric"] == "rps" and r["arm"] == "fork"][0]
        self.assertEqual(res["values"], [by_rep[1], by_rep[2], by_rep[3]])

    def test_bgsave_has_every_save(self):
        res = {(r["metric"], r["arm"]): r for r in self.ds["results"] if r["workload"] == "bgsave-64mb"}
        self.assertEqual(len(res[("fork_ms", "fork")]["values"]), 9)
        self.assertEqual(len(res[("used_memory_mb", "baseline")]["values"]), 3)


class ConvertRobustness(TempDirCase):
    def test_missing_arm_and_metrics(self):
        p = self.write("a.csv", CSV_HEADER +
                       "baseline,1,set,c=1 P=1 d=3,rps,100\n"
                       "baseline,2,set,c=1 P=1 d=3,rps,300\n")
        ds = self.convert([p], {"kind": "windows-ci", "toolchain": "msys2", "date": "2026-01-02"})
        self.assertEqual(b2j.validate_dataset(ds), [])
        self.assertEqual([a["label"] for a in ds["arms"]], ["baseline"])
        self.assertEqual(ds["results"][0]["median"], 200)
        self.assertTrue(any("No fork rows" in n for n in ds["notes"]))
        self.assertTrue(any("runner noise" in n for n in ds["notes"]))

    def test_crlf_garbage_repeated_headers_and_several_files(self):
        a = self.write("a.csv", (CSV_HEADER + "fork,1,get,c=1 P=1 d=3,rps,10\n"
                                 "fork,1,get,c=1 P=1 d=3,p99_ms,n/a\n"
                                 "not,a,row\n\n").replace("\n", "\r\n"))
        b = self.write("b.csv", CSV_HEADER + "fork,2,get,c=1 P=1 d=3,rps,30\n"
                                             "baseline,1,get,c=1 P=1 d=3,rps,5\n")
        ds = self.convert([a, b], {"kind": "proxy", "toolchain": "host", "date": "2026-01-02"})
        self.assertEqual(ds["reps"], 2)
        rps = {r["arm"]: r for r in ds["results"] if r["metric"] == "rps"}
        self.assertEqual(rps["fork"]["values"], [10, 30])
        self.assertEqual(rps["fork"]["median"], 20)
        self.assertNotIn("p99_ms", {r["metric"] for r in ds["results"]})

    def test_third_arm_and_unknown_metric(self):
        p = self.write("a.csv", CSV_HEADER +
                       "fork,1,set,c=1 P=1 d=3,rps,3\n"
                       "baseline-o2,1,set,c=1 P=1 d=3,rps,2\n"
                       "baseline,1,set,c=1 P=1 d=3,rps,1\n"
                       "baseline,1,set,c=1 P=1 d=3,queue_ms,1\n")
        ds = self.convert([p], {"kind": "proxy", "toolchain": "host", "date": "2026-01-02"})
        self.assertEqual([a["label"] for a in ds["arms"]], ["baseline", "baseline-o2", "fork"])
        bench = b2j.merge_datasets([ds], "2026-01-03T00:00:00Z")
        self.assertEqual(bench["metrics"]["queue_ms"]["unit"], "ms")
        self.assertEqual(bench["metrics"]["queue_ms"]["better"], "lower")

    def test_job_log_and_workflow_default(self):
        log = self.write("prepare.log",
                         "2026-09-28T10:00:00.1234567Z ##[group]Run $ErrorActionPreference = 'Stop'\n"
                         "2026-09-28T10:00:00.1234567Z env:\n"
                         "2026-09-28T10:00:00.1234567Z   FORK_LABEL: fork\n"
                         "2026-09-28T10:00:00.1234567Z   PATCH_BASE_VERSION: 8.10.2\n"
                         "2026-09-28T10:00:00.1234567Z   INPUT_VERSION: \n"
                         "2026-09-28T10:00:00.1234567Z   INPUT_OPTIMIZATION: -O3\n")
        self.assertEqual(b2j.read_job_log(log), {"redis_version": "8.10.2", "optimization": "-O3"})
        self.assertEqual(b2j.read_job_log(os.path.join(self.tmp, "missing.log")), {})
        self.assertRegex(b2j.version_from_workflow(os.path.join(REPO, ".github", "workflows", "verify.yml")) or "",
                         r"^[0-9]+\.[0-9]+")

    def test_cli_convert_with_meta(self):
        meta = self.write("meta.json", json.dumps({
            "run_id": 123456, "run_url": "https://example.invalid/runs/123456",
            "head_sha": "0123456789abcdef0123456789abcdef01234567",
            "created_at": "2026-09-01T08:00:00Z", "runner": "GitHub-hosted windows-latest"}))
        log = self.write("prepare.log", "  INPUT_VERSION: 8.10.1\n  INPUT_OPTIMIZATION: -O2\n")
        out = os.path.join(self.tmp, "ds.json")
        r = run(["convert", FIXTURE, "-o", out, "--kind", "windows-ci", "--toolchain", "cygwin",
                 "--meta", meta, "--job-log", log])
        self.assertEqual(r.returncode, 0, r.stderr)
        with open(out) as fh:
            ds = json.load(fh)
        self.assertEqual(b2j.validate_dataset(ds), [])
        self.assertEqual(ds["id"], "windows-ci-cygwin-run123456")
        self.assertEqual(ds["commit"], "0123456")
        self.assertEqual(ds["redis_version"], "8.10.1")
        self.assertEqual(ds["platform"], "GitHub-hosted windows-latest")
        self.assertEqual(ds["title"], "Windows CI (Cygwin): stock -O0 vs fork -O2")
        self.assertEqual(ds["date"], "2026-09-01T08:00:00Z")

    def test_cli_convert_without_rows_fails(self):
        p = self.write("empty.csv", CSV_HEADER)
        r = run(["convert", p, "-o", os.path.join(self.tmp, "x.json")])
        self.assertNotEqual(r.returncode, 0)


class Merge(TempDirCase):
    def ds(self, ident, kind, date):
        p = self.write(ident + ".csv", CSV_HEADER + "baseline,1,get,c=1 P=1 d=3,rps,1\n"
                                                    "fork,1,get,c=1 P=1 d=3,rps,2\n")
        toolchain = "host" if kind == "proxy" else "msys2"
        return self.convert([p], {"kind": kind, "toolchain": toolchain, "date": date, "id": ident})

    def test_order_and_metrics(self):
        items = [self.ds("old-ci", "windows-ci", "2026-08-01T10:00:00Z"),
                 self.ds("same-day-proxy", "proxy", "2026-09-01T23:00:00Z"),
                 self.ds("same-day-ci", "windows-ci", "2026-09-01T01:00:00Z"),
                 self.ds("newest-proxy", "proxy", "2026-09-10T00:00:00Z")]
        bench = b2j.merge_datasets(items, "2026-09-11T00:00:00Z")
        self.assertEqual(bench["schema"], 1)
        self.assertEqual(bench["generated_at"], "2026-09-11T00:00:00Z")
        self.assertEqual([d["id"] for d in bench["datasets"]],
                         ["newest-proxy", "same-day-ci", "same-day-proxy", "old-ci"])
        self.assertEqual(list(bench["metrics"]), ["rps"])
        self.assertEqual(bench["metrics"]["rps"], {"unit": "req/s", "better": "higher", "title": "Throughput"})

    def test_merge_skips_bad_inputs_and_duplicates(self):
        good = self.write("good.json", json.dumps(self.ds("good", "proxy", "2026-09-01")))
        dup = self.write("dup.json", json.dumps(self.ds("good", "proxy", "2026-09-02")))
        bad = self.write("bad.json", "{ not json")
        wrong = self.write("wrong.json", json.dumps({"id": "x"}))
        out = os.path.join(self.tmp, "bench.json")
        r = run(["merge", "-o", out, good, dup, bad, wrong, os.path.join(self.tmp, "missing.json")])
        self.assertEqual(r.returncode, 0, r.stderr)
        with open(out) as fh:
            bench = json.load(fh)
        self.assertEqual([d["id"] for d in bench["datasets"]], ["good"])
        self.assertIn("skipping", r.stderr)

    def test_merge_of_nothing(self):
        out = os.path.join(self.tmp, "bench.json")
        r = run(["merge", "-o", out])
        self.assertEqual(r.returncode, 0, r.stderr)
        with open(out) as fh:
            bench = json.load(fh)
        self.assertEqual(bench["datasets"], [])
        self.assertEqual(bench["metrics"], {})

    def test_ratio_orientation(self):
        self.assertAlmostEqual(b2j.ratio(200, 100, "higher"), 2.0)
        self.assertAlmostEqual(b2j.ratio(50, 100, "lower"), 2.0)
        self.assertIsNone(b2j.ratio(None, 100, "lower"))
        self.assertIsNone(b2j.ratio(1, 0, "lower"))


class CommittedDatasets(unittest.TestCase):
    def test_committed_datasets_are_valid(self):
        if not os.path.isdir(DATASETS_DIR):
            self.skipTest("no committed datasets")
        names = sorted(n for n in os.listdir(DATASETS_DIR) if n.endswith(".json"))
        ids = set()
        for name in names:
            with self.subTest(dataset=name):
                with open(os.path.join(DATASETS_DIR, name), encoding="utf-8") as fh:
                    d = json.load(fh)
                self.assertEqual(b2j.validate_dataset(d), [])
                self.assertNotIn(d["id"], ids)
                ids.add(d["id"])
                stem = name[:-len(".json")]
                raw = os.path.join(DATASETS_DIR, stem + ".csv")
                if os.path.exists(raw):
                    again = b2j.build_dataset(b2j.read_rows([raw]), {
                        "kind": d["kind"], "toolchain": d["toolchain"], "date": d["date"]})
                    self.assertEqual(again["results"], d["results"], "JSON does not match its raw CSV")


@unittest.skipUnless(shutil.which("bash"), "bash not available")
class BuildSite(TempDirCase):
    def build(self, *extra):
        out = os.path.join(self.tmp, "out")
        r = subprocess.run(["bash", BUILD_SITE, out] + list(extra), capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        with open(os.path.join(out, "data", "bench.json"), encoding="utf-8") as fh:
            return out, json.load(fh), r.stderr

    def test_without_bench_dir(self):
        out, bench, _ = self.build()
        for rel in (".nojekyll", "docs/OPERATIONS.md", "docs/ROADMAP.md", "docs/LICENSING.md",
                    "docs/patches.md", "assets/banner-dark.svg"):
            self.assertTrue(os.path.exists(os.path.join(out, rel)), rel)
        for d in bench["datasets"]:
            self.assertEqual(b2j.validate_dataset(d), [])

    def test_with_empty_and_missing_bench_dir(self):
        empty = os.path.join(self.tmp, "empty")
        os.makedirs(empty)
        _, a, _ = self.build("--bench-dir", empty)
        _, b, _ = self.build("--bench-dir", os.path.join(self.tmp, "missing"))
        self.assertEqual(len(a["datasets"]), len(b["datasets"]))

    def test_refuses_to_empty_a_foreign_directory(self):
        keep = self.write("out/important.txt", "keep me")
        r = subprocess.run(["bash", BUILD_SITE, os.path.join(self.tmp, "out")], capture_output=True, text=True)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("--force", r.stderr)
        self.assertTrue(os.path.exists(keep))
        out, _, _ = self.build("--force")
        self.assertFalse(os.path.exists(keep))
        self.assertTrue(os.path.exists(os.path.join(out, ".nojekyll")))

    def test_rebuilds_over_an_earlier_build(self):
        out, _, _ = self.build()
        stale = os.path.join(out, "stale.html")
        with open(stale, "w") as fh:
            fh.write("old")
        self.build()
        self.assertFalse(os.path.exists(stale))

    def test_with_runs(self):
        runs = os.path.join(self.tmp, "runs")
        with open(FIXTURE) as fh:
            rows = fh.read()
        self.write("runs/111/meta.json", json.dumps({
            "run_id": 111, "run_url": "https://example.invalid/runs/111", "head_sha": "abcdef1234567",
            "created_at": "2030-01-01T00:00:00Z", "runner": "GitHub-hosted windows-latest"}))
        self.write("runs/111/bench-msys2/bench-msys2.csv", rows)
        self.write("runs/111/bench-cygwin/bench-cygwin.csv", CSV_HEADER)  # empty: skipped
        self.write("runs/222/other.txt", "no csv here")
        self.write("runs/333/bench-weird/bench-weird.csv", rows)          # unknown toolchain
        _, bench, err = self.build("--bench-dir", runs)
        ci = [d for d in bench["datasets"] if d["kind"] == "windows-ci"]
        self.assertEqual([d["id"] for d in ci], ["windows-ci-msys2-run111"])
        self.assertEqual(bench["datasets"][0]["id"], "windows-ci-msys2-run111")
        self.assertIn("unknown toolchain", err)


if __name__ == "__main__":
    unittest.main()
