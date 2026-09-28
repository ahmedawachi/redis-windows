import * as D from "./data.js";
import { h, roving, barFacet, divergingBars, dumbbells, lineChart, dataTable, legend } from "./charts.js";
import { statTile } from "./tiles.js";

const $ = (sel, root = document) => root.querySelector(sel);
const state = { bench: null, toolchain: "all", ds: null, speedMetric: "rps", latMetric: "p99_ms", tables: new Set() };

const ICON_INFO = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="9"/><path d="M12 8h.01M11 12h1v5h1"/></svg>';
const ICON_TABLE = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" aria-hidden="true"><rect x="2" y="3" width="12" height="10" rx="1.5"/><path d="M2 6.5h12M6 6.5V13"/></svg>';
const ICON_CHART = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" aria-hidden="true"><path d="M3 13V8M8 13V3M13 13V6"/></svg>';

init();

async function init() {
  wireTableToggles();
  wireSegments();
  state.bench = await D.loadBench("data/bench.json");
  if (state.bench.status !== "ok") { renderEmpty(state.bench.status); return; }
  setupControls();
  selectFromUrl();
  renderAll();
  let lastW = 0, raf = 0;
  new ResizeObserver((entries) => {
    const w = Math.round(entries[0].contentRect.width);
    if (w === lastW) return;
    lastW = w;
    cancelAnimationFrame(raf);
    raf = requestAnimationFrame(() => renderCharts());
  }).observe($("#bench-area"));
}

// ------------------------------------------------------------------ controls

function visibleDatasets() {
  const all = state.bench.datasets;
  return state.toolchain === "all" ? all : all.filter((d) => d.toolchain === state.toolchain);
}

function setupControls() {
  $("#controls").hidden = false;
  const toolchains = [...new Set(state.bench.datasets.map((d) => d.toolchain).filter(Boolean))];
  const tc = $("#tc-select");
  if (toolchains.length > 1) {
    $("#tc-wrap").hidden = false;
    tc.append(h("option", { value: "all" }, "All toolchains"));
    for (const t of toolchains) tc.append(h("option", { value: t }, toolchainName(t)));
    tc.addEventListener("change", () => {
      state.toolchain = tc.value;
      fillDatasets();
      const list = visibleDatasets();
      if (!list.includes(state.ds)) state.ds = list[0];
      $("#ds-select").value = state.ds.id;
      update();
    });
  }
  fillDatasets();
  $("#ds-select").addEventListener("change", (e) => {
    state.ds = state.bench.datasets.find((d) => d.id === e.target.value) || state.ds;
    update();
  });
}

function fillDatasets() {
  const sel = $("#ds-select");
  sel.replaceChildren();
  const list = visibleDatasets();
  const ci = list.filter((d) => !d.isProxy), px = list.filter((d) => d.isProxy);
  const add = (label, items) => {
    if (!items.length) return;
    const g = h("optgroup", { label });
    for (const d of items) g.append(h("option", { value: d.id, title: datasetLabel(d) }, datasetLabel(d)));
    sel.append(g);
  };
  add("Windows CI", ci);
  add("Proxy measurements (not Windows)", px);
}

// Date first: a narrow select truncates the end of the label, and the date is what
// tells two runs of the same title apart.
function datasetLabel(d) {
  const title = d.title || d.id;
  const bits = d.date ? [D.fmtDate(d.date), title] : [title];
  if (d.toolchain && d.toolchain !== "host" && !title.toLowerCase().includes(d.toolchain.toLowerCase())) bits.push(toolchainName(d.toolchain));
  return bits.join(" · ");
}
const toolchainName = (t) => ({ msys2: "MSYS2", cygwin: "Cygwin", host: "Host" }[t] || t);

function selectFromUrl() {
  const want = new URLSearchParams(location.search).get("dataset");
  state.ds = state.bench.datasets.find((d) => d.id === want) || state.bench.datasets[0];
  $("#ds-select").value = state.ds.id;
}

function update() {
  const area = $("#bench-area");
  area.classList.add("is-stale");
  const url = new URL(location.href);
  url.searchParams.set("dataset", state.ds.id);
  history.replaceState(null, "", url);
  // Hold the previous render at reduced opacity for a beat, then swap.
  setTimeout(() => {
    renderAll();
    requestAnimationFrame(() => area.classList.remove("is-stale"));
  }, 140);
}

