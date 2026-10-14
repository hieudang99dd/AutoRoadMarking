#!/usr/bin/env python3
"""Static validation for Visual Studio/Civil 3D build profiles; no Autodesk SDK needed."""
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
read = lambda path: (ROOT / path).read_text(encoding="utf-8-sig")

solution = ET.fromstring(read("AutoRoadMarking_Pro.slnx"))
props = ET.fromstring(read("Directory.Build.props"))
host = ET.fromstring(read("Autoroadmarking_Pro.CadHost/Autoroadmarking_Pro.CadHost.csproj"))

expected = {"C3D2024": "2024", "C3D2025": "2025", "C3D2026": "2026"}
actual = {node.attrib["Name"] for node in solution.findall("./Configurations/BuildType")}
assert {"Debug", "Release", *expected.keys()} <= actual, actual
assert len(solution.findall("./Project")) == 4, "Expected four project references"

config_names = props.findtext("./PropertyGroup/Configurations", default="")
assert set(expected) <= set(config_names.split(";"))
rules = props.findall("./PropertyGroup/Civil3DVersion")
for name, version in expected.items():
    assert any(name in el.attrib.get("Condition", "") and el.text == version for el in rules), name
assert any(el.text == "2024" and el.attrib.get("Condition") == "'$(Civil3DVersion)' == ''" for el in rules)
assert props.findtext("./PropertyGroup/BaseIntermediateOutputPath") == r"obj\C3D$(Civil3DVersion)\"
assert any(el.tag == "Optimize" and el.text == "true" for group in props for el in group)
assert host.findtext("./PropertyGroup/OutputPath") == r"bin\$(ArmOutputConfiguration)\C3D$(Civil3DVersion)\"
assert "net48" in read("Autoroadmarking_Pro.CadHost/Autoroadmarking_Pro.CadHost.csproj")
assert "net8.0-windows" in read("Autoroadmarking_Pro.CadHost/Autoroadmarking_Pro.CadHost.csproj")

for version in expected.values():
    name = f"build-{version}.cmd"
    assert (ROOT / name).is_file()
    assert f"-Civil3DVersion {version}" in read(name)
assert "build-release.ps1" in read("build-all.ps1")
assert all(version in read("build-all.ps1") for version in expected.values())
assert "build-all.ps1" in read("build-all.cmd")
assert "verify-build-profiles.py" in read(".github/workflows/qa.yml")
for name in (*[f"build-{v}.cmd" for v in expected.values()], "build-all.cmd"):
    assert name in read("AutoRoadMarking_Pro.slnx")
print("MULTI-VERSION BUILD PROFILE QA: PASS")
