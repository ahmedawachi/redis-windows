// Renders the repository's Markdown docs (copied next to the site as docs/*.md) with
// marked + DOMPurify, both loaded from jsDelivr with Subresource Integrity.
import { spy } from "./toc.js";

const REPO = "https://github.com/ahmedawachi/redis-windows";
const DOCS = [
  { key: "operations", file: "docs/OPERATIONS.md", repo: "docs/OPERATIONS.md", title: "Operations", blurb: "Install, upgrade and rollback, monitoring, alerting, incident checklist" },
  { key: "roadmap", file: "docs/ROADMAP.md", repo: "docs/ROADMAP.md", title: "Roadmap", blurb: "Every change with its reasoning, what's deferred" },
  { key: "patches", file: "docs/patches.md", repo: "patches/redis/README.md", title: "Patches", blurb: "What each patch does, its config and INFO fields, how to test it" },
  { key: "licensing", file: "docs/LICENSING.md", repo: "docs/LICENSING.md", title: "Licensing", blurb: "This repo, Redis 8, and the bundled runtime" },
];

const $ = (s) => document.querySelector(s);
const el = (tag, attrs = {}, text) => {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) if (v != null) e.setAttribute(k, v);
  if (text != null) e.textContent = text;
  return e;
};

const params = new URLSearchParams(location.search);
const current = DOCS.find((d) => d.key === params.get("doc")) || DOCS[0];

buildNav();
render();

function buildNav() {
  const ul = $("#doc-list");
  for (const d of DOCS) {
    const li = el("li");
    const a = el("a", { href: `docs.html?doc=${d.key}` });
    a.append(document.createTextNode(d.title));
    a.append(el("small", {}, d.blurb));
    if (d === current) a.setAttribute("aria-current", "true");
    li.append(a);
    ul.append(li);
  }
}

async function render() {
  const out = $("#doc");
  const status = (msg) => {
    out.replaceChildren();
    const box = el("div", { class: "card doc-status" });
    box.append(el("p", {}, msg));
    const p = el("p");
    const a = el("a", { href: `${REPO}/blob/main/${current.repo}` }, `Read ${current.repo} on GitHub`);
    p.append(a);
    box.append(p);
    out.append(box);
  };
  if (!window.marked || !window.DOMPurify) { status("The Markdown renderer could not be loaded."); return; }
  let md;
  try {
    const res = await fetch(current.file, { cache: "no-cache" });
    if (!res.ok) throw new Error(String(res.status));
    md = await res.text();
  } catch (e) {
    status("This document is not available on the site right now.");
    return;
  }
  const html = window.marked.parse(md, { gfm: true });
  const clean = window.DOMPurify.sanitize(html, { USE_PROFILES: { html: true } });
  const tpl = document.createElement("template");
  tpl.innerHTML = clean;
  const frag = tpl.content;
  anchorHeadings(frag);
  rewriteLinks(frag);
  labelCode(frag);
  out.replaceChildren(frag);
  out.setAttribute("aria-busy", "false");

  const h1 = out.querySelector("h1");
  document.title = `${h1 ? h1.textContent.replace(/#$/, "").trim() : current.title} · redis-windows docs`;

  const heads = $("#doc-headings");
  heads.replaceChildren();
  for (const h of out.querySelectorAll("h2")) {
    const li = el("li");
    li.append(el("a", { href: `#${h.id}` }, h.textContent.replace(/#$/, "").trim()));
    heads.append(li);
  }
  $("#headings-block").hidden = !heads.children.length;
  spy([...heads.querySelectorAll("a")]);

  if (location.hash) {
    const t = document.getElementById(decodeURIComponent(location.hash.slice(1)));
    if (t) requestAnimationFrame(() => t.scrollIntoView());
  }
}

function slug(text) {
  return text.trim().toLowerCase().replace(/[^\p{L}\p{N}\s_-]/gu, "").replace(/\s/g, "-");
}

function anchorHeadings(root) {
  const used = new Map();
  for (const h of root.querySelectorAll("h1, h2, h3, h4")) {
    let id = slug(h.textContent) || "section";
    const n = used.get(id) || 0;
    used.set(id, n + 1);
    if (n) id = `${id}-${n}`;
    h.id = id;
    const a = el("a", { class: "anchor", href: `#${id}`, "aria-label": `Link to ${h.textContent.trim()}` }, "#");
    h.append(a);
  }
}

function rewriteLinks(root) {
  const base = `https://repo.invalid/${current.repo}`;
  for (const a of root.querySelectorAll("a[href]")) {
    const href = a.getAttribute("href");
    if (href.startsWith("#")) continue;
    if (/^(https?:|mailto:)/i.test(href)) { a.setAttribute("rel", "noopener"); continue; }
    let url;
    try { url = new URL(href, base); } catch (e) { continue; }
    if (url.host !== "repo.invalid") continue;
    const path = url.pathname.replace(/^\//, "");
    const doc = DOCS.find((d) => d.repo === path);
    a.setAttribute("href", doc ? `docs.html?doc=${doc.key}${url.hash}` : `${REPO}/blob/main/${path}${url.hash}`);
  }
  for (const img of root.querySelectorAll("img[src]")) {
    const src = img.getAttribute("src");
    if (/^https?:/i.test(src)) continue;
    try {
      const url = new URL(src, base);
      img.setAttribute("src", `${REPO}/raw/main/${url.pathname.replace(/^\//, "")}`);
    } catch (e) { img.remove(); }
  }
}

function labelCode(root) {
  for (const code of root.querySelectorAll("pre > code[class*='language-']")) {
    const m = code.className.match(/language-([\w+-]+)/);
    if (m) code.parentElement.prepend(el("span", { class: "code-lang", "aria-hidden": "true" }, m[1]));
  }
}
