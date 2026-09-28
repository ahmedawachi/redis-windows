/* Theme (system / light / dark) and the small-screen menu.
   Loaded in <head> without defer so the stored theme applies before first paint. */
(function () {
  var KEY = "rw-theme";
  var root = document.documentElement;

  function read() {
    try {
      var v = window.localStorage.getItem(KEY);
      return v === "light" || v === "dark" ? v : "system";
    } catch (e) {
      return "system";
    }
  }
  function write(v) {
    try {
      if (v === "system") window.localStorage.removeItem(KEY);
      else window.localStorage.setItem(KEY, v);
    } catch (e) { /* storage unavailable: the choice lasts for this page only */ }
  }
  function apply(v) {
    if (v === "light" || v === "dark") root.setAttribute("data-theme", v);
    else root.removeAttribute("data-theme");
  }

  apply(read());

  var ICONS = {
    system: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/></svg>',
    light: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/></svg>',
    dark: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/></svg>'
  };
  var NAMES = { system: "System", light: "Light", dark: "Dark" };
  var NEXT = { system: "light", light: "dark", dark: "system" };

  function paint(btn, v) {
    btn.innerHTML = ICONS[v] + '<span class="theme-label"></span>';
    btn.querySelector(".theme-label").textContent = NAMES[v];
    btn.setAttribute("aria-label", "Theme: " + NAMES[v] + ". Switch to " + NAMES[NEXT[v]]);
    btn.title = "Theme: " + NAMES[v];
  }

  function init() {
    var btn = document.querySelector("[data-theme-toggle]");
    if (btn) {
      paint(btn, read());
      btn.addEventListener("click", function () {
        var v = NEXT[read()];
        write(v);
        apply(v);
        paint(btn, v);
        document.dispatchEvent(new CustomEvent("themechange", { detail: v }));
      });
    }
    var menu = document.querySelector("[data-menu-toggle]");
    var nav = document.getElementById("site-nav");
    if (menu && nav) {
      menu.addEventListener("click", function () {
        var open = nav.classList.toggle("is-open");
        menu.setAttribute("aria-expanded", open ? "true" : "false");
      });
      document.addEventListener("keydown", function (e) {
        if (e.key === "Escape" && nav.classList.contains("is-open")) {
          nav.classList.remove("is-open");
          menu.setAttribute("aria-expanded", "false");
          menu.focus();
        }
      });
    }
    // Copy buttons on code blocks.
    Array.prototype.forEach.call(document.querySelectorAll("[data-copy]"), function (b) {
      if (!navigator.clipboard) { b.hidden = true; return; }
      b.addEventListener("click", function () {
        var pre = b.parentNode.querySelector("pre");
        if (!pre) return;
        navigator.clipboard.writeText(pre.textContent.replace(/\n$/, "")).then(function () {
          b.textContent = "Copied";
          setTimeout(function () { b.textContent = "Copy"; }, 1600);
        }, function () {});
      });
    });
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
  else init();
})();
