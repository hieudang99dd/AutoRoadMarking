#!/usr/bin/env python3
"""Tab 3 responsive source-card contract checks (stdlib only; CI safe).

Browser geometry is additionally tested locally with Chromium at multiple widths/zooms.
This static QA stops an accidental re-introduction of fixed four-card inline styles.
"""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "Autoroadmarking_Pro.CadHost" / "UI" / "web"
html = (WEB / "index.html").read_text(encoding="utf-8-sig")
css = (WEB / "assets/css/tab3-source-layout.css").read_text(encoding="utf-8-sig")
design = (WEB / "assets/css/design-system.css").read_text(encoding="utf-8-sig")
foundation = (WEB / "assets/css/foundation.css").read_text(encoding="utf-8-sig")

links = re.findall(r'<link\b[^>]*rel="stylesheet"[^>]*href="([^"]+)"', html)
assert links, "No CSS links found"
assert links[-1] == "./assets/css/tab3-source-layout.css", (
    "Tab 3 source layout must be loaded after every legacy stylesheet"
)
for ref in links:
    assert (WEB / ref.removeprefix("./")).is_file(), f"Missing CSS: {ref}"

tab3_section = html.split("<!-- TAB 3 -->", 1)[1].split('<section class="t3-source-row">', 1)[0]
assert "<style" not in tab3_section.lower(), "Inline CSS reintroduced into Tab 3"
assert ".t3-source-row{grid-template-columns:1fr 1fr 1fr 1.35fr!important" not in design.replace(" ", ""), (
    "Fixed 4-column !important rule reintroduced into design-system"
)
assert "<section class=\"t3-source-row\">" in html
for name in ("tim", "edge", "component", "overview"):
    assert f'<article class="t3-source-card {name}">' in html, name
for status_id in ("statusTim", "statusMep", "statusMepCauKien", "statusAssembly"):
    assert f'id="{status_id}"' in html, status_id

# Responsive behavior scoped only to tab 3, without body transform/scale.
assert "container-type: inline-size" in css
assert "container-name: arm-tab3-workspace" in css
assert "@container arm-tab3-workspace (max-width: 1350px)" in css
assert "@container arm-tab3-workspace (max-width: 710px)" in css
assert "@supports not (container-type: inline-size)" in css
assert "repeat(4, minmax(0, 1fr))" in css
assert "repeat(2, minmax(0, 1fr))" in css
assert "flex-flow: row wrap !important" in css
assert "overflow-wrap: anywhere" in css
assert "height: auto !important" in css
assert all(selector.strip().startswith(("#tabVach2D", "@", "}", "*", "This")) for selector in [
    "#tabVach2D .t3-source-row", "#tabVach2D .t3-source-card",
    "#tabVach2D .t3-source-title", "#tabVach2D .t3-source-title .status-pill"
])
assert "transform: scale(" not in css
# Check only the two exact orphan fragments removed from the baseline.
# A generic '\n{' pattern would also match valid CSS declarations with a selector.
assert "\n{\n    overflow: hidden;\n}" not in foundation
assert "\n{\n    min-width:0;\n    min-height:0;\n}" not in foundation

print("TAB 3 SOURCE CARD RESPONSIVE QA: PASS")
