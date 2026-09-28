// Stat tiles shared by the landing page and the benchmarks page.
import * as D from "./data.js";
import { h } from "./charts.js";

const ICON_UP = '<svg viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M6 10V2M2.5 5.5 6 2l3.5 3.5"/></svg>';
const ICON_DOWN = '<svg viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M6 2v8M2.5 6.5 6 10l3.5-3.5"/></svg>';
const ICON_EQ = '<svg viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="M2.5 4.5h7M2.5 7.5h7"/></svg>';

// k: an entry from D.kpis(). The value is the fork's; the delta is fork vs stock,
// its arrow is the direction the number moved and its colour whether that is good.
export function statTile(k) {
  const tile = h("div", { class: "stat" });
  tile.append(h("span", { class: "stat-label" }, k.label, h("span", { class: "visually-hidden" }, ", fork build")));
  const [forkText, stockText] = k.metric === "rps" ? [D.fmtCompact(k.fork), D.fmtCompact(k.baseline)] : D.fmtPair(k.fork, k.baseline);
  tile.append(h("span", { class: "stat-value" }, forkText, k.unit ? h("span", { class: "unit" }, k.unit) : null));
  const d = h("span", { class: `stat-delta ${k.verdict}` });
  const dir = h("span", { class: "dir", "aria-hidden": "true" });
  dir.innerHTML = k.verdict === "neutral" ? ICON_EQ : k.fork > k.baseline ? ICON_UP : ICON_DOWN;
  d.append(dir, h("span", null, k.text));
  tile.append(d);
  tile.append(h("span", { class: "stat-vs" }, `Fork shown · stock ${stockText}${k.unit ? ` ${k.unit}` : ""}`));
  return tile;
}

// The headline as a tile: "N× less CPU per command".
export function headlineTile(ds) {
  const hl = D.headline(ds);
  if (!hl) return null;
  const tile = h("div", { class: "stat" });
  tile.append(h("span", { class: "stat-label" }, "Server CPU per command"));
  const factor = hl.s >= 1 ? hl.s : 1 / hl.s;
  tile.append(h("span", { class: "stat-value" }, D.formatRatio(factor), h("span", { class: "unit" }, hl.s >= 1 ? "less" : "more")));
  const v = D.verdict(hl.s);
  const d = h("span", { class: `stat-delta ${v}` });
  const dir = h("span", { class: "dir", "aria-hidden": "true" });
  dir.innerHTML = v === "neutral" ? ICON_EQ : hl.fork > hl.baseline ? ICON_UP : ICON_DOWN;
  const [f, b] = D.fmtPair(hl.fork, hl.baseline);
  d.append(dir, h("span", null, `${f} µs vs ${b} µs`));
  tile.append(d, h("span", { class: "stat-vs" }, hl.workload.title));
  return tile;
}
