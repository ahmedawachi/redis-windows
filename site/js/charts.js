// Chart primitives: HTML bars and dumbbells positioned in percent, an SVG line chart,
// one shared tooltip, and accessible data tables. All text goes in via textContent.

const SVGNS = "http://www.w3.org/2000/svg";

export function h(tag, attrs, ...kids) {
  const e = document.createElement(tag);
  setAttrs(e, attrs);
  for (const c of kids.flat()) {
    if (c == null || c === false) continue;
    e.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return e;
}

export function s(tag, attrs) {
  const e = document.createElementNS(SVGNS, tag);
  setAttrs(e, attrs);
  return e;
}

function setAttrs(e, attrs) {
  if (!attrs) return;
  for (const [k, v] of Object.entries(attrs)) {
    if (v == null || v === false) continue;
    if (k === "class") e.setAttribute("class", v);
    else if (k === "text") e.textContent = v;
    else if (k === "style") for (const [sk, sv] of Object.entries(v)) e.style.setProperty(sk, sv);
    else e.setAttribute(k, v === true ? "" : String(v));
  }
}

// ------------------------------------------------------------------ text measuring

const measureCtx = document.createElement("canvas").getContext("2d");
const FONT_LABEL = '600 12px system-ui, -apple-system, "Segoe UI", sans-serif';
export function textWidth(str, font = FONT_LABEL) {
  if (!measureCtx) return String(str).length * 7;
  measureCtx.font = font;
  return measureCtx.measureText(String(str)).width;
}

// ------------------------------------------------------------------ tooltip

let tipEl = null;
let tipOwner = null;
let tipAnchor = null;
let tipFrame = 0;

// A pointer tooltip hides on scroll (the pointer has left it); a keyboard one follows
// its mark, because focusing a mark below the fold scrolls the page to it.
function onScroll() {
  if (!tipOwner) return;
  if (document.activeElement === tipOwner && tipAnchor instanceof Element && tipAnchor.isConnected) {
    if (!tipFrame) tipFrame = requestAnimationFrame(() => { tipFrame = 0; if (tipOwner) placeTip(); });
  } else hideTip();
}

function tipNode() {
  if (!tipEl) {
    tipEl = h("div", { class: "tip", role: "tooltip", id: "chart-tip" });
    tipEl.hidden = true;
    document.body.append(tipEl);
    document.addEventListener("keydown", (e) => { if (e.key === "Escape") hideTip(); });
    window.addEventListener("scroll", onScroll, { passive: true });
  }
  return tipEl;
}

function placeTip() {
  const t = tipEl;
  const a = tipAnchor;
  const rect = a instanceof Element ? a.getBoundingClientRect() : a;
  const tw = t.offsetWidth, th = t.offsetHeight;
  const vw = document.documentElement.clientWidth, vh = window.innerHeight;
  let top = rect.top - th - 10;
  if (top < 8) top = Math.min(rect.bottom + 10, vh - th - 8);
  let left = rect.left + rect.width / 2 - tw / 2;
  left = Math.max(8, Math.min(left, vw - tw - 8));
  t.style.left = `${Math.round(left)}px`;
  t.style.top = `${Math.round(Math.max(8, top))}px`;
}

// content: { rows: [{ value, label, cls }], foot: [string | [strong, rest]], anchor?: Element | DOMRect }
export function showTip(owner, content) {
  const t = tipNode();
  t.replaceChildren();
  for (const r of content.rows || []) {
    t.append(h("div", { class: "tip-row" },
      h("span", { class: `key-line ${r.cls || ""}`, "aria-hidden": "true" }),
      h("span", { class: "tip-val" }, r.value),
      r.label ? h("span", { class: "tip-lab" }, r.label) : null));
  }
  for (const f of content.foot || []) {
    if (Array.isArray(f)) t.append(h("div", { class: "tip-foot" }, h("strong", null, f[0]), f[1] ? ` ${f[1]}` : ""));
    else if (f) t.append(h("div", { class: "tip-foot" }, f));
  }
  t.hidden = false;
  tipOwner = owner;
  tipAnchor = content.anchor || owner;
  placeTip();
}

export function hideTip(owner) {
  if (!tipEl || (owner && owner !== tipOwner)) return;
  tipEl.hidden = true;
  tipOwner = null;
  tipAnchor = null;
}

export function attachTip(node, getContent) {
  const on = () => { node.classList.add("is-hot"); showTip(node, getContent()); };
  const off = () => { node.classList.remove("is-hot"); hideTip(node); };
  node.addEventListener("pointerenter", on);
  node.addEventListener("pointerleave", off);
  node.addEventListener("focus", on);
  node.addEventListener("blur", off);
}

// One tab stop per chart; arrow keys walk the marks. Charts re-render into the same
// container, so it gets one listener that always reads the current marks.
const rovingMarks = new WeakMap();
export function roving(container) {
  const marks = [...container.querySelectorAll("[data-mark]")];
  marks.forEach((m, i) => m.setAttribute("tabindex", i === 0 ? "0" : "-1"));
  const known = rovingMarks.has(container);
  rovingMarks.set(container, marks);
  if (known) return;
  container.addEventListener("keydown", (e) => {
    const marks = rovingMarks.get(container) || [];
    const i = marks.indexOf(document.activeElement);
    if (i < 0) return;
    let j = null;
    if (e.key === "ArrowDown" || e.key === "ArrowRight") j = Math.min(marks.length - 1, i + 1);
    else if (e.key === "ArrowUp" || e.key === "ArrowLeft") j = Math.max(0, i - 1);
    else if (e.key === "Home") j = 0;
    else if (e.key === "End") j = marks.length - 1;
    if (j == null) return;
    e.preventDefault();
    marks[i].setAttribute("tabindex", "-1");
    marks[j].setAttribute("tabindex", "0");
    marks[j].focus();
  });
}

// ------------------------------------------------------------------ scales and ticks

function niceStep(range, count) {
  const raw = range / Math.max(1, count);
  const p = 10 ** Math.floor(Math.log10(raw));
  const f = raw / p;
  const n = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
  return n * p;
}

export function tickCount(width) {
  return Math.max(2, Math.min(6, Math.floor(width / 110)));
}

export function linearScale(maxValue, width, fmt = (v) => String(v)) {
  const max = maxValue > 0 ? maxValue : 1;
  const base = tickCount(width);
  let pick = null;
  for (let n = Math.max(2, base - 1); n <= 8; n++) {
    const step = niceStep(max, n);
    const top = Math.ceil(max / step - 1e-9) * step;
    const count = Math.round(top / step) + 1;
    const labelPx = Math.max(72, Math.max(...[0, step, top].map((v) => textWidth(fmt(v), TICK_FONT))) + 40);
    if (count * labelPx > width) break;
    pick = { step, top };
    if (top / max <= 1.45) break;
  }
  if (!pick) { const step = niceStep(max, 2); pick = { step, top: Math.ceil(max / step - 1e-9) * step }; }
  const { step, top } = pick;
  const ticks = [];
  for (let v = 0; v <= top + step / 2; v += step) ticks.push(+v.toPrecision(12));
  return { ticks, pos: (v) => (Math.max(0, v) / top) * 100, log: false, min: 0, max: top };
}
const TICK_FONT = '12px system-ui, -apple-system, "Segoe UI", sans-serif';

export function logScale(minValue, maxValue, width) {
  const steps = [1, 2, 5];
  const lo = floorNice(minValue), hi = ceilNice(maxValue);
  const all = [];
  for (let e = Math.floor(Math.log10(lo)) - 1; e <= Math.ceil(Math.log10(hi)) + 1; e++) {
    for (const m of steps) {
      const v = +(m * 10 ** e).toPrecision(12);
      if (v >= lo * 0.999 && v <= hi * 1.001) all.push(v);
    }
  }
  const room = tickCount(width) + 1;
  let ticks = all;
  if (ticks.length > room) ticks = all.filter((v) => Math.abs(Math.log10(v) - Math.round(Math.log10(v))) < 1e-9);
  if (ticks.length < 2) ticks = [lo, hi];
  if (ticks.length > room) { const k = Math.ceil(ticks.length / room); ticks = ticks.filter((_, i) => i % k === 0 || i === ticks.length - 1); }
  const L0 = Math.log10(lo), L1 = Math.log10(hi);
  return { ticks, pos: (v) => ((Math.log10(Math.max(v, lo)) - L0) / (L1 - L0)) * 100, log: true, min: lo, max: hi };
}
function floorNice(v) { const e = 10 ** Math.floor(Math.log10(v)); const m = v / e; return (m >= 5 ? 5 : m >= 2 ? 2 : 1) * e; }
function ceilNice(v) { const e = 10 ** Math.floor(Math.log10(v)); const m = v / e; return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * e; }

function gridlines(ticks, pos, extra = []) {
  const g = h("div", { class: "gridlines", "aria-hidden": "true" });
  for (const t of ticks) g.append(h("i", { style: { left: `${pos(t)}%` } }));
  for (const x of extra) g.append(h("i", { class: x.cls, style: { left: `${x.at}%` } }));
  return g;
}

function axisRow(ticks, pos, fmt) {
  const a = h("div", { class: "axis-row", "aria-hidden": "true" });
  for (const t of ticks) {
    const p = pos(t);
    a.append(h("span", { class: p <= 0.5 ? "first" : p >= 99.5 ? "last" : null, style: { left: `${p}%` } }, fmt(t)));
  }
  return a;
}

// ------------------------------------------------------------------ grouped horizontal bars

// facet: { title, note, rows: [{ label, note, bars: [{ key, cls, value, text, name }], tip(bar) }] }
// opts: { width, fmtAxis, labelKey (the one arm that gets a direct label) }
export function barFacet(facet, opts) {
  const scale = linearScale(Math.max(...facet.rows.flatMap((r) => r.bars.map((b) => b.value))), opts.width, opts.fmtAxis);
  const box = h("div", { class: "facet" });
  if (facet.title) box.append(h("p", { class: "facet-title" }, facet.title, facet.note ? h("span", null, ` · ${facet.note}`) : null));
  const rows = h("div", { class: "rows" });
  rows.append(gridlines(scale.ticks, scale.pos));
  for (const r of facet.rows) {
    const row = h("div", { class: "row" });
    row.append(h("div", { class: "row-head" }, h("span", { class: "row-label" }, r.label), r.note ? h("span", { class: "row-note" }, r.note) : null));
    const track = h("div", { class: "track" });
    for (const b of r.bars) {
      const w = scale.pos(b.value);
      const hit = h("div", { class: `hit ${b.cls}`, "data-mark": "", role: "img", "aria-label": `${r.label}, ${b.name}: ${b.text}` });
      const bar = h("div", { class: "bar", style: { left: "0", width: `${Math.max(w, 0.4)}%` } });
      hit.append(bar);
      if (b.key === opts.labelKey) {
        const lw = textWidth(b.label || b.text) + 8;
        const trackPx = opts.width;
        const endPx = (w / 100) * trackPx;
        if (endPx + lw + 4 <= trackPx) hit.append(h("span", { class: "bar-label", style: { left: `calc(${w}% + 6px)` } }, b.label || b.text));
        else if (lw + 8 <= endPx) hit.append(h("span", { class: "bar-label inside", style: { right: `calc(${100 - w}% + 6px)` } }, b.label || b.text));
      }
      attachTip(hit, () => ({ ...r.tip(b), anchor: bar }));
      track.append(hit);
    }
    row.append(track);
    rows.append(row);
  }
  box.append(rows, axisRow(scale.ticks, scale.pos, (v) => opts.fmtAxis(v, scale.max)));
  if (opts.axisCaption) box.append(h("div", { class: "axis-caption" }, opts.axisCaption));
  return box;
}

// ------------------------------------------------------------------ diverging ratio bars

const DIV_STOPS = [1.1, 1.25, 1.5, 2, 2.5, 3, 4, 5, 10, 20, 50, 100];

// groups: [{ name, rows: [{ label, s, verdict, text, tip() }] }]; s > 1 = fork better (right).
export function divergingBars(groups, opts) {
  const all = groups.flatMap((g) => g.rows.map((r) => r.s));
  const maxAbs = Math.max(...all.map((v) => Math.abs(Math.log2(v))), Math.log2(1.1));
  const M = DIV_STOPS.find((v) => Math.log2(v) >= maxAbs * 1.12) || DIV_STOPS[DIV_STOPS.length - 1];
  const LM = Math.log2(M);
  const pos = (v) => 50 + (Math.log2(v) / LM) * 50;
  const inner = DIV_STOPS.filter((v) => v < M);
  const want = opts.width < 420 ? 0 : opts.width < 700 ? 1 : 2;
  const targets = want === 1 ? [0.5] : want === 2 ? [1 / 3, 2 / 3] : [];
  const mids = [...new Set(targets.map((f) => inner.reduce((best, v) => (Math.abs(Math.log2(v) / LM - f) < Math.abs(Math.log2(best) / LM - f) ? v : best), inner[0])).filter(Boolean))].sort((a, b) => a - b);
  const ticks = [1 / M, ...mids.map((v) => 1 / v).reverse(), 1, ...mids, M];
  const fmtTick = (v) => (v === 1 ? "1×" : v > 1 ? `${trim(v)}×` : `${trim(v)}×`);

  const box = h("div", { class: "facet" });
  const rows = h("div", { class: "rows" });
  rows.append(gridlines(ticks.filter((t) => t !== 1), pos, [{ cls: "mid", at: 50 }]));
  const labelled = new Set(opts.labelRows || []);
  for (const g of groups) {
    if (g.name) rows.append(h("div", { class: "grp" }, g.name));
    for (const r of g.rows) {
      const row = h("div", { class: "row" });
      row.append(h("div", { class: "row-head" }, h("span", { class: "row-label" }, r.label)));
      const track = h("div", { class: "track" });
      const p = pos(r.s);
      const right = r.s >= 1;
      const hit = h("div", { class: `hit dir-${r.verdict}`, "data-mark": "", role: "img", "aria-label": `${r.label}: ${r.text}` });
      const bar = h("div", {
        class: `bar${right ? "" : " neg"}`,
        style: right ? { left: "50%", width: `${Math.max(p - 50, 0.4)}%` } : { left: `${p}%`, width: `${Math.max(50 - p, 0.4)}%` },
      });
      hit.append(bar);
      if (labelled.has(r)) {
        const txt = r.short;
        const lw = textWidth(txt) + 8;
        const px = (v) => (v / 100) * opts.width;
        const barPx = px(Math.abs(p - 50));
        if (right && px(100 - p) >= lw + 4) hit.append(h("span", { class: "bar-label", style: { left: `calc(${p}% + 6px)` } }, txt));
        else if (!right && px(p) >= lw + 4) hit.append(h("span", { class: "bar-label", style: { right: `calc(${100 - p}% + 6px)` } }, txt));
        else if (barPx >= lw + 8 && r.verdict !== "neutral") hit.append(h("span", { class: "bar-label inside", style: right ? { right: `calc(${100 - p}% + 6px)` } : { left: `calc(${p}% + 6px)` } }, txt));
      }
      attachTip(hit, () => ({ ...r.tip(), anchor: bar }));
      track.append(hit);
      row.append(track);
      rows.append(row);
    }
  }
  box.append(rows, axisRow(ticks, pos, fmtTick));
  box.append(h("div", { class: "axis-caption" }, "← stock better · fork ÷ stock, log scale · fork better →"));
  return box;
}
const trim = (v) => (v >= 10 ? String(Math.round(v)) : String(+v.toFixed(2)));

// ------------------------------------------------------------------ dumbbells

// groups: [{ name, rows: [{ label, note, points: [{ key, cls, value, name, text }], tip(point) }] }]
// One panel per group, each with its own scale (log when that panel spans more than 20×).
// A connector runs from the baseline point to the fork point.
export function dumbbells(groups, opts) {
  const wrap = h("div");
  let anyLog = false;
  groups.forEach((g, gi) => {
    const vals = g.rows.flatMap((r) => r.points.map((p) => p.value)).filter((v) => v > 0);
    if (!vals.length) return;
    const lo = Math.min(...vals), hi = Math.max(...vals);
    const useLog = hi / lo > 20;
    anyLog = anyLog || useLog;
    const scale = useLog ? logScale(lo, hi, opts.width) : linearScale(hi, opts.width, opts.fmtAxis);
    const box = h("div", { class: "facet" });
    if (g.name) box.append(h("p", { class: "facet-title" }, g.name, useLog ? h("span", null, " · log scale") : null));
    const rows = h("div", { class: "rows" });
    rows.append(gridlines(scale.ticks, scale.pos));
    for (const r of g.rows) {
      const row = h("div", { class: "row" });
      row.append(h("div", { class: "row-head" }, h("span", { class: "row-label" }, r.label), r.note ? h("span", { class: "row-note" }, r.note) : null));
      const track = h("div", { class: "db-track" });
      const b = r.points.find((p) => p.key === "baseline");
      const f = r.points.find((p) => p.key === "fork");
      if (b && f) {
        const a = scale.pos(b.value), c = scale.pos(f.value);
        track.append(h("div", { class: "db-line", "aria-hidden": "true", style: { left: `${Math.min(a, c)}%`, width: `${Math.abs(c - a)}%` } }));
      }
      // Draw the fork last so it sits on top when points coincide.
      const order = [...r.points].sort((x, y) => (x.key === "fork") - (y.key === "fork"));
      for (const p of order) {
        const dot = h("div", { class: `db-dot ${p.cls}`, "data-mark": "", role: "img", "aria-label": `${r.label}, ${p.name}: ${p.text}`, style: { left: `${scale.pos(p.value)}%` } });
        attachTip(dot, () => r.tip(p));
        track.append(dot);
      }
      row.append(track);
      rows.append(row);
    }
    box.append(rows, axisRow(scale.ticks, scale.pos, opts.fmtAxis));
    if (gi === groups.length - 1) box.append(h("div", { class: "axis-caption" }, `${opts.unitLabel} · lower is better`));
    wrap.append(box);
  });
  return { node: wrap, log: anyLog };
}

// ------------------------------------------------------------------ line chart

// series: [{ key, cls, name, points: [{ i, y }] }]; xs: [{ i, t (ms or null), label }]
export function lineChart(xs, series, opts) {
  const W = Math.max(280, opts.width);
  const H = opts.height || 260;
  const maxY = Math.max(...series.flatMap((se) => se.points.map((p) => p.y)));
  const yScale = linearScale(maxY, H * 1.4, opts.fmtAxis);
  const yTicks = yScale.ticks;
  const yLab = yTicks.map((t) => opts.fmtAxis(t));
  const left = Math.ceil(Math.max(...yLab.map((l) => textWidth(l, '12px system-ui, -apple-system, "Segoe UI", sans-serif')))) + 12;
  const endLab = series.map((se) => se.name);
  const right = Math.min(120, Math.ceil(Math.max(...endLab.map((l) => textWidth(l))) + 16));
  const top = 12, bottom = 28;
  const pw = W - left - right, ph = H - top - bottom;
  const times = xs.map((x) => x.t);
  const useTime = times.every((t) => t != null) && new Set(times).size === times.length;
  const t0 = Math.min(...times), t1 = Math.max(...times);
  const X = (i) => {
    if (xs.length === 1) return left + pw / 2;
    if (useTime) return left + ((xs[i].t - t0) / (t1 - t0)) * pw;
    return left + (i / (xs.length - 1)) * pw;
  };
  const Y = (v) => top + ph - (v / yScale.max) * ph;

  const svg = s("svg", { class: "line-svg", viewBox: `0 0 ${W} ${H}`, width: W, height: H, role: "group", "aria-label": opts.ariaLabel });
  for (let k = 0; k < yTicks.length; k++) {
    const y = Math.round(Y(yTicks[k])) + 0.5;
    svg.append(s("line", { class: yTicks[k] === 0 ? "ax" : "gl", x1: left, x2: left + pw, y1: y, y2: y }));
    const t = s("text", { class: "tk", x: left - 8, y: y + 4, "text-anchor": "end" });
    t.textContent = yLab[k];
    svg.append(t);
  }
  // x labels: first, last, and as many between as fit.
  const maxLabels = Math.max(2, Math.floor(pw / 90));
  const every = Math.ceil(xs.length / maxLabels);
  xs.forEach((x, i) => {
    if (!(i === 0 || i === xs.length - 1 || i % every === 0)) return;
    if (i !== xs.length - 1 && xs.length - 1 - i < every && i !== 0) return;
    const anchor = xs.length === 1 ? "middle" : i === 0 ? "start" : i === xs.length - 1 ? "end" : "middle";
    const t = s("text", { class: "tk", x: X(i), y: H - 8, "text-anchor": anchor });
    t.textContent = x.label;
    svg.append(t);
  });
  const xh = s("line", { class: "xh", y1: top, y2: top + ph, x1: -10, x2: -10, visibility: "hidden" });
  svg.append(xh);
  const dots = [];
  for (const se of series) {
    const g = s("g", { class: se.cls });
    const d = se.points.map((p, n) => `${n ? "L" : "M"}${X(p.i).toFixed(1)} ${Y(p.y).toFixed(1)}`).join(" ");
    g.append(s("path", { class: "series", d }));
    for (const p of se.points) {
      const c = s("circle", { class: "pt", cx: X(p.i), cy: Y(p.y), r: 4.5 });
      dots.push({ c, i: p.i });
      g.append(c);
    }
    const last = se.points[se.points.length - 1];
    if (last) {
      const lab = s("text", { class: "end-label", x: X(last.i) + 10, y: Y(last.y) + 4 });
      lab.textContent = se.name;
      g.append(lab);
    }
    svg.append(g);
  }
  // De-collide end labels: if two are closer than 14px, drop the lower-priority ones (legend + tooltip carry them).
  const labels = [...svg.querySelectorAll(".end-label")];
  const ys = labels.map((l) => +l.getAttribute("y"));
  for (let a = 0; a < labels.length; a++) for (let b = a + 1; b < labels.length; b++) if (Math.abs(ys[a] - ys[b]) < 14) labels[b].remove();

  const overlay = s("rect", { class: "overlay", x: left, y: top, width: pw, height: ph, tabindex: "0", role: "img", "aria-label": `${opts.ariaLabel}. Use the arrow keys to step through runs.` });
  svg.append(overlay);
  let cur = -1;
  const focusAt = (i) => {
    cur = i;
    const x = X(i);
    xh.setAttribute("x1", x); xh.setAttribute("x2", x); xh.setAttribute("visibility", "visible");
    for (const d of dots) d.c.classList.toggle("is-hot", d.i === i);
    const box = svg.getBoundingClientRect();
    const scaleX = box.width / W, scaleY = box.height / H;
    const ymin = Math.min(...series.flatMap((se) => se.points.filter((p) => p.i === i).map((p) => Y(p.y))));
    showTip(overlay, { ...opts.tip(i), anchor: { left: box.left + x * scaleX, top: box.top + ymin * scaleY - 6, width: 0, height: 0, bottom: box.top + ymin * scaleY } });
  };
  const clear = () => { cur = -1; xh.setAttribute("visibility", "hidden"); for (const d of dots) d.c.classList.remove("is-hot"); hideTip(overlay); };
  const nearest = (clientX) => {
    const box = svg.getBoundingClientRect();
    const x = ((clientX - box.left) / box.width) * W;
    let best = 0, bd = Infinity;
    xs.forEach((_, i) => { const dd = Math.abs(X(i) - x); if (dd < bd) { bd = dd; best = i; } });
    return best;
  };
  overlay.addEventListener("pointermove", (e) => { const i = nearest(e.clientX); if (i !== cur) focusAt(i); });
  overlay.addEventListener("pointerleave", clear);
  overlay.addEventListener("focus", () => focusAt(cur >= 0 ? cur : xs.length - 1));
  overlay.addEventListener("blur", clear);
  overlay.addEventListener("keydown", (e) => {
    let i = null;
    if (e.key === "ArrowRight") i = Math.min(xs.length - 1, cur + 1);
    else if (e.key === "ArrowLeft") i = Math.max(0, cur - 1);
    else if (e.key === "Home") i = 0;
    else if (e.key === "End") i = xs.length - 1;
    if (i == null) return;
    e.preventDefault();
    focusAt(i);
  });
  return svg;
}

// ------------------------------------------------------------------ tables

// columns: [{ label, num, cls }] (cls adds a series swatch), rows: [[cell, ...]]
export function dataTable({ caption, columns, rows }) {
  const t = h("table", { class: "data-table" });
  if (caption) t.append(h("caption", null, caption));
  const hr = h("tr");
  for (const c of columns) {
    hr.append(h("th", { scope: "col", class: c.num ? "num" : null },
      c.cls ? h("span", { class: "th-arm" }, h("span", { class: `swatch ${c.cls}`, "aria-hidden": "true" }), c.label) : c.label));
  }
  t.append(h("thead", null, hr));
  const tb = h("tbody");
  for (const r of rows) {
    const tr = h("tr");
    r.forEach((cell, i) => {
      if (i === 0) tr.append(h("th", { scope: "row" }, cell));
      else tr.append(h("td", { class: columns[i] && columns[i].num ? "num" : null }, cell));
    });
    tb.append(tr);
  }
  t.append(tb);
  return t;
}

export function legend(items) {
  const ul = h("ul", { class: "chart-legend", "aria-label": "Legend" });
  for (const it of items) {
    ul.append(h("li", null, h("span", { class: `${it.line ? "key-line" : `swatch${it.dot ? " dot" : ""}`} ${it.cls}`, "aria-hidden": "true" }), it.label));
  }
  return ul;
}
