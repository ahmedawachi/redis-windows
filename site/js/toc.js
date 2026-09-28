// Marks the table-of-contents entry for the section being read.
export function spy(links) {
  const map = new Map();
  for (const a of links) {
    const id = decodeURIComponent((a.getAttribute("href") || "").split("#")[1] || "");
    const target = id && document.getElementById(id);
    if (target) map.set(target, a);
  }
  if (!map.size || !("IntersectionObserver" in window)) return () => {};
  const visible = new Set();
  const mark = () => {
    const first = [...map.keys()].find((t) => visible.has(t)) || null;
    for (const [t, a] of map) {
      if (t === first) a.setAttribute("aria-current", "true");
      else a.removeAttribute("aria-current");
    }
  };
  const io = new IntersectionObserver((entries) => {
    for (const e of entries) (e.isIntersecting ? visible.add(e.target) : visible.delete(e.target));
    mark();
  }, { rootMargin: "-72px 0px -55% 0px" });
  for (const t of map.keys()) io.observe(t);
  return () => io.disconnect();
}

const toc = document.querySelector(".toc[data-spy], nav.toc:not(.docs-nav)");
if (toc) {
  const sections = [...document.querySelectorAll(".prose > section[id]")];
  const links = [...toc.querySelectorAll("a[href^='#']")];
  // Spy on sections rather than headings so a long section stays marked while it is read.
  if (sections.length) {
    const byId = new Map(links.map((a) => [a.getAttribute("href").slice(1), a]));
    const visible = new Set();
    const io = new IntersectionObserver((entries) => {
      for (const e of entries) (e.isIntersecting ? visible.add(e.target) : visible.delete(e.target));
      const first = sections.find((s) => visible.has(s));
      for (const [id, a] of byId) {
        if (first && id === first.id) a.setAttribute("aria-current", "true");
        else a.removeAttribute("aria-current");
      }
    }, { rootMargin: "-72px 0px -50% 0px" });
    for (const s of sections) io.observe(s);
  }
}
