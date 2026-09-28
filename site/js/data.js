// Loading, indexing and wording for data/bench.json (schema 1).
// Every number the site shows comes through here from that file; nothing is computed
// except medians' ratios, and those always compare fork with baseline.

export const REPO = "https://github.com/ahmedawachi/redis-windows";

// Arms keep a fixed categorical slot whatever the dataset contains.
export const ARM_ORDER = ["fork", "baseline", "baseline-o2"];
const ARM_SHORT = { fork: "Fork", baseline: "Stock", "baseline-o2": "Stock -O2" };
export const armShort = (label) => ARM_SHORT[label] || label;
export const armClass = (label) => (ARM_ORDER.includes(label) ? `arm-${label}` : "arm-other");

// verify.yml: "Differences under about 10% are within runner noise."
export const NOISE = 0.10;

const DEFAULT_METRICS = {
  rps: { unit: "req/s", better: "higher", title: "Throughput" },
  avg_ms: { unit: "ms", better: "lower", title: "Average latency" },
  p50_ms: { unit: "ms", better: "lower", title: "p50 latency" },
  p99_ms: { unit: "ms", better: "lower", title: "p99 latency" },
  max_ms: { unit: "ms", better: "lower", title: "Max latency" },
  cpu_user_s: { unit: "s", better: "lower", title: "Server user CPU" },
  cpu_sys_s: { unit: "s", better: "lower", title: "Server system CPU" },
  cpu_total_s: { unit: "s", better: "lower", title: "Server CPU (user + sys)" },
  cpu_us_per_op: { unit: "µs", better: "lower", title: "Server CPU per operation" },
  fork_ms: { unit: "ms", better: "lower", title: "Fork stall" },
  bgsave_wall_ms: { unit: "ms", better: "lower", title: "BGSAVE duration" },
  child_cpu_s: { unit: "s", better: "lower", title: "BGSAVE child CPU" },
  used_memory_mb: { unit: "MB", better: null, title: "Dataset size (used_memory)" },
};

export async function loadBench(url = "data/bench.json") {
  let res;
  try {
    res = await fetch(url, { cache: "no-cache" });
  } catch (e) {
    return { status: "missing", datasets: [] };
  }
  if (!res.ok) return { status: "missing", datasets: [] };
  let data;
  try {
    data = await res.json();
  } catch (e) {
    return { status: "invalid", datasets: [] };
  }
  if (!data || typeof data !== "object" || !Array.isArray(data.datasets)) return { status: "invalid", datasets: [] };
  const metrics = { ...DEFAULT_METRICS };
  for (const [k, v] of Object.entries(data.metrics || {})) metrics[k] = { ...(DEFAULT_METRICS[k] || {}), ...v };
  const datasets = data.datasets
    .filter((d) => d && typeof d === "object" && Array.isArray(d.results) && Array.isArray(d.workloads))
    .map((d) => indexDataset(d, metrics))
    .filter((d) => d.results.length > 0);
  datasets.sort(compareDatasets);
  return { status: datasets.length ? "ok" : "empty", data, metrics, datasets, generatedAt: data.generated_at || null };
}

function compareDatasets(a, b) {
  const ka = a.kind === "windows-ci" ? 0 : 1;
  const kb = b.kind === "windows-ci" ? 0 : 1;
  if (ka !== kb) return ka - kb;
  const ta = Date.parse(a.date), tb = Date.parse(b.date);
  const na = Number.isNaN(ta), nb = Number.isNaN(tb);
  if (na !== nb) return na ? 1 : -1;
  if (!na && ta !== tb) return tb - ta;
  return String(a.id).localeCompare(String(b.id));
}

export function parseParams(s) {
  const out = {};
  for (const tok of String(s || "").split(/\s+/)) {
    const m = tok.match(/^([A-Za-z_]+)=(.+)$/);
    if (m) out[m[1]] = m[2];
  }
  return out;
}

const GROUP_DEFAULT = {
  small: "Small values",
  large: "Large values",
  hmget: "Large hash reads",
  hset: "Large hash writes",
  bgsave: "Persistence",
};