function wireSegments() {
  for (const seg of document.querySelectorAll(".seg")) {
    seg.addEventListener("click", (e) => {
      const b = e.target.closest("button[data-metric]");
      if (!b) return;
      for (const x of seg.querySelectorAll("button")) x.setAttribute("aria-pressed", String(x === b));
      const fig = seg.closest("figure");
      if (fig.id === "fig-speedup") { state.speedMetric = b.dataset.metric; renderSpeedup(); }
      if (fig.id === "fig-latency") { state.latMetric = b.dataset.metric; renderLatency(); }
    });
  }
}

function wireTableToggles() {
  for (const b of document.querySelectorAll("[data-table-toggle]")) {
    paintTableBtn(b, false);
    b.addEventListener("click", () => {
      const fig = b.closest("figure");
      const on = b.getAttribute("aria-pressed") !== "true";
      paintTableBtn(b, on);
      $("[data-table]", fig).hidden = !on;
      $("[data-body]", fig).hidden = on;
      $("[data-legend]", fig).hidden = on;
      if (on) state.tables.add(fig.id); else state.tables.delete(fig.id);
    });
  }
}
function paintTableBtn(b, on) {
  b.setAttribute("aria-pressed", String(on));
  b.innerHTML = on ? ICON_CHART : ICON_TABLE;
  b.append(document.createTextNode(on ? "Chart" : "Table"));
}

// ------------------------------------------------------------------ page states

function renderEmpty(status) {
  const n = $("#notice");
  n.replaceChildren();
  const card = h("section", { class: "card empty-state", "aria-labelledby": "empty-title" });
  card.append(h("h2", { id: "empty-title" }, status === "invalid" ? "The benchmark data could not be read" : "No benchmark results yet"));
  if (status === "invalid") {
    card.append(h("p", null, "data/bench.json exists but is not in the expected format, so nothing is drawn rather than something wrong."));
  } else {
    card.append(h("p", null, "The first Windows benchmark run is pending. Nothing on this page is filled in by hand: the charts appear once a run's results are published to data/bench.json."));
  }
  card.append(h("p", null, "How results appear here:"));
  const ol = h("ol");
  ol.append(
    h("li", null, "The Verify workflow builds the stock and the fork packages for MSYS2 and Cygwin."),
    h("li", null, "One windows-latest runner per toolchain benchmarks both with ci/bench.sh, interleaved, three repetitions."),
    h("li", null, "The medians are published to data/bench.json, and this page draws every chart from that file."),
  );
  card.append(ol);
  n.append(card);
}

function renderNotice() {
  const n = $("#notice");
  n.replaceChildren();
  const ds = state.ds;
  const anyCI = state.bench.datasets.some((d) => !d.isProxy);
  if (!ds.isProxy) return;
  const box = h("div", { class: "notice proxy", role: "note" });
  box.innerHTML = ICON_INFO;
  const body = h("div");
  if (!anyCI) body.append(h("h2", null, "The first Windows benchmark run is pending; showing a macOS proxy measurement."));
  else body.append(h("h2", null, "This dataset is a proxy measurement, not Windows."));
  body.append(h("p", null, `Measured on ${ds.platform || "a non-Windows host"}. It shows the effect of the build flags and patches on the same Redis source, but Windows numbers come from the benchmark job.`));
  box.append(body);
  n.append(box);
}

function titleBadges() {
  for (const t of document.querySelectorAll(".chart-title, #hf-title-row")) {
    for (const b of t.querySelectorAll(".badge")) b.remove();
    if (state.ds.isProxy) t.append(h("span", { class: "badge proxy" }, D.proxyBadgeText(state.ds)));
  }
}

function renderAll() {
  $("#bench-area").hidden = false;
  renderNotice();
  renderHero();
  renderKpis();
  renderCharts();
  renderMethod();
}

function renderCharts() {
  renderSpeedup();
  renderThroughput();
  renderLatency();
  renderCpu();
  renderPersist();
  renderHistory();
  titleBadges();
}

// ------------------------------------------------------------------ helpers

