using System.Globalization;
using System.Net;
using System.Text;
using RepoContext.Core.Stats;

namespace RepoContext.Cli.Output;

/// <summary>
/// Renders the token-savings dashboard as a single self-contained HTML page
/// (ADR 0011): inline CSS/SVG/JS, no external resources, works offline from a
/// <c>file://</c> URL — a localhost server would contradict the no-network
/// constraint and is not needed. Like every renderer, a pure projection of
/// the usage log: identical log ⇒ byte-identical output (no timestamps,
/// culture-invariant formatting).
/// </summary>
public static class StatsHtmlOutput
{
    // Reference dataviz palette (validated for both surfaces): series 1 (blue)
    // carries "reads replaced", series 2 (aqua) carries "response cost".
    private const string LightVars =
        "--plane:#f9f9f7;--surface:#fcfcfb;--ink:#0b0b0b;--ink-2:#52514e;--muted:#898781;" +
        "--grid:#e1e0d9;--axis:#c3c2b7;--border:rgba(11,11,11,.10);" +
        "--series-1:#2a78d6;--series-2:#1baf7a;--good:#006300;--bad:#d03b3b;" +
        "--tip-bg:#0b0b0b;--tip-ink:#ffffff;--tip-ink-2:#c3c2b7";

    private const string DarkVars =
        "--plane:#0d0d0d;--surface:#1a1a19;--ink:#ffffff;--ink-2:#c3c2b7;--muted:#898781;" +
        "--grid:#2c2c2a;--axis:#383835;--border:rgba(255,255,255,.10);" +
        "--series-1:#3987e5;--series-2:#199e70;--good:#0ca30c;--bad:#e66767;" +
        "--tip-bg:#383835;--tip-ink:#ffffff;--tip-ink-2:#c3c2b7";

    private const int SvgWidth = 720;

    /// <summary>Renders the full HTML document.</summary>
    public static string Render(UsageReport report)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>repoctx — token savings</title>\n");
        AppendStyle(sb);
        sb.Append("</head>\n<body>\n<main class=\"viz-root\">\n");

        AppendHeader(sb, report);
        if (report.Totals.Calls == 0)
        {
            sb.Append("<section class=\"card empty\"><p>No usage recorded yet.</p>" +
                      "<p class=\"note\">Run queries (context, outline, search, ...) and " +
                      "re-generate this page with <code>repoctx stats --open</code>.</p></section>\n");
        }
        else
        {
            AppendTiles(sb, report.Totals);
            AppendLegend(sb);
            AppendCommandChart(sb, report);
            AppendDayChart(sb, report);
            AppendTables(sb, report);
        }

        sb.Append("<footer><p>Explainable estimate: embedded slices and non-empty outline " +
                  "skeletons are credited at the full-file read cost they are assumed to " +
                  "replace; only explicit matching full-file possession assertions credit a " +
                  "reused read. Partial-evidence receipts receive no full-file credit. Discovery " +
                  "responses (search, related, changed, architecture, paths-only context) " +
                  "count as pure cost. All figures are real o200k token counts from the " +
                  "local log <code>.repoctx/stats.jsonl</code> — nothing leaves this " +
                  "machine; set <code>REPOCTX_NO_STATS=1</code> to disable recording.</p></footer>\n");
        sb.Append("<div id=\"tip\" hidden></div>\n");
        if (report.Totals.Calls > 0)
        {
            AppendScript(sb);
        }

