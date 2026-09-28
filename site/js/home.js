// Landing page: the headline stat row, read from data/bench.json. Hidden when there is no data.
import * as D from "./data.js";
import { h } from "./charts.js";
import { statTile, headlineTile } from "./tiles.js";

(async () => {
  const bench = await D.loadBench("data/bench.json");
  if (bench.status !== "ok") return;
  const ds = bench.datasets[0];
  measuredSentence(ds);
  const tiles = [];
  const hl = headlineTile(ds);
  if (hl) tiles.push(hl);
  for (const k of D.kpis(ds)) if (tiles.length < 4) tiles.push(statTile(k));
  if (!tiles.length) return;
  const row = document.getElementById("hero-stats");
  row.replaceChildren(...tiles);
  const meta = document.getElementById("hero-stats-meta");
  meta.replaceChildren(
    ds.isProxy ? h("span", { class: "badge proxy" }, D.proxyBadgeText(ds)) : h("span", { class: "badge ci" }, "Windows CI"),
    h("span", null, [ds.platform, ds.toolchain && ds.toolchain !== "host" ? ds.toolchain.toUpperCase() : null, D.fmtDate(ds.date), ds.reps ? `median of ${ds.reps}` : null].filter(Boolean).join(" · ")),
    h("a", { href: `benchmarks.html?dataset=${encodeURIComponent(ds.id)}` }, "All benchmarks →"),
  );
  if (ds.isProxy) meta.append(h("span", null, "The first Windows benchmark run is pending."));
  document.getElementById("hero-stats-wrap").hidden = false;
})();

// The "Optimized build" card's measured figures, from the same dataset as the stat row.
function measuredSentence(ds) {
  const slot = document.getElementById("opt-measured");
  if (!slot) return;
  const parts = [];
  const hl = D.headline(ds);
  if (hl) parts.push([D.describe(ds, hl.metric, hl.s), ` on ${hl.workload.title}`]);
  const bg = ds.workloads.filter((w) => w.info.cat === "bgsave" && D.speedup(ds, w.id, "bgsave_wall_ms") != null)
    .sort((a, b) => (b.info.populateMB || 0) - (a.info.populateMB || 0))[0];
  if (bg) parts.push([D.describe(ds, "bgsave_wall_ms", D.speedup(ds, bg.id, "bgsave_wall_ms")), bg.info.populateMB ? ` with a ${bg.info.populateMB} MB dataset` : ""]);
  if (!parts.length) return;
  const where = ds.isProxy ? `the latest ${/mac/i.test(ds.platform || "") ? "macOS " : ""}proxy run (not Windows)` : "the latest Windows CI run";
  slot.replaceChildren(` In ${where}, the fork showed `);
  parts.forEach(([strong, rest], i) => {
    if (i) slot.append(" and ");
    slot.append(h("strong", null, strong), rest);
  });
  slot.append(".");
  slot.hidden = false;
}