const ds = () => state.ds;
const requestWorkloads = () => ds().workloads.filter((w) => w.info.cat !== "bgsave");
const armsOf = () => ds().arms;

function figureWidth(fig) {
  const cs = getComputedStyle(fig);
  return Math.max(200, fig.clientWidth - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight));
}

function groupBy(list, key) {
  const out = [];
  for (const x of list) {
    const k = key(x);
    let g = out.find((o) => o.name === k);
    if (!g) { g = { name: k, items: [] }; out.push(g); }
    g.items.push(x);
  }
  return out;
}

function setSub(fig, text) { $("[data-sub]", fig).textContent = text; }

function show(fig, on) { fig.hidden = !on; }

function valueText(v, metric, full = false) {
  const m = D.metricInfo(ds(), metric);
  return D.fmtValue(v, m.unit, !full && metric === "rps");
}

function armTipRows(w, metric) {
  return armsOf()
    .map((a) => ({ a, v: ds().median(w.id, metric, a.label) }))
    .filter((x) => x.v != null)
    .map((x) => ({ value: valueText(x.v, metric), label: D.armShort(x.a.label), cls: D.armClass(x.a.label) }));
}

function ratioCell(w, metric) {
  const sp = D.speedup(ds(), w.id, metric);
  return sp == null ? "–" : D.describe(ds(), metric, sp);
}

function armLegend() {
  return legend(armsOf().map((a) => ({ cls: D.armClass(a.label), label: armLegendText(a) })));
}

function armLegendText(a) {
  const name = String(a.name || "");
  const rest = name.includes(": ") ? name.slice(name.indexOf(": ") + 2) : name;
  return rest && rest !== a.label ? `${D.armShort(a.label)} · ${rest}` : D.armShort(a.label);
}

function setLegend(fig, node) {
  const slot = $("[data-legend]", fig);
  slot.replaceChildren();
  if (node) slot.append(node);
}

function setTable(fig, table) {
  const slot = $("[data-table]", fig);
  slot.replaceChildren(table);
}

function setBody(fig, node) {
  const body = $("[data-body]", fig);
  body.replaceChildren(node);
  roving(body);
}

// ------------------------------------------------------------------ hero figure

function renderHero() {
  const box = $("#hero-fig");
  box.replaceChildren();
  const hl = D.headline(ds());
  if (!hl) { box.hidden = true; return; }
  box.hidden = false;
  const v = D.verdict(hl.s);
  const factor = hl.s >= 1 ? hl.s : 1 / hl.s;
  const words = hl.s >= 1 ? "less CPU per command" : "more CPU per command";

  const left = h("div");
  const kicker = h("p", { class: "hf-kicker", id: "hf-title-row" }, h("span", { id: "hf-title" }, "Headline: server CPU per command, fork vs stock"));
  if (ds().isProxy) kicker.append(h("span", { class: "badge proxy" }, D.proxyBadgeText(ds())));
  left.append(kicker);
  const num = h("div", { class: "hero-num" }, D.formatRatio(factor).replace("×", ""), h("span", { class: "x" }, "×"));
  left.append(num, h("p", { class: "hero-words" }, words + (v === "neutral" ? " (within runner noise)" : "")));
  left.append(h("p", { class: "hero-context" },
    `${hl.workload.title}. Server CPU (user + sys) divided by requests, median of ${ds().reps || "the"} repetitions. This metric is fixed for every dataset.`));

  const [forkText, stockText] = D.fmtPair(hl.fork, hl.baseline);
  const right = h("div", { class: "hf-compare", role: "img", "aria-label": `Fork ${forkText} µs per command, stock ${stockText} µs per command` });
  const max = Math.max(hl.fork, hl.baseline) * 1.3;
  for (const [label, val, text] of [["fork", hl.fork, forkText], ["baseline", hl.baseline, stockText]]) {
    const pct = (val / max) * 100;
    const row = h("div", { class: "hf-bar-row" });
    row.append(h("span", { class: "who" }, h("span", { class: `swatch ${D.armClass(label)}`, "aria-hidden": "true" }), D.armShort(label)));
    const track = h("div", { class: "hf-track" });
    track.append(h("div", { class: `hf-bar ${D.armClass(label)}`, style: { width: `${pct}%`, background: "var(--c)" } }));
    track.append(h("span", { class: "hf-val", style: { left: `calc(${pct}% + 8px)` } }, `${text} µs`));
    row.append(track);
    right.append(row);
  }
  right.append(h("p", { class: "hero-context", style: { margin: "4px 0 0" } }, "µs of server CPU per command · lower is better"));
  box.append(left, right);
}

