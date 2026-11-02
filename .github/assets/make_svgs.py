"""Builds the README's hero and stats SVGs, light and dark.

    python .github/assets/make_svgs.py <brand-kit-dir> <tests-passing>

The lockup is copied from the Supraman brand kit (lockups/workman-lockup-on-{light,dark}.svg), not
redrawn. The terminal lines are verbatim output of a local server holding synthetic demo data.
"""
import pathlib
import re
import sys
from html import escape

brand = pathlib.Path(sys.argv[1])
tests = sys.argv[2]
out = pathlib.Path(__file__).parent

THEMES = {
    "light": dict(bg="#ffffff", panel="#f6f8fa", edge="#d0d7de", ink="#1f2430", muted="#59636e",
                  accent="#b06c00", lockup="on-light"),
    "dark": dict(bg="#0d1117", panel="#161b22", edge="#30363d", ink="#e6e9ef", muted="#8b949e",
                 accent="#f5a524", lockup="on-dark"),
}
FONT = "Archivo,-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif"
MONO = "ui-monospace,SFMono-Regular,'SF Mono',Menlo,Consolas,monospace"

# Terminal: always dark, it reads as a terminal. (kind, text): call, ok, refused, fail, hint, plain.
TERMINAL = [
    ("call", 'work_report(ref:"checkout-v2/1.2", status:"done", …)', "worker"),
    ("ok", "OK  1.2  attempt 1 recorded, status done", None),
    ("gap", "", None),
    ("call", 'work_status(ref:"checkout-v2/1.2", to:"done")', "orchestrator"),
    ("refused", "REFUSED  work_status(1.2 → done). 3 conditions unmet.", None),
    ("fail", '✗ criterion c1 "Quantities add up, no duplicate lines" unchecked', None),
    ("fail", '✗ criterion c2 "An empty guest cart changes nothing" unchecked', None),
    ("fail", "✗ no verification with run_by=orchestrator (1 recorded, none", None),
    ("plain", "  of them run by the orchestrator)", None),
    ("hint", '  → work_verify(ref:"1.2", command:"dotnet test --filter', None),
    ("hint", '    FullyQualifiedName~CartMergeTests", result_line:"…",', None),
    ("hint", '    run_by:"orchestrator")', None),
]


def lockup(t, x, y, height):
    svg = (brand / "lockups" / f"workman-lockup-{t['lockup']}.svg").read_text(encoding="utf-8").strip()
    view = re.search(r'viewBox="([^"]+)"', svg).group(1)
    w, h = (float(v) for v in view.split()[2:])
    width = height * w / h
    inner = re.sub(r"^<svg[^>]*>", "", svg)
    return f'<svg x="{x}" y="{y}" width="{width:.0f}" height="{height}" viewBox="{view}">{inner}'


def hero(t):
    tb, tink, tmut, amber = "#12151b", "#e6e9ef", "#8b949e", "#f5a524"
    colours = dict(ok="#3fb950", refused="#f85149", fail="#ff7b72", hint="#79c0ff", plain=tink)
    def mono(text):  # SVG collapses runs of spaces; a no-break space keeps the column
        return escape(text).replace(" ", " ")

    rows, y = [], 134
    for kind, text, who in TERMINAL:
        if kind == "gap":
            y += 10
            continue
        if kind == "call":
            rows.append(f'<text x="622" y="{y}" fill="{amber}">›</text>'
                        f'<text x="640" y="{y}" fill="{tink}">{mono(text)}</text>'
                        f'<text x="1214" y="{y}" fill="{tmut}" text-anchor="end">{who}</text>')
        else:
            weight = ' font-weight="700"' if kind == "refused" else ""
            rows.append(f'<text x="640" y="{y}" fill="{colours[kind]}"{weight}>{mono(text)}</text>')
        y += 23
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1280 440" width="1280" height="440" role="img" aria-label="Workman: your AI agents forget, Workman remembers. Decisions, rules and next steps live outside the context, as named items your team can open. A terminal shows work_status refusing to close a task whose done-criteria are unchecked and that no orchestrator verified.">
<rect width="1280" height="440" rx="18" fill="{t["bg"]}" stroke="{t["edge"]}"/>
{lockup(t, 64, 60, 52)}
<text x="64" y="160" font-family="{MONO}" font-size="13" letter-spacing="2" fill="{t["accent"]}">CLAUDE CODE PLUGIN · MCP SERVER · DASHBOARD</text>
<text font-family="{FONT}" font-weight="800" font-size="34" fill="{t["ink"]}"><tspan x="64" y="212">Your AI agents forget.</tspan><tspan x="64" y="254">Workman remembers.</tspan></text>
<text font-family="{FONT}" font-size="17" fill="{t["muted"]}"><tspan x="64" y="300">Decisions, rules and next steps live outside</tspan><tspan x="64" y="326">the context, as named items your team can open.</tspan></text>
<text x="64" y="384" font-family="{MONO}" font-size="15" fill="{t["muted"]}"><tspan fill="{t["accent"]}">/plugin</tspan> install workman@supraman</text>
<rect x="600" y="56" width="640" height="344" rx="12" fill="{tb}" stroke="{t["edge"]}"/>
<circle cx="624" cy="80" r="6" fill="#f85149"/><circle cx="644" cy="80" r="6" fill="#d29922"/><circle cx="664" cy="80" r="6" fill="#3fb950"/>
<text x="920" y="85" text-anchor="middle" font-family="{MONO}" font-size="12" fill="{tmut}"><tspan fill="{amber}">^</tspan><tspan fill="{tink}">work</tspan>man · mcp__workman__*</text>
<line x1="600" y1="100" x2="1240" y2="100" stroke="#30363d"/>
<g font-family="{MONO}" font-size="14">{"".join(rows)}</g>
</svg>
'''


def stats(t):
    cards = [
        ("20", "work_* tools, one server", "19 for every orchestrator, plus", "work_import behind its own grant"),
        ("5", "conditions guard done", "criteria, an orchestrator's verify,", "children, blockers, a commit"),
        ("6", "tools a worker's token sees", "workers report and verify;", "only an orchestrator closes"),
        (tests, "tests passing", "the .NET server and the plugin,", "run by CI on every change"),
    ]
    w, gap, x0 = 287, 18, 18
    parts = []
    for i, (big, head, l1, l2) in enumerate(cards):
        x = x0 + i * (w + gap)
        parts.append(f'''<rect x="{x}" y="18" width="{w}" height="164" rx="14" fill="{t["panel"]}" stroke="{t["edge"]}"/>
<text x="{x + 24}" y="82" font-family="{FONT}" font-weight="800" font-size="46" fill="{t["accent"]}">{escape(big)}</text>
<text x="{x + 24}" y="114" font-family="{FONT}" font-weight="600" font-size="17" fill="{t["ink"]}">{escape(head)}</text>
<text font-family="{FONT}" font-size="14" fill="{t["muted"]}"><tspan x="{x + 24}" y="142">{escape(l1)}</tspan><tspan x="{x + 24}" y="162">{escape(l2)}</tspan></text>''')
    label = (f"20 work_* tools on one server; 5 conditions guard done; a worker's token sees 6 tools; "
             f"{tests} tests passing")
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1238 200" width="1238" height="200" role="img" aria-label="{escape(label)}">
{"".join(parts)}
</svg>
'''


for name, t in THEMES.items():
    (out / f"hero-{name}.svg").write_text(hero(t), encoding="utf-8")
    (out / f"stats-{name}.svg").write_text(stats(t), encoding="utf-8")
print("ok")