// Works for both a category-style `test` ("small", "large", "hmget", "hset", "bgsave")
// and the raw ci/bench.sh test names ("set", "get", "hset", "hmget", "hset-large").
export function classify(w) {
  const t = String(w.test || "").toLowerCase();
  const p = parseParams(w.params);
  const hay = `${w.id || ""} ${w.title || ""}`.toLowerCase();
  const d = Number(p.d);
  let cmd = null;
  const c0 = String(w.command || "").toLowerCase();
  if (["set", "get", "hset", "hmget"].includes(c0)) cmd = c0;
  else if (c0 === "hset-large") cmd = "hset";
  else if (["set", "get", "hset", "hmget"].includes(t)) cmd = t;
  else if (t === "hset-large") cmd = "hset";
  if (!cmd) {
    const m = hay.match(/\b(hmget|hset|set|get)\b/);
    cmd = m ? m[1] : null;
  }
  let cat;
  if (t === "bgsave" || p.populate) cat = "bgsave";
  else if (t === "hmget") cat = "hmget";
  else if (t === "hset-large") cat = "hset";
  else if (t === "small" || t === "large") cat = t;
  else if (t === "hset") cat = Number.isFinite(d) && d <= 4096 ? "small" : "hset";
  else if (t === "set" || t === "get") cat = Number.isFinite(d) && d > 4096 ? "large" : "small";
  else cat = t || "other";
  const P = Number(p.P || 1), c = Number(p.c || 1);
  return {
    cat, cmd, params: p,
    clients: Number.isFinite(c) ? c : 1,
    pipeline: Number.isFinite(P) ? P : 1,
    bytes: Number.isFinite(d) ? d : null,
    populateMB: p.populate ? Number(String(p.populate).replace(/[^0-9.]/g, "")) : null,
  };
}

export function formatBytes(n) {
  if (n == null || !Number.isFinite(n)) return "";
  if (n >= 1e6) return `${trimNum(n / 1e6, 1)} MB`;
  if (n >= 1e3) return `${trimNum(n / 1e3, 1)} kB`;
  return `${n} B`;
}
const trimNum = (v, d) => String(Number(v.toFixed(d)));

function fallbackTitle(w, info) {
  if (info.cat === "bgsave") return info.populateMB ? `BGSAVE · ${info.populateMB} MB dataset` : "BGSAVE";
  const parts = [info.cmd ? info.cmd.toUpperCase() : (w.test || w.id)];
  if (info.bytes) parts.push(formatBytes(info.bytes));
  const cl = `${info.clients} client${info.clients === 1 ? "" : "s"}`;
  parts.push(info.pipeline > 1 ? `${cl} × ${info.pipeline} pipelined` : cl);
  return parts.join(" · ");
}

function indexDataset(d, metrics) {
  const workloads = d.workloads.map((w) => {
    const info = classify(w);
    return { ...w, info, title: w.title || fallbackTitle(w, info), group: w.group || GROUP_DEFAULT[info.cat] || "Other" };
  });
  const byId = new Map(workloads.map((w) => [w.id, w]));
  const map = new Map();
  const results = [];
  for (const r of d.results) {
    if (!r || !byId.has(r.workload) || typeof r.median !== "number" || !Number.isFinite(r.median)) continue;
    map.set(`${r.workload}|${r.metric}|${r.arm}`, r);
    results.push(r);
  }
  const declared = Array.isArray(d.arms) ? d.arms.filter((a) => a && a.label) : [];
  const seen = new Set(results.map((r) => r.arm));
  const arms = declared.filter((a) => seen.has(a.label));
  for (const label of seen) if (!arms.some((a) => a.label === label)) arms.push({ label, name: label });
  arms.sort((a, b) => rank(a.label) - rank(b.label));
  const usedWorkloads = workloads.filter((w) => results.some((r) => r.workload === w.id));
  return {
    ...d,
    metrics,
    workloads: usedWorkloads,
    arms,
    results,
    isProxy: d.kind !== "windows-ci",
    get(wid, metric, arm) { return map.get(`${wid}|${metric}|${arm}`); },
    median(wid, metric, arm) { const r = map.get(`${wid}|${metric}|${arm}`); return r ? r.median : null; },
    has(arm) { return arms.some((a) => a.label === arm); },
  };
}
const rank = (label) => { const i = ARM_ORDER.indexOf(label); return i < 0 ? 99 : i; };

