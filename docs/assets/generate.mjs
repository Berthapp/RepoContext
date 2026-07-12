// Generates docs/assets/token-savings*.svg and demo.svg from the measured
// numbers in docs/token-savings.md and real captured CLI output.
// Re-run after re-measuring:  node docs/assets/generate.mjs
import { writeFileSync } from "node:fs";

const esc = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
// SVG text collapses leading whitespace; non-breaking spaces survive every renderer.
const mono_esc = (s) => esc(s).replace(/^ +/, (m) => " ".repeat(m.length));

// ---------------------------------------------------------------- bar chart
// Measured numbers from docs/token-savings.md (o200k BPE, this repo, 75 files).
const rows = [
  { label: "repoctx context (pointers), then read the top-3 files", v: 6222, kind: "base" },
  { label: "read the 3 relevant files directly (grep workflow)", v: 5336, kind: "base" },
  { label: "repoctx context --detail outline --budget-tokens 2000 · 7 files surveyed", v: 2151, kind: "hero" },
  { label: "repoctx context --detail slices --budget-tokens 2000 · content included", v: 2110, kind: "hero", delta: "−66 %" },
];

function chart(mode) {
  const t = mode === "dark"
    ? { surface: "#1a1a19", ink: "#ffffff", ink2: "#c3c2b7", muted: "#898781",
        base: "#565550", hero: "#3987e5", axis: "#383835", good: "#0ca30c", ring: "rgba(255,255,255,0.10)" }
    : { surface: "#fcfcfb", ink: "#0b0b0b", ink2: "#52514e", muted: "#898781",
        base: "#b5b3ab", hero: "#2a78d6", axis: "#c3c2b7", good: "#006300", ring: "rgba(11,11,11,0.10)" };

  const W = 880, M = 32, barH = 22, rowGap = 58, labelDy = -8;
  const x0 = M, maxW = W - M * 2 - 88;
  const px = maxW / 6222;
  const top = 122;
  const H = top + rows.length * rowGap + 26;

  let bars = "";
  rows.forEach((r, i) => {
    const y = top + i * rowGap;
    const w = Math.round(r.v * px * 10) / 10;
    const c = r.kind === "hero" ? t.hero : t.base;
    // flat left end on the baseline, 4px rounded data-end on the right
    const path = `M${x0},${y} h${w - 4} a4,4 0 0 1 4,4 v${barH - 8} a4,4 0 0 1 -4,4 h-${w - 4} Z`;
    bars += `<text x="${x0}" y="${y + labelDy}" fill="${t.ink2}" font-size="13">${esc(r.label)}</text>\n`;
    bars += `<path d="${path}" fill="${c}"/>\n`;
    const val = r.v.toLocaleString("en-US");
    bars += `<text x="${x0 + w + 10}" y="${y + barH / 2 + 4.5}" fill="${t.ink}" font-size="13.5" font-weight="600">${val}</text>\n`;
    if (r.delta) {
      bars += `<text x="${x0 + w + 58}" y="${y + barH / 2 + 4.5}" fill="${t.good}" font-size="12.5" font-weight="600">${esc(r.delta)}</text>\n`;
    }
  });

  return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img"
  aria-label="Bar chart: tokens needed to get working context for the same task. Full-read workflows cost 6,222 and 5,336 tokens; repoctx budgeted bundles cost 2,151 (outline) and 2,110 (slices) - 66 percent less.">
  <rect width="${W}" height="${H}" rx="10" fill="${t.surface}" stroke="${t.ring}"/>
  <g font-family="system-ui, -apple-system, 'Segoe UI', sans-serif">
    <text x="${M}" y="38" fill="${t.ink}" font-size="17" font-weight="650">Tokens to get working context — same task, measured</text>
    <text x="${M}" y="58" fill="${t.ink2}" font-size="12.5">o200k BPE counts of everything the agent receives · this repository (75 files) · see docs/token-savings.md</text>
    <rect x="${M}" y="70" width="10" height="10" rx="2" fill="${t.base}"/>
    <text x="${M + 16}" y="79" fill="${t.ink2}" font-size="12">full-file workflow</text>
    <rect x="${M + 130}" y="70" width="10" height="10" rx="2" fill="${t.hero}"/>
    <text x="${M + 146}" y="79" fill="${t.ink2}" font-size="12">repoctx budgeted bundle</text>
    ${bars}
    <line x1="${x0}" y1="${top - 6}" x2="${x0}" y2="${top + rows.length * rowGap - rowGap + barH + 8}" stroke="${t.axis}" stroke-width="1.5"/>
  </g>
</svg>\n`;
}

// ------------------------------------------------------------- terminal demo
const mono = "ui-monospace, SFMono-Regular, Menlo, Consolas, monospace";
const term = { bg: "#161615", chrome: "#22221f", ink: "#e8e6df", dim: "#b0aea4",
               prompt: "#1baf7a", accent: "#5598e7", good: "#31c231", muted: "#898781" };

// Real captured output, elided (…) to fit; every line was produced by the tool.
const scenes = [
  {
    caption: "1 · budgeted working context — content included",
    cmd: `repoctx context "improve token budget packing" --detail slices --budget-tokens 2000`,
    out: [
      [`Context for "improve token budget packing" (4 term(s), state b7bc50cb1424):`, "dim"],
      [`  1. src/RepoContext.Core/Context/ContextResult.cs   0.8014  source  [L32-32]  ~106 tokens  55420e02e030`, "ink"],
      [`      reasons: fts, symbol:BudgetTokens, imported-by:src/…/ContextEngine.cs, graph:+5`, "dim"],
      [`      |     public int? BudgetTokens { get; init; }`, "accent"],
      [`      ⋮`, "muted"],
      [`Budget: 8 file(s) · ~1951 estimated tokens · 57 more candidate(s) omitted`, "ink"],
    ],
  },
  {
    caption: "2 · outline before reading — a third of the cost",
    cmd: `repoctx outline src/RepoContext.Core/Context/ReasonCompression.cs`,
    out: [
      [`Outline: src/…/ReasonCompression.cs (source, csharp, 59 lines, ~422 tokens, hash 519a5dc5a333)`, "dim"],
      [`  L14-59  class      internal static class ReasonCompression`, "ink"],
      [`             Dedupes reason lists and caps the only unbounded class, graph reasons…`, "dim"],
      [`  L18-52  method     internal static IReadOnlyList<string> Compress(List<string> reasons)`, "ink"],
      [`  L54-58  method     internal static bool IsGraphReason(string reason) =>`, "ink"],
    ],
  },
  {
    caption: "3 · after an edit — what went stale, what it impacts",
    cmd: `repoctx changed`,
    out: [
      [`Index is stale (state 3a0b441aae12). Run 'repoctx index'.`, "dim"],
      [`Changed:`, "ink"],
      [`  modified  src/RepoContext.Core/Context/ReasonCompression.cs`, "ink"],
      [`Impacted (link to a change):`, "ink"],
      [`  src/RepoContext.Core/Context/ContextEngine.cs  (imports:src/…/ReasonCompression.cs)`, "dim"],
      [`  src/RepoContext.Core/Indexing/ChangeDetector.cs  (imports:src/…/ReasonCompression.cs)`, "dim"],
    ],
  },
  {
    caption: "4 · never pay twice — echo the hash back",
    cmd: `repoctx context "improve token budget packing" --known src/…/ContextResult.cs@55420e02e030`,
    out: [
      [`Context for "improve token budget packing" (4 term(s), state b7bc50cb1424):`, "dim"],
      [`  1. src/RepoContext.Core/Context/ContextResult.cs   0.8014  source  [L32-32]  ~0 tokens  55420e02e030  unchanged`, "good"],
      [`      reasons: fts, symbol:BudgetTokens, imported-by:src/…/ContextEngine.cs, graph:+5`, "dim"],
    ],
  },
];

function demo() {
  const W = 880, lineH = 19, pad = 24, headH = 40;
  const maxLines = Math.max(...scenes.map((s) => s.out.length)) + 1;
  const H = headH + pad + maxLines * lineH + pad + 14;
  const total = scenes.length * 7; // seconds

  let css = `text{font-family:${mono};font-size:12.2px}`;
  scenes.forEach((_, i) => {
    const a = ((i * 7) / total) * 100, b = (((i + 1) * 7 - 0.6) / total) * 100;
    // Scene 0 is fully visible at t=0, so a renderer without CSS animation
    // support still shows one complete frame instead of a blank terminal.
    css += i === 0
      ? `
@keyframes s0{0%{opacity:1}${b}%{opacity:1}${b + 1.2}%{opacity:0}98.8%{opacity:0}100%{opacity:1}}
.s0{animation:s0 ${total}s linear infinite}`
      : `
@keyframes s${i}{0%{opacity:0}${a - 1}%{opacity:0}${a + 1.2}%{opacity:1}${b}%{opacity:1}${Math.min(b + 1.2, 100)}%{opacity:0}100%{opacity:0}}
.s${i}{opacity:0;animation:s${i} ${total}s linear infinite}`;
  });

  let groups = "";
  scenes.forEach((s, i) => {
    let y = headH + pad + 6;
    let lines = `<text x="${pad}" y="${y}"><tspan fill="${term.prompt}">$ </tspan><tspan fill="${term.ink}">${esc(s.cmd)}</tspan></text>\n`;
    s.out.forEach((l) => {
      y += lineH;
      lines += `<text x="${pad}" y="${y}" fill="${term[l[1]]}">${mono_esc(l[0])}</text>\n`;
    });
    lines += `<text x="${W - pad}" y="${H - 16}" fill="${term.muted}" text-anchor="end" font-size="11.5">${esc(s.caption)}</text>\n`;
    groups += `<g class="s${i}">\n${lines}</g>\n`;
  });

  return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img"
  aria-label="Animated terminal demo of the repoctx token-frugal loop: budgeted context with embedded slices, outline before reading, changed after an edit, and known-hash markers.">
  <style>${css}</style>
  <rect width="${W}" height="${H}" rx="10" fill="${term.bg}"/>
  <rect width="${W}" height="${headH}" rx="10" fill="${term.chrome}"/>
  <rect y="${headH - 10}" width="${W}" height="10" fill="${term.chrome}"/>
  <circle cx="24" cy="${headH / 2}" r="5.5" fill="#e34948"/>
  <circle cx="44" cy="${headH / 2}" r="5.5" fill="#eda100"/>
  <circle cx="64" cy="${headH / 2}" r="5.5" fill="#1baf7a"/>
  <text x="${W / 2}" y="${headH / 2 + 4}" fill="${term.dim}" text-anchor="middle" font-family="${mono}" font-size="12">repoctx — the token-frugal loop</text>
  ${groups}
</svg>\n`;
}

writeFileSync(new URL("token-savings.svg", import.meta.url), chart("light"));
writeFileSync(new URL("token-savings-dark.svg", import.meta.url), chart("dark"));
writeFileSync(new URL("demo.svg", import.meta.url), demo());
console.log("written");