        sb.Append("</main>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static void AppendStyle(StringBuilder sb)
    {
        sb.Append("<style>\n");
        // Theme tokens belong on the root so the outer page canvas and the
        // dashboard inherit the same palette.
        sb.Append(":root{color-scheme:light dark;").Append(LightVars).Append("}\n");
        // The OS preference, unless a host page pinned data-theme="light" ...
        sb.Append("@media (prefers-color-scheme:dark){:root:not([data-theme=light]){")
          .Append(DarkVars).Append("}}\n");
        // ... and an explicit host toggle always wins.
        sb.Append(":root[data-theme=dark]{").Append(DarkVars).Append("}\n");
        sb.Append("""
            *{box-sizing:border-box;margin:0}
            body{background:var(--plane)}
            .viz-root{font:14px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif;
              color:var(--ink);background:var(--plane);max-width:880px;margin:0 auto;
              padding:32px 20px 40px}
            header h1{font-size:22px;font-weight:600}
            header .sub{color:var(--ink-2);font-size:13px;margin-top:2px}
            .tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));
              gap:12px;margin:20px 0 8px}
            .tile{background:var(--surface);border:1px solid var(--border);border-radius:10px;
              padding:14px 16px}
            .tile .label{color:var(--ink-2);font-size:12px}
            .tile .value{font-size:26px;font-weight:600;margin-top:2px}
            .tile .delta{font-size:12px;margin-top:2px}
            .delta.good{color:var(--good)} .delta.bad{color:var(--bad)}
            .legend{display:flex;gap:18px;align-items:center;margin:14px 2px 6px;
              color:var(--ink-2);font-size:12px}
            .legend .key{display:inline-block;width:12px;height:12px;border-radius:3px;
              vertical-align:-1px;margin-right:6px}
            .card{background:var(--surface);border:1px solid var(--border);border-radius:10px;
              padding:16px 18px;margin:10px 0}
            .card h2{font-size:14px;font-weight:600;margin-bottom:10px}
            .card.empty{text-align:center;padding:48px 18px}
            .note{color:var(--muted);font-size:12px;margin-top:6px}
            svg{width:100%;height:auto;display:block}
            .bar{shape-rendering:geometricPrecision}
            .band .hit{fill:transparent;outline:none;cursor:default}
            .band:hover .bar,.band:focus-within .bar{opacity:.82}
            .band:focus-within .hit{stroke:var(--muted);stroke-width:1}
            .axis-label{fill:var(--muted);font-size:11px;
              font-variant-numeric:tabular-nums}
            .cat-label{fill:var(--ink-2);font-size:12px}
            .gridline{stroke:var(--grid);stroke-width:1}
            .baseline{stroke:var(--axis);stroke-width:1}
            table{border-collapse:collapse;width:100%;font-size:13px}
            th{color:var(--ink-2);font-weight:500;text-align:left;font-size:12px}
            th.num,td.num{text-align:right;font-variant-numeric:tabular-nums}
            th,td{padding:5px 10px;border-bottom:1px solid var(--grid)}
            tr:last-child td{border-bottom:none}
            td.neg{color:var(--bad)}
            footer p{color:var(--muted);font-size:12px;margin-top:16px}
            code{font-family:ui-monospace,Consolas,monospace;font-size:11px}
            #tip{position:fixed;z-index:10;background:var(--tip-bg);color:var(--tip-ink);
              border-radius:8px;padding:8px 10px;font-size:12px;pointer-events:none;
              max-width:260px;box-shadow:0 4px 16px rgba(0,0,0,.25)}
            #tip .t{font-weight:600;margin-bottom:4px}
            #tip .r{display:flex;gap:8px;align-items:center;color:var(--tip-ink-2)}
            #tip .r b{color:var(--tip-ink);font-weight:600;margin-left:auto;
              font-variant-numeric:tabular-nums}
            #tip .k{display:inline-block;width:10px;height:3px;border-radius:2px}
            """);
        sb.Append("\n</style>\n");
    }

    private static void AppendHeader(StringBuilder sb, UsageReport report)
    {
        sb.Append("<header><h1>Token savings</h1><p class=\"sub\">repoctx · real o200k token counts");
        if (report.FirstDay is not null)
        {
            sb.Append(" · ").Append(Html(report.FirstDay)).Append(" to ").Append(Html(report.LastDay!));
        }

        sb.Append("</p></header>\n");
    }

    private static void AppendTiles(StringBuilder sb, UsageBucket totals)
    {
        sb.Append("<section class=\"tiles\">\n");
        Tile(sb, "Net saved", N(totals.SavedTokens), DeltaHtml(totals));
        Tile(sb, "Reads replaced", N(totals.ReplacedTokens), null);
        Tile(sb, "Response tokens", N(totals.ServedTokens), null);
        Tile(sb, "Calls", N(totals.Calls), null);
        sb.Append("</section>\n");
    }

    private static void Tile(StringBuilder sb, string label, string value, string? deltaHtml)
    {
        sb.Append("<div class=\"tile\"><div class=\"label\">").Append(label)
          .Append("</div><div class=\"value\">").Append(value).Append("</div>");
        if (deltaHtml is not null)
        {
            sb.Append(deltaHtml);
        }

        sb.Append("</div>\n");
    }

    private static string? DeltaHtml(UsageBucket totals)
    {
        if (totals.ReplacedTokens <= 0)
        {
            return null;
        }

        long percent = (long)Math.Round(
            100.0 * totals.SavedTokens / totals.ReplacedTokens, MidpointRounding.AwayFromZero);
        string cls = totals.SavedTokens >= 0 ? "good" : "bad";
        return string.Create(CultureInfo.InvariantCulture,
            $"<div class=\"delta {cls}\">{percent} % of replaced reads</div>");
    }

    private static void AppendLegend(StringBuilder sb)
    {
        sb.Append("<div class=\"legend\">" +
                  "<span><span class=\"key\" style=\"background:var(--series-1)\"></span>reads replaced</span>" +
                  "<span><span class=\"key\" style=\"background:var(--series-2)\"></span>response cost</span>" +
                  "</div>\n");
    }

    /// <summary>Horizontal grouped bars: replaced vs served per command.</summary>
    private static void AppendCommandChart(StringBuilder sb, UsageReport report)
    {
        const int gutter = 116;
        const int right = 16;
        const int top = 6;
        const int band = 40;
        const int axisRow = 22;
        int plotW = SvgWidth - gutter - right;
        int height = top + (report.Commands.Count * band) + axisRow;
        long max = NiceMax(report.Commands.Max(c => Math.Max(c.Bucket.ReplacedTokens, c.Bucket.ServedTokens)));

        sb.Append("<section class=\"card\"><h2>By command</h2>\n");
        sb.Append(Inv($"<svg viewBox=\"0 0 {SvgWidth} {height}\" role=\"img\" "))
          .Append("aria-label=\"Reads replaced and response cost per command\">\n");

        // Gridlines at ½ and max; the baseline carries zero.
        int plotBottom = top + (report.Commands.Count * band);
        foreach (double f in new[] { 0.5, 1.0 })
        {
            double x = gutter + (plotW * f);
            sb.Append(Inv($"<line class=\"gridline\" x1=\"{x:0.#}\" y1=\"{top}\" "))
              .Append(Inv($"x2=\"{x:0.#}\" y2=\"{plotBottom}\"/>\n"));
            sb.Append(Inv($"<text class=\"axis-label\" x=\"{x:0.#}\" y=\"{height - 6}\" "))
              .Append("text-anchor=\"middle\">")
              .Append(N((long)(max * f))).Append("</text>\n");
        }

        sb.Append(Inv($"<line class=\"baseline\" x1=\"{gutter}\" y1=\"{top}\" "))
          .Append(Inv($"x2=\"{gutter}\" y2=\"{plotBottom}\"/>\n"));

        int y = top;
        foreach (UsageByCommand command in report.Commands)
        {
            sb.Append("<g class=\"band\">\n");
            sb.Append(Inv($"<text class=\"cat-label\" x=\"{gutter - 10}\" y=\"{y + 24}\" "))
              .Append("text-anchor=\"end\">")
              .Append(Html(command.Command)).Append("</text>\n");
            HBar(sb, gutter, y + 7, Scale(command.Bucket.ReplacedTokens, max, plotW), "var(--series-1)");
            HBar(sb, gutter, y + 21, Scale(command.Bucket.ServedTokens, max, plotW), "var(--series-2)");
            HitRect(sb, 0, y, SvgWidth, band, command.Command, command.Bucket);
            sb.Append("</g>\n");
            y += band;
        }

        sb.Append("</svg></section>\n");
    }

    /// <summary>Vertical grouped columns: replaced vs served per recent day.</summary>
    private static void AppendDayChart(StringBuilder sb, UsageReport report)
    {
        const int gutter = 56;
        const int right = 16;
        const int top = 8;
        const int plotH = 150;
        const int axisRow = 24;
        int plotW = SvgWidth - gutter - right;
        int height = top + plotH + axisRow;
        double band = (double)plotW / report.Days.Count;
        long max = NiceMax(report.Days.Max(d => Math.Max(d.Bucket.ReplacedTokens, d.Bucket.ServedTokens)));

        sb.Append(Inv($"<section class=\"card\"><h2>Recent days (up to {UsageReport.RecentDayCount})</h2>\n"));
        sb.Append(Inv($"<svg viewBox=\"0 0 {SvgWidth} {height}\" role=\"img\" "))
          .Append("aria-label=\"Reads replaced and response cost per day\">\n");

        foreach (double f in new[] { 0.5, 1.0 })
        {
            double gy = top + (plotH * (1 - f));
            sb.Append(Inv($"<line class=\"gridline\" x1=\"{gutter}\" y1=\"{gy:0.#}\" "))
              .Append(Inv($"x2=\"{SvgWidth - right}\" y2=\"{gy:0.#}\"/>\n"));
            sb.Append(Inv($"<text class=\"axis-label\" x=\"{gutter - 8}\" y=\"{gy + 4:0.#}\" "))
              .Append("text-anchor=\"end\">")
              .Append(N((long)(max * f))).Append("</text>\n");
        }

        int baselineY = top + plotH;
        sb.Append(Inv($"<line class=\"baseline\" x1=\"{gutter}\" y1=\"{baselineY}\" "))
          .Append(Inv($"x2=\"{SvgWidth - right}\" y2=\"{baselineY}\"/>\n"));

        int i = 0;
        foreach (UsageByDay day in report.Days)
        {
            double x0 = gutter + (band * i);
            double center = x0 + (band / 2);
            sb.Append("<g class=\"band\">\n");
            // 12px columns with a 2px surface gap, centered in the band.
            VBar(sb, center - 13, baselineY, Scale(day.Bucket.ReplacedTokens, max, plotH), "var(--series-1)");
            VBar(sb, center + 1, baselineY, Scale(day.Bucket.ServedTokens, max, plotH), "var(--series-2)");
            sb.Append(Inv($"<text class=\"axis-label\" x=\"{center:0.#}\" y=\"{height - 6}\" "))
              .Append("text-anchor=\"middle\">")
              .Append(Html(day.Day[5..])).Append("</text>\n");
            HitRect(sb, x0, top, band, plotH + axisRow, day.Day, day.Bucket);
            sb.Append("</g>\n");
            i++;
        }

        sb.Append("</svg></section>\n");
    }

    /// <summary>The table view — every figure reachable without hover or color.</summary>
    private static void AppendTables(StringBuilder sb, UsageReport report)
    {
        sb.Append("<section class=\"card\"><h2>All figures</h2>\n");
        AppendTable(sb, "command", report.Commands.Select(c => (c.Command, c.Bucket)));
        sb.Append("<div style=\"height:14px\"></div>\n");
        AppendTable(sb, "day", report.Days.Select(d => (d.Day, d.Bucket)));
        sb.Append("</section>\n");
    }

    private static void AppendTable(
        StringBuilder sb, string labelHeader, IEnumerable<(string Label, UsageBucket Bucket)> rows)
    {
        sb.Append("<table><thead><tr><th>").Append(labelHeader)
          .Append("</th><th class=\"num\">calls</th><th class=\"num\">served</th>" +
                  "<th class=\"num\">replaced</th><th class=\"num\">saved</th></tr></thead><tbody>\n");
        foreach ((string label, UsageBucket bucket) in rows)
        {
            sb.Append("<tr><td>").Append(Html(label)).Append("</td>")
              .Append("<td class=\"num\">").Append(N(bucket.Calls)).Append("</td>")
              .Append("<td class=\"num\">").Append(N(bucket.ServedTokens)).Append("</td>")
              .Append("<td class=\"num\">").Append(N(bucket.ReplacedTokens)).Append("</td>")
              .Append(bucket.SavedTokens < 0 ? "<td class=\"num neg\">" : "<td class=\"num\">")
              .Append(N(bucket.SavedTokens)).Append("</td></tr>\n");
        }

        sb.Append("</tbody></table>\n");
    }

    /// <summary>Horizontal bar, 12px thick, 4px rounded data-end, square baseline.</summary>
    private static void HBar(StringBuilder sb, double x, double y, double w, string fill)
    {
        if (w <= 0)
        {
            return;
        }

        const int h = 12;
        double r = Math.Min(4, w / 2);
        sb.Append(Inv($"<path class=\"bar\" fill=\"{fill}\" d=\"M{x:0.#},{y:0.#} h{w - r:0.#} "))
          .Append(Inv($"q{r:0.#},0 {r:0.#},{r:0.#} v{h - (2 * r):0.#} q0,{r:0.#} -{r:0.#},{r:0.#} "))
          .Append(Inv($"h-{w - r:0.#} z\"/>\n"));
    }

    /// <summary>Vertical column, 12px thick, 4px rounded cap, square baseline.</summary>
    private static void VBar(StringBuilder sb, double x, double baselineY, double h, string fill)
    {
        if (h <= 0)
        {
            return;
        }

        const int w = 12;
        double r = Math.Min(4, h / 2);
        sb.Append(Inv($"<path class=\"bar\" fill=\"{fill}\" d=\"M{x:0.#},{baselineY:0.#} "))
          .Append(Inv($"v-{h - r:0.#} q0,-{r:0.#} {r:0.#},-{r:0.#} h{w - (2 * r):0.#} "))
          .Append(Inv($"q{r:0.#},0 {r:0.#},{r:0.#} v{h - r:0.#} z\"/>\n"));
    }

    /// <summary>
    /// The band's transparent hit/focus target: one tooltip per band lists both
    /// series, and the aria-label carries the same readout for screen readers.
    /// </summary>
    private static void HitRect(
        StringBuilder sb, double x, double y, double w, double h, string label, UsageBucket bucket)
    {
        string safeLabel = Html(label);
        sb.Append(Inv($"<rect class=\"hit\" x=\"{x:0.#}\" y=\"{y:0.#}\" width=\"{w:0.#}\" "))
          .Append(Inv($"height=\"{h:0.#}\" tabindex=\"0\" data-label=\"{safeLabel}\" "))
          .Append("data-replaced=\"").Append(N(bucket.ReplacedTokens))
          .Append("\" data-served=\"").Append(N(bucket.ServedTokens))
          .Append("\" data-saved=\"").Append(N(bucket.SavedTokens))
          .Append("\" data-calls=\"").Append(N(bucket.Calls))
          .Append("\" aria-label=\"").Append(safeLabel)
          .Append(": reads replaced ").Append(N(bucket.ReplacedTokens))
          .Append(", response cost ").Append(N(bucket.ServedTokens))
          .Append(", net saved ").Append(N(bucket.SavedTokens))
          .Append(", calls ").Append(N(bucket.Calls)).Append("\"/>\n");
    }

    private static void AppendScript(StringBuilder sb)
    {
        // Tooltip layer: textContent only (labels are untrusted data), keyboard
        // focus gets the same readout as hover.
        sb.Append("""
            <script>
            (function () {
              var tip = document.getElementById('tip');
              function row(color, label, value) {
                var r = document.createElement('div');
                r.className = 'r';
                if (color) {
                  var k = document.createElement('span');
                  k.className = 'k';
                  k.style.background = color;
                  r.appendChild(k);
                }
                r.appendChild(document.createTextNode(label));
                var b = document.createElement('b');
                b.textContent = value;
                r.appendChild(b);
                return r;
              }
              function show(el, cx, cy) {
                tip.textContent = '';
                var t = document.createElement('div');
                t.className = 't';
                t.textContent = el.dataset.label;
                tip.appendChild(t);
                tip.appendChild(row('var(--series-1)', 'reads replaced', el.dataset.replaced));
                tip.appendChild(row('var(--series-2)', 'response cost', el.dataset.served));
                tip.appendChild(row(null, 'net saved', el.dataset.saved));
                tip.appendChild(row(null, 'calls', el.dataset.calls));
                tip.hidden = false;
                var w = tip.offsetWidth, h = tip.offsetHeight;
                var x = Math.min(cx + 14, window.innerWidth - w - 8);
                var y = Math.min(cy + 14, window.innerHeight - h - 8);
                tip.style.left = Math.max(8, x) + 'px';
                tip.style.top = Math.max(8, y) + 'px';
              }
              function hide() { tip.hidden = true; }
              document.querySelectorAll('.hit').forEach(function (el) {
                el.addEventListener('pointermove', function (e) { show(el, e.clientX, e.clientY); });
                el.addEventListener('pointerleave', hide);
                el.addEventListener('focus', function () {
                  var b = el.getBoundingClientRect();
                  show(el, b.left + (b.width / 2), b.top + (b.height / 2));
                });
                el.addEventListener('blur', hide);
              });
            })();
            </script>
            """);
        sb.Append('\n');
    }

    private static double Scale(long value, long max, int extent) =>
        max <= 0 ? 0 : (double)value / max * extent;

    /// <summary>Smallest of 1/2/5 × 10^k (min 10) covering the value.</summary>
    private static long NiceMax(long value)
    {
        if (value <= 10)
        {
            return 10;
        }

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (double m in new[] { 1.0, 2.0, 5.0, 10.0 })
        {
            if (m * magnitude >= value)
            {
                return (long)(m * magnitude);
            }
        }

        return value;
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Html(string text) => WebUtility.HtmlEncode(text);

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