// ------------------------------------------------------------------ KPI tiles

function renderKpis() {
  const row = $("#kpis");
  row.replaceChildren();
  for (const k of D.kpis(ds())) row.append(statTile(k));
  row.hidden = !row.children.length;
}

// ------------------------------------------------------------------ (c) speedup

function renderSpeedup() {
  const fig = $("#fig-speedup");
  const metric = state.speedMetric;
  const m = D.metricInfo(ds(), metric);
  const rows = requestWorkloads()
    .map((w) => ({ w, s: D.speedup(ds(), w.id, metric) }))
    .filter((x) => x.s != null);
  // Offer only the metrics this dataset has.
  for (const b of fig.querySelectorAll(".seg button")) b.disabled = !requestWorkloads().some((w) => D.speedup(ds(), w.id, b.dataset.metric) != null);
  if (!rows.length) { show(fig, false); return; }
  show(fig, true);
  setSub(fig, `Fork vs stock on ${m.title.toLowerCase()}, per workload. Right of 1× means the fork is better; grey bars are within runner noise (±10%).`);
  setLegend(fig, legend([
    { cls: "dir-better", label: "Fork better" },
    { cls: "dir-neutral", label: "Within noise" },
    { cls: "dir-worse", label: "Stock better" },
  ]));
  const groups = groupBy(rows, (x) => x.w.group).map((g) => ({
    name: g.name,
    rows: g.items.map(({ w, s: sp }) => ({
      label: w.title, s: sp, verdict: D.verdict(sp), short: D.formatRatio(sp), text: D.describe(ds(), metric, sp),
      tip: () => ({ rows: armTipRows(w, metric).filter((r) => !r.cls.includes("o2")), foot: [[D.describe(ds(), metric, sp)], w.title] }),
    })),
  }));
  const flat = groups.flatMap((g) => g.rows);
  const sorted = [...flat].sort((a, b) => a.s - b.s);
  const labelRows = flat.length > 1 ? [sorted[0], sorted[sorted.length - 1]] : flat;
  const w = figureWidth(fig);
  setBody(fig, divergingBars(groups, { width: w, labelRows }));
  setTable(fig, dataTable({
    caption: `Fork ÷ stock, ${m.title.toLowerCase()} (${m.unit})`,
    columns: [{ label: "Workload" }, { label: "Group" }, { label: "Stock", num: true, cls: "arm-baseline" }, { label: "Fork", num: true, cls: "arm-fork" }, { label: "Fork vs stock" }],
    rows: rows.map(({ w: wl }) => [wl.title, wl.group, valueText(ds().median(wl.id, metric, "baseline"), metric, true), valueText(ds().median(wl.id, metric, "fork"), metric, true), ratioCell(wl, metric)]),
  }));
}

// ------------------------------------------------------------------ (d) throughput

function renderThroughput() {
  const fig = $("#fig-throughput");
  const metric = "rps";
  const wl = requestWorkloads().filter((w) => ds().median(w.id, metric, "fork") != null || ds().median(w.id, metric, "baseline") != null);
  if (!wl.length) { show(fig, false); return; }
  show(fig, true);
  setSub(fig, "Requests per second reported by redis-benchmark, per workload and build. Higher is better. Each panel has its own scale.");
  setLegend(fig, armLegend());
  const width = figureWidth(fig);
  const body = h("div");
  for (const g of groupBy(wl, (w) => w.group)) {
    for (const facet of splitByRange(g, metric)) {
      body.append(barFacet({
        title: facet.title, note: facet.note,
        rows: facet.items.map((w) => ({
          label: w.title,
          bars: armsOf().map((a) => ({ a, v: ds().median(w.id, metric, a.label) })).filter((x) => x.v != null).map((x) => ({
            key: x.a.label, cls: D.armClass(x.a.label), value: x.v, name: D.armShort(x.a.label),
            text: valueText(x.v, metric), label: D.fmtCompact(x.v),
          })),
          tip: () => ({ rows: armTipRows(w, metric), foot: [[ratioCell(w, metric)], w.title] }),
        })),
      }, { width, fmtAxis: D.fmtAxisCompact, labelKey: "fork", axisCaption: "req/s · higher is better" }));
    }
  }
  setBody(fig, body);
  setTable(fig, armTable(wl, metric, "Throughput (req/s)"));
}