// ------------------------------------------------------------------ comparisons

export function metricInfo(ds, metric) {
  return ds.metrics[metric] || { unit: "", better: null, title: metric };
}

// Oriented speedup: > 1 means the fork is better on this metric. Null when not comparable.
export function speedup(ds, wid, metric) {
  const m = metricInfo(ds, metric);
  const f = ds.median(wid, metric, "fork");
  const b = ds.median(wid, metric, "baseline");
  if (f == null || b == null || !m.better) return null;
  if (m.better === "higher") return b > 0 ? f / b : null;
  return f > 0 ? b / f : null;
}

export function verdict(s) {
  if (s == null) return "none";
  if (s >= 1 + NOISE) return "better";
  if (s <= 1 / (1 + NOISE)) return "worse";
  return "neutral";
}

export function formatRatio(r) {
  if (r == null || !Number.isFinite(r)) return "–";
  return `${r >= 10 ? r.toFixed(1) : r.toFixed(2)}×`;
}

const PHRASES = {
  rps: ["higher throughput", "lower throughput"],
  cpu_us_per_op: ["less CPU per command", "more CPU per command"],
  cpu_total_s: ["less server CPU", "more server CPU"],
  p50_ms: ["lower p50 latency", "higher p50 latency"],
  p99_ms: ["lower p99 latency", "higher p99 latency"],
  avg_ms: ["lower average latency", "higher average latency"],
  max_ms: ["lower max latency", "higher max latency"],
  fork_ms: ["shorter fork stall", "longer fork stall"],
  bgsave_wall_ms: ["faster BGSAVE", "slower BGSAVE"],
  child_cpu_s: ["less BGSAVE child CPU", "more BGSAVE child CPU"],
};

// "1.52× less CPU per command", oriented so the factor is always >= 1.
export function describe(ds, metric, s) {
  if (s == null) return "no comparison";
  const m = metricInfo(ds, metric);
  const [good, bad] = PHRASES[metric] || (m.better === "higher" ? ["higher", "lower"] : ["lower", "higher"]);
  const text = s >= 1 ? `${formatRatio(s)} ${good}` : `${formatRatio(1 / s)} ${bad}`;
  return verdict(s) === "neutral" ? `${text}, within noise` : text;
}

// ------------------------------------------------------------------ number formatting

const NF0 = new Intl.NumberFormat("en-US", { maximumFractionDigits: 0 });
export const nf = (v, digits = 0) => new Intl.NumberFormat("en-US", { maximumFractionDigits: digits, minimumFractionDigits: 0 }).format(v);

export function fmtNum(v) {
  if (v == null || !Number.isFinite(v)) return "–";
  const a = Math.abs(v);
  if (a >= 1000) return NF0.format(v);
  if (a >= 100) return nf(v, 0);
  if (a >= 10) return nf(v, 1);
  if (a >= 1) return nf(v, 2);
  if (a === 0) return "0";
  return nf(v, 3);
}

// Two numbers shown side by side ("1.20 µs vs 2.26 µs") keep the same number of decimals,
// so trimming a trailing zero never makes one look less precise than the other.
const fracDigits = (v) => { const a = Math.abs(v); return a >= 100 || a === 0 ? 0 : a >= 10 ? 1 : a >= 1 ? 2 : 3; };
export function fmtPair(a, b) {
  if (a == null || b == null || !Number.isFinite(a) || !Number.isFinite(b)) return [fmtNum(a), fmtNum(b)];
  const d = Math.max(fracDigits(a), fracDigits(b));
  const f = new Intl.NumberFormat("en-US", { minimumFractionDigits: d, maximumFractionDigits: d });
  return [f.format(a), f.format(b)];
}

export function fmtCompact(v) {
  if (v == null || !Number.isFinite(v)) return "–";
  const a = Math.abs(v);
  if (a >= 1e6) return `${trimNum(v / 1e6, a >= 1e7 ? 1 : 2)}M`;
  if (a >= 1e4) return `${trimNum(v / 1e3, a >= 1e5 ? 0 : 1)}K`;
  return fmtNum(v);
}