// Split a group into panels when its values span more than 20×, by client/pipeline shape.
function splitByRange(g, metric) {
  const vals = g.items.flatMap((w) => armsOf().map((a) => ds().median(w.id, metric, a.label))).filter((v) => v > 0);
  const whole = [{ title: g.name, items: g.items }];
  if (!vals.length || Math.max(...vals) / Math.min(...vals) <= 20) return whole;
  const shape = (w) => (w.info.pipeline > 1 ? `${w.info.clients} clients × ${w.info.pipeline} pipelined` : `${w.info.clients} client${w.info.clients === 1 ? "" : "s"}, no pipelining`);
  const parts = groupBy(g.items, shape);
  return parts.length > 1 ? parts.map((p) => ({ title: g.name, note: p.name, items: p.items })) : whole;
}

function armTable(wl, metric, caption) {
  const arms = armsOf();
  return dataTable({
    caption,
    columns: [{ label: "Workload" }, ...arms.map((a) => ({ label: D.armShort(a.label), num: true, cls: D.armClass(a.label) })), { label: "Fork vs stock" }],
    rows: wl.map((w) => [w.title, ...arms.map((a) => { const v = ds().median(w.id, metric, a.label); return v == null ? "–" : valueText(v, metric, true); }), ratioCell(w, metric)]),
  });
}

// ------------------------------------------------------------------ (e, f) dumbbells

function dumbbellFigure(fig, metric, sub, noteWords) {
  const m = D.metricInfo(ds(), metric);
  const wl = requestWorkloads().filter((w) => ds().median(w.id, metric, "fork") != null && ds().median(w.id, metric, "baseline") != null);
  if (!wl.length) { show(fig, false); return; }
  show(fig, true);
  setLegend(fig, legend(armsOf().map((a) => ({ cls: D.armClass(a.label), dot: true, label: D.armShort(a.label) }))));
  const groups = groupBy(wl, (w) => w.group).map((g) => ({
    name: g.name,
    rows: g.items.map((w) => {
      const sp = D.speedup(ds(), w.id, metric);
      const v = D.verdict(sp);
      const note = v === "neutral" ? "≈ same" : sp >= 1 ? `${D.formatRatio(sp)} ${noteWords[0]}` : `${D.formatRatio(1 / sp)} ${noteWords[1]}`;
      return {
        label: w.title, note,
        points: armsOf().map((a) => ({ a, v: ds().median(w.id, metric, a.label) })).filter((x) => x.v != null && x.v > 0).map((x) => ({
          key: x.a.label, cls: D.armClass(x.a.label), value: x.v, name: D.armShort(x.a.label), text: valueText(x.v, metric),
        })),
        tip: () => ({ rows: armTipRows(w, metric), foot: [[D.describe(ds(), metric, sp)], w.title] }),
      };
    }),
  }));
  const res = dumbbells(groups, { width: figureWidth(fig), fmtAxis: (t) => D.fmtNum(t), unitLabel: `${m.title} (${m.unit})` });
  setSub(fig, `${sub} Each group has its own scale${res.log ? "; a group whose values span more than 20× uses a log scale" : ""}.`);
  setBody(fig, res.node);
  setTable(fig, armTable(wl, metric, `${m.title} (${m.unit})`));
}

function renderLatency() {
  const fig = $("#fig-latency");
  const metric = state.latMetric;
  for (const b of fig.querySelectorAll(".seg button")) b.disabled = !requestWorkloads().some((w) => ds().median(w.id, b.dataset.metric, "fork") != null);
  const label = metric === "p50_ms" ? "Median (p50)" : "p99";
  dumbbellFigure(fig, metric, `${label} latency per workload, stock → fork, in ms. Lower is better.`, ["lower", "higher"]);
}

function renderCpu() {
  dumbbellFigure($("#fig-cpu"), "cpu_us_per_op", "Server CPU (user + sys) per request, stock → fork, in µs. Lower is better.", ["less", "more"]);
}

// ------------------------------------------------------------------ (g) persistence

function renderPersist() {
  const fig = $("#fig-persist");
  const bg = ds().workloads.filter((w) => w.info.cat === "bgsave").sort((a, b) => (a.info.populateMB || 0) - (b.info.populateMB || 0));
  const metrics = ["fork_ms", "bgsave_wall_ms", "child_cpu_s"].filter((m) => bg.some((w) => ds().median(w.id, m, "fork") != null || ds().median(w.id, m, "baseline") != null));
  if (!bg.length || !metrics.length) { show(fig, false); return; }
  show(fig, true);
  setSub(fig, "BGSAVE per dataset size: the fork() stall on the main thread, the total save time and the child's CPU. Lower is better. Each panel has its own scale.");
  setLegend(fig, armLegend());
  const width = figureWidth(fig);
  const body = h("div");
  const sizeLabel = (w) => (w.info.populateMB ? `${w.info.populateMB} MB dataset` : w.title);
  const usedNote = (w) => { const u = ds().median(w.id, "used_memory_mb", "fork") ?? ds().median(w.id, "used_memory_mb", "baseline"); return u != null ? `used_memory ${D.fmtNum(u)} MB` : null; };
  const tableRows = [];
  for (const metric of metrics) {
    const m = D.metricInfo(ds(), metric);
    const items = bg.filter((w) => armsOf().some((a) => ds().median(w.id, metric, a.label) != null));
    body.append(barFacet({
      title: m.title, note: m.unit,
      rows: items.map((w) => ({
        label: sizeLabel(w), note: usedNote(w),
        bars: armsOf().map((a) => ({ a, v: ds().median(w.id, metric, a.label) })).filter((x) => x.v != null).map((x) => ({
          key: x.a.label, cls: D.armClass(x.a.label), value: x.v, name: D.armShort(x.a.label), text: valueText(x.v, metric), label: valueText(x.v, metric),
        })),
        tip: () => ({ rows: armTipRows(w, metric), foot: [[ratioCell(w, metric)], `${m.title} · ${sizeLabel(w)}`] }),
      })),
    }, { width, fmtAxis: (t) => D.fmtNum(t), labelKey: "fork", axisCaption: `${m.unit} · lower is better` }));
    for (const w of items) tableRows.push([`${sizeLabel(w)} · ${m.title}`, ...armsOf().map((a) => { const v = ds().median(w.id, metric, a.label); return v == null ? "–" : valueText(v, metric); }), ratioCell(w, metric)]);
  }
  setBody(fig, body);
  setTable(fig, dataTable({
    caption: "BGSAVE by dataset size",
    columns: [{ label: "Dataset · metric" }, ...armsOf().map((a) => ({ label: D.armShort(a.label), num: true, cls: D.armClass(a.label) })), { label: "Fork vs stock" }],
    rows: tableRows,
  }));
}

// ------------------------------------------------------------------ (h) history