// Axis ticks share one style per axis: all compact (5K, 10K) once the axis reaches 10,000.
export function fmtAxisCompact(v, axisMax) {
  if (!(axisMax >= 1e4) || v === 0) return fmtNum(v);
  if (Math.abs(v) >= 1e6) return `${trimNum(v / 1e6, 2)}M`;
  return `${trimNum(v / 1e3, 1)}K`;
}

export function fmtValue(v, unit, compact = false) {
  const n = compact ? fmtCompact(v) : fmtNum(v);
  return unit ? `${n} ${unit}` : n;
}

export function fmtDate(iso) {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return iso ? String(iso) : "–";
  return new Date(t).toLocaleDateString("en-GB", { year: "numeric", month: "short", day: "numeric", timeZone: "UTC" });
}

// ------------------------------------------------------------------ fixed workload choices

const byCat = (ds, cat) => ds.workloads.filter((w) => w.info.cat === cat);

// The headline is FIXED: server CPU per command of the pipelined small GET. If that
// workload is absent, the first small workload that has the metric; never the best one.
export const HEADLINE_METRIC = "cpu_us_per_op";
export function headlineWorkload(ds) {
  const small = byCat(ds, "small").filter((w) => ds.median(w.id, HEADLINE_METRIC, "fork") != null);
  return small.find((w) => w.info.cmd === "get" && w.info.pipeline > 1)
    || small.find((w) => w.info.cmd === "get")
    || small[0]
    || null;
}

export function headline(ds) {
  const w = headlineWorkload(ds);
  if (!w) return null;
  const s = speedup(ds, w.id, HEADLINE_METRIC);
  if (s == null) return null;
  return { workload: w, metric: HEADLINE_METRIC, s, fork: ds.median(w.id, HEADLINE_METRIC, "fork"), baseline: ds.median(w.id, HEADLINE_METRIC, "baseline") };
}

// KPI tiles, each fork vs stock on one fixed workload.
export function kpis(ds) {
  const out = [];
  const add = (key, label, w, metric) => {
    if (!w) return;
    const f = ds.median(w.id, metric, "fork");
    const b = ds.median(w.id, metric, "baseline");
    if (f == null || b == null) return;
    const s = speedup(ds, w.id, metric);
    out.push({ key, label, workload: w, metric, unit: metricInfo(ds, metric).unit, fork: f, baseline: b, s, text: describe(ds, metric, s), verdict: verdict(s) });
  };
  const small = byCat(ds, "small");
  const pipeGet = small.find((w) => w.info.cmd === "get" && w.info.pipeline > 1) || small.find((w) => w.info.pipeline > 1);
  if (pipeGet) add("thr-small", `Throughput, ${pipeGet.info.cmd ? pipeGet.info.cmd.toUpperCase() : "small values"} pipelined`, pipeGet, "rps");
  const hm = byCat(ds, "hmget");
  const hmW = hm.find((w) => w.info.clients === 1) || hm[0];
  if (hmW) add("thr-hmget", `HMGET ${formatBytes(hmW.info.bytes) || "large"} throughput`, hmW, "rps");
  const large = byCat(ds, "large");
  const lgGet = large.find((w) => w.info.cmd === "get") || large[0];
  if (lgGet) add("p99-large", `p99 latency, ${lgGet.info.cmd ? lgGet.info.cmd.toUpperCase() : "large"} ${formatBytes(lgGet.info.bytes)}`.trim(), lgGet, "p99_ms");
  const bg = byCat(ds, "bgsave").filter((w) => ds.median(w.id, "fork_ms", "fork") != null);
  bg.sort((a, b) => (b.info.populateMB || 0) - (a.info.populateMB || 0));
  if (bg[0]) add("bgsave-stall", `BGSAVE fork stall${bg[0].info.populateMB ? `, ${bg[0].info.populateMB} MB` : ""}`, bg[0], "fork_ms");
  return out;
}

export function proxyBadgeText(ds) {
  return /mac/i.test(ds.platform || "") || /mac/i.test(ds.title || "") ? "macOS proxy — not Windows" : "Proxy — not Windows";
}