function renderHistory() {
  const fig = $("#fig-history");
  const tc = ds().toolchain;
  const runs = state.bench.datasets
    .filter((d) => !d.isProxy && d.toolchain === tc && D.headline(d))
    .sort((a, b) => (Date.parse(a.date) || 0) - (Date.parse(b.date) || 0));
  if (ds().isProxy || runs.length < 2) { show(fig, false); return; }
  show(fig, true);
  const metric = D.HEADLINE_METRIC;
  const m = D.metricInfo(ds(), metric);
  setSub(fig, `The headline metric across Windows CI runs on ${toolchainName(tc)}: server CPU per command of ${D.headline(runs[runs.length - 1]).workload.title}, in µs. Lower is better.`);
  const arms = D.ARM_ORDER.filter((a) => runs.some((d) => d.has(a)));
  setLegend(fig, legend(arms.map((a) => ({ cls: D.armClass(a), line: true, label: D.armShort(a) }))));
  const xs = runs.map((d, i) => ({ i, t: Number.isNaN(Date.parse(d.date)) ? null : Date.parse(d.date), label: shortDate(d.date) }));
  const series = arms.map((a) => ({
    key: a, cls: D.armClass(a), name: D.armShort(a),
    points: runs.map((d, i) => { const w = D.headline(d).workload; const v = d.median(w.id, metric, a); return v == null ? null : { i, y: v }; }).filter(Boolean),
  })).filter((se) => se.points.length);
  const svg = lineChart(xs, series, {
    width: figureWidth(fig), height: 260, fmtAxis: (t) => D.fmtNum(t),
    ariaLabel: `Server CPU per command over ${runs.length} Windows CI runs`,
    tip: (i) => {
      const d = runs[i];
      const w = D.headline(d).workload;
      const rows = arms.map((a) => ({ a, v: d.median(w.id, metric, a) })).filter((x) => x.v != null)
        .map((x) => ({ value: D.fmtValue(x.v, m.unit), label: D.armShort(x.a), cls: D.armClass(x.a) }));
      const sp = D.speedup(d, w.id, metric);
      return { rows, foot: [[D.describe(d, metric, sp)], `${d.title || d.id} · ${D.fmtDate(d.date)}${d.commit ? ` · ${d.commit}` : ""}`] };
    },
  });
  setBody(fig, svg);
  setTable(fig, dataTable({
    caption: `Server CPU per command (${m.unit}), Windows CI, ${toolchainName(tc)}`,
    columns: [{ label: "Run" }, { label: "Commit" }, ...arms.map((a) => ({ label: D.armShort(a), num: true, cls: D.armClass(a) })), { label: "Fork vs stock" }],
    rows: runs.map((d) => {
      const w = D.headline(d).workload;
      return [`${D.fmtDate(d.date)} · ${d.title || d.id}`, d.commit || "–", ...arms.map((a) => { const v = d.median(w.id, metric, a); return v == null ? "–" : D.fmtValue(v, m.unit); }), D.describe(d, metric, D.speedup(d, w.id, metric))];
    }),
  }));
}
const shortDate = (iso) => { const t = Date.parse(iso); return Number.isNaN(t) ? String(iso || "") : new Date(t).toLocaleDateString("en-GB", { month: "short", day: "numeric", timeZone: "UTC" }); };

// ------------------------------------------------------------------ (i) methodology

function renderMethod() {
  const d = ds();
  $("#env-block").hidden = false;
  const env = $("#env");
  env.replaceChildren();
  const item = (k, v) => env.append(h("div", null, h("dt", null, k), h("dd", null, v)));
  const link = (href, text) => (isHttp(href) ? h("a", { href }, text) : text);
  item("Kind", d.isProxy ? `Proxy measurement (${D.proxyBadgeText(d)})` : "Windows CI");
  item("Platform", d.platform || "–");
  item("Toolchain", d.toolchain ? toolchainName(d.toolchain) : "–");
  item("Redis", d.redis_version || "–");
  item("Commit", d.commit ? link(`${D.REPO}/commit/${encodeURIComponent(d.commit)}`, d.commit) : "–");
  item("Run", d.run_url ? link(d.run_url, "Workflow run") : "–");
  item("Date", D.fmtDate(d.date));
  item("Statistic", `Median of ${d.reps || "?"} repetition${d.reps === 1 ? "" : "s"} per arm${hasBgsave(d) ? "; BGSAVE: median of every save across those repetitions" : ""}`);
  const arms = $("#arms");
  arms.replaceChildren();
  for (const a of d.arms) arms.append(h("li", null, h("span", { class: `swatch ${D.armClass(a.label)}`, "aria-hidden": "true" }), h("strong", null, D.armShort(a.label)), h("span", null, `${a.name || a.label} (label "${a.label}")`)));
  const notes = Array.isArray(d.notes) ? d.notes.filter((n) => typeof n === "string" && n.trim()) : [];
  $("#notes-wrap").hidden = !notes.length;
  const ul = $("#notes");
  ul.replaceChildren(...notes.map((n) => h("li", null, n)));
  ul.hidden = !notes.length;
}
const hasBgsave = (d) => d.workloads.some((w) => w.info.cat === "bgsave");
const isHttp = (u) => /^https:\/\//i.test(String(u || ""));

