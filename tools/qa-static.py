#!/usr/bin/env python3
"""Static QA không cần Autodesk SDK. Trả exit code != 0 nếu có lỗi."""
from __future__ import annotations

import re
import sys
import csv
import json
import hashlib
import shutil
import subprocess
import xml.etree.ElementTree as ET
from collections import Counter
from html.parser import HTMLParser
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ERRORS: list[str] = []


def fail(msg: str) -> None:
    ERRORS.append(msg)


def scan_csharp(path: Path) -> None:
    text = path.read_text(encoding="utf-8")
    stack: list[tuple[str, int]] = []
    i = 0
    line = 1
    state = "code"
    pairs = {')': '(', '}': '{', ']': '['}

    while i < len(text):
        c = text[i]
        n = text[i + 1] if i + 1 < len(text) else ''
        if c == '\n':
            line += 1

        if state == 'linecomment':
            if c == '\n': state = 'code'
            i += 1; continue
        if state == 'blockcomment':
            if c == '*' and n == '/': state = 'code'; i += 2; continue
            i += 1; continue
        if state == 'char':
            if c == '\\': i += 2; continue
            if c == "'": state = 'code'
            i += 1; continue
        if state == 'string':
            if c == '\\': i += 2; continue
            if c == '"': state = 'code'
            i += 1; continue
        if state == 'verbatim':
            if c == '"' and n == '"': i += 2; continue
            if c == '"': state = 'code'
            i += 1; continue

        if c == '/' and n == '/': state = 'linecomment'; i += 2; continue
        if c == '/' and n == '*': state = 'blockcomment'; i += 2; continue
        if c == "'": state = 'char'; i += 1; continue
        if c == '$' and n == '@' and i + 2 < len(text) and text[i + 2] == '"': state = 'verbatim'; i += 3; continue
        if c == '@' and n == '$' and i + 2 < len(text) and text[i + 2] == '"': state = 'verbatim'; i += 3; continue
        if c == '@' and n == '"': state = 'verbatim'; i += 2; continue
        if c == '$' and n == '"': state = 'string'; i += 2; continue
        if c == '"': state = 'string'; i += 1; continue

        if c in '({[':
            stack.append((c, line))
        elif c in ')}]':
            if not stack or stack[-1][0] != pairs[c]:
                fail(f"C# delimiter mismatch {path.relative_to(ROOT)}:{line} ({c})")
                return
            stack.pop()
        i += 1

    if state in {'string', 'verbatim', 'char', 'blockcomment'}:
        fail(f"C# lexical state chưa đóng: {path.relative_to(ROOT)} ({state})")
    if stack:
        fail(f"C# delimiter chưa đóng: {path.relative_to(ROOT)} {stack[-1]}")


TEXT_EXTENSIONS = {
    '.cs', '.csproj', '.slnx', '.xaml', '.html', '.css', '.js', '.json',
    '.csv', '.md', '.txt', '.ps1', '.py', '.yml', '.yaml', '.xml',
    '.props', '.targets', '.gitignore'
}
EXCLUDED_DIRS = {'.git', 'bin', 'obj', '.vs'}
MANIFEST_NAME = 'SOURCE_MANIFEST.sha256'


def source_files() -> list[Path]:
    result: list[Path] = []
    for path in ROOT.rglob('*'):
        if not path.is_file():
            continue
        rel = path.relative_to(ROOT)
        if any(part in EXCLUDED_DIRS for part in rel.parts):
            continue
        if rel.as_posix() == MANIFEST_NAME:
            continue
        result.append(path)
    return sorted(result, key=lambda p: p.relative_to(ROOT).as_posix().lower())


def manifest_digest(path: Path) -> str:
    data = path.read_bytes()
    suffix = path.suffix.lower()
    if suffix in TEXT_EXTENSIONS or path.name in {'.gitignore', '.gitattributes'}:
        data = data.replace(b'\r\n', b'\n').replace(b'\r', b'\n')
    return hashlib.sha256(data).hexdigest()


def verify_manifest() -> None:
    manifest = ROOT / MANIFEST_NAME
    if not manifest.exists():
        fail('Thiếu SOURCE_MANIFEST.sha256.')
        return

    entries: dict[str, str] = {}
    for line_no, line in enumerate(manifest.read_text(encoding='utf-8').splitlines(), start=1):
        if not line.strip():
            continue
        match = re.fullmatch(r'([0-9a-f]{64})\s{2}(.+)', line)
        if not match:
            fail(f'Manifest sai định dạng dòng {line_no}.')
            continue
        entries[match.group(2).replace('\\', '/')] = match.group(1)

    current = {
        p.relative_to(ROOT).as_posix(): manifest_digest(p)
        for p in source_files()
    }

    missing = sorted(set(current) - set(entries))
    stale = sorted(set(entries) - set(current))
    changed = sorted(
        path for path in set(current) & set(entries)
        if current[path] != entries[path]
    )

    if missing:
        fail('Manifest thiếu file: ' + ', '.join(missing[:20]))
    if stale:
        fail('Manifest còn file không tồn tại: ' + ', '.join(stale[:20]))
    if changed:
        fail('Manifest có SHA stale: ' + ', '.join(changed[:20]))


class UiParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.ids: list[str] = []
        self.window_calls: set[str] = set()

    def handle_starttag(self, tag, attrs):
        for key, value in attrs:
            if key == 'id' and value:
                self.ids.append(value)
            if key in {'onclick', 'onchange', 'oninput', 'onkeyup', 'onblur'} and value:
                self.window_calls.update(re.findall(r'window\.([A-Za-z_$][\w$]*)\s*\(', value))


def main() -> int:
    cs_files = sorted(ROOT.rglob('*.cs'))
    for p in cs_files:
        scan_csharp(p)

    for pattern in ('*.csproj', '*.slnx', '*.xaml'):
        for p in ROOT.rglob(pattern):
            try:
                ET.parse(p)
            except Exception as exc:
                fail(f"XML invalid {p.relative_to(ROOT)}: {exc}")

    web = ROOT / 'Autoroadmarking_Pro.CadHost' / 'UI' / 'web'
    html = (web / 'index.html').read_text(encoding='utf-8')
    parser = UiParser(); parser.feed(html)
    duplicates = {k: v for k, v in Counter(parser.ids).items() if v > 1}
    if duplicates:
        fail(f"Duplicate HTML id: {duplicates}")

    js_files = sorted((web / 'assets' / 'js').rglob('*.js'))
    node = shutil.which('node')
    if node:
        for js in js_files:
            proc = subprocess.run([node, '--check', str(js)], capture_output=True, text=True)
            if proc.returncode != 0:
                fail(f'JavaScript syntax error {js.relative_to(ROOT)}: {proc.stderr.strip()}')

    js_text = '\n'.join(p.read_text(encoding='utf-8') for p in js_files) + '\n' + html
    definitions = set(re.findall(r'window\.([A-Za-z_$][\w$]*)\s*=', js_text))
    definitions.update(re.findall(r'function\s+([A-Za-z_$][\w$]*)\s*\(', js_text))
    missing_functions = sorted(parser.window_calls - definitions)
    if missing_functions:
        fail('UI functions chưa định nghĩa: ' + ', '.join(missing_functions))

    bridge = (web / 'assets' / 'js' / 'bridge' / 'arm-host-bridge.js').read_text(encoding='utf-8')
    executor = (ROOT / 'Autoroadmarking_Pro.CadHost' / 'Cad' / 'CadActionExecutor.cs').read_text(encoding='utf-8')
    router = (ROOT / 'Autoroadmarking_Pro.CadHost' / 'UI' / 'WebMessageRouter.cs').read_text(encoding='utf-8')
    posts = set(re.findall(r"\bpost\(\s*['\"]([^'\"]+)['\"]", js_text))
    cases = set(re.findall(r'case\s+"([^"]+)"\s*:', executor + '\n' + router)) | {'Ping'}
    missing_actions = sorted(posts - cases)
    if missing_actions:
        fail('WebView actions chưa có backend handler: ' + ', '.join(missing_actions))

    # UI dùng các tên thao tác tiếng Việt/legacy qua window.goiAction(); chúng phải
    # được bridge ánh xạ hoặc là action backend trực tiếp, không được rơi vào default âm thầm.
    ui_alias_calls = set(re.findall(r"goiAction\s*\(\s*['\"]([^'\"]+)['\"]", js_text))
    bridge_cases = set(re.findall(r"case\s+['\"]([^'\"]+)['\"]\s*:", bridge))
    missing_aliases = sorted(a for a in ui_alias_calls if a not in bridge_cases and a not in cases)
    if missing_aliases:
        fail('goiAction alias chưa được bridge/backend xử lý: ' + ', '.join(missing_aliases))

    # Tab 1: tham số typed và metadata mở rộng phải tách nguồn sự thật.
    if 'id="t1StandardPatternFields"' not in html:
        fail('Tab 1 thiếu container typed t1StandardPatternFields.')
    if 'id="t1_CustomProperties"' not in html:
        fail('Tab 1 thiếu trường metadata mở rộng t1_CustomProperties.')
    policy_path = ROOT / 'Autoroadmarking_Pro.Application' / 'Markings' / 'MarkingCustomPropertyPolicy.cs'
    if not policy_path.exists():
        fail('Thiếu MarkingCustomPropertyPolicy.cs.')
    else:
        policy_text = policy_path.read_text(encoding='utf-8')
        for key in ('DashLength', 'GapLength', 'PaintRatio', 'StandardRef', 'RoadIdentity', 'AxisKey'):
            if f'"{key}"' not in policy_text:
                fail(f'Custom property reserved policy thiếu key {key}.')
    if 'MarkingCustomPropertyPolicy.Validate(t.CustomProperties)' not in executor:
        fail('CadActionExecutor chưa validate CustomProperties bằng policy có thẩm quyền.')
    if 'ClearCustomPattern(t)' not in executor:
        fail('Save template chưa xóa field pattern không có thẩm quyền.')

    # 7.1 ↔ 7.3: input không có max; backend planner không hard-code max 3 m.
    input_match = re.search(r'<input[^>]+id="t3_stopToPedDistance"[^>]*>', html)
    if not input_match:
        fail('Không tìm thấy input t3_stopToPedDistance.')
    elif re.search(r'\bmax\s*=', input_match.group(0), re.I):
        fail('t3_stopToPedDistance vẫn có thuộc tính max.')

    planner = (ROOT / 'Autoroadmarking_Pro.Application' / 'Intersections' / 'MarkingPlacementPlanner.cs').read_text(encoding='utf-8')
    if 'warningMaximum' not in planner or 'requested > warningMaximum.Value' not in planner:
        fail('Planner 7.1↔7.3 thiếu cơ chế warningMaximum không-clamp.')
    if re.search(r'Math\.(?:Min|Max|Clamp)\s*\([^\n;]*\b3(?:\.0+)?\b', planner):
        fail('Planner 7.1↔7.3 có dấu hiệu hard-code 3 m.')

    # Finalized project contracts / generated geometry.
    for action in ('Tab1_UpdateLayerManagementSet', 'Tab1_ApplySharedLayerSet', 'ImportMarkingTemplates'):
        if f'case "{action}"' not in executor:
            fail(f'Thiếu backend action {action}.')

    linetype_service = ROOT / 'Autoroadmarking_Pro.CadHost' / 'Cad' / 'Layers' / 'CadLinetypeService.cs'
    if not linetype_service.exists():
        fail('Thiếu CadLinetypeService cho CUSTOM_REAL.')
    else:
        lt = linetype_service.read_text(encoding='utf-8')
        if 'SetDashLengthAt' not in lt or 'IsGeometryDriven' not in lt:
            fail('CadLinetypeService chưa xử lý đầy đủ CUSTOM_REAL/geometry-driven.')

    stop_cross = (ROOT / 'Autoroadmarking_Pro.CadHost' / 'Cad' / 'Markings' / 'StopCrosswalkGenerator.cs').read_text(encoding='utf-8')
    for token in (
        'CreateCrosswalkZebra',
        'PEDESTRIAN_CROSSWALK_ZEBRA',
        '["PaintRatio"] = "1"',
        'STEP2_POLYGON_AXIS_INTERSECTION',
        'PlanStopCrosswalk('
    ):
        if token not in stop_cross:
            fail(f'Generator 7.3 thiếu contract/token {token}.')
    if 'ResolveBullhornEndStation' in stop_cross:
        fail('Generator 7.3 đã quay lại heuristic ResolveBullhornEndStation; Step 2 phải là reference chính thức.')
    if 'crosswalkAnchorStation = boundaryStation' not in stop_cross:
        fail('Generator 7.3 không dùng trực tiếp boundaryStation của Step 2 làm anchor.')

    batch_path = ROOT / 'Autoroadmarking_Pro.CadHost' / 'Cad' / 'Markings' / 'BatchApproachLaneGenerator.cs'
    if not batch_path.exists():
        fail('Thiếu BatchApproachLaneGenerator.cs cho Bước 5.')
        batch = ''
    else:
        batch = batch_path.read_text(encoding='utf-8')
        for token in (
            'PlanApproachSegment(',
            'SEGMENT_BEFORE_STOP_NO_CLAMP',
            '"1.1"',
            '"1.2"',
            '"2.1"',
            '"2.2"',
            'IsCenterLineCode(code) ? "CENTER_LINE" : "LANE_LINE"'
        ):
            if token not in batch:
                fail(f'Bước 5 thiếu contract/token {token}.')
        if 'startStation = Math.Max(axisStart' in batch or 'endStation = Math.Min(axisEnd' in batch:
            fail('Bước 5 vẫn silent-clamp station vào endpoint RoadAxis.')

    longitudinal = (ROOT / 'Autoroadmarking_Pro.CadHost' / 'Cad' / 'Markings' / 'LongitudinalMarkingGenerator.cs').read_text(encoding='utf-8')
    if 'DUPLICATE_ON_CROSS_SECTION' not in longitudinal or 'RequiresDoublePresentation' not in longitudinal:
        fail('Vạch 1.3 chưa được nhân đôi theo presentation rule khi sinh hình học.')

    csv_path = web / 'data' / 'templates' / 'ARM_MarkingTemplates_QCVN41_CUSTOM_COMMON_v6_FINAL.csv'
    if not csv_path.exists():
        fail('Thiếu CSV template QCVN/GGT đóng gói.')
    else:
        csv_text = csv_path.read_text(encoding='utf-8-sig')
        if 'GTT' in csv_text:
            fail('CSV đóng gói vẫn còn mã GTT cũ.')
        try:
            with csv_path.open(encoding='utf-8-sig', newline='') as handle:
                csv_rows = list(csv.DictReader(handle))
        except Exception as exc:
            csv_rows = []
            fail(f'CSV template không đọc được: {exc}')

        codes = {str(row.get('Code', '')).strip().upper() for row in csv_rows}
        for code in ('1.3', '7.3', 'GGT'):
            if code.upper() not in codes:
                fail(f'CSV đóng gói thiếu mã {code}.')

        # CustomProperties của template đóng gói không được chứa key hệ thống/reserved.
        reserved_match = re.search(r'new\[\]\s*\{(.*?)\}\s*,\s*StringComparer\.OrdinalIgnoreCase', policy_text, re.S) if policy_path.exists() else None
        reserved = set(re.findall(r'"([^"]+)"', reserved_match.group(1))) if reserved_match else set()
        reserved_lower = {x.lower() for x in reserved}
        for row in csv_rows:
            raw = str(row.get('CustomProperties', '') or '').strip()
            if not raw:
                continue
            try:
                props = json.loads(raw)
            except Exception as exc:
                fail(f"CSV {row.get('Code','?')} có CustomProperties JSON lỗi: {exc}")
                continue
            if not isinstance(props, dict):
                fail(f"CSV {row.get('Code','?')} CustomProperties phải là JSON object.")
                continue
            bad = sorted(k for k in props if str(k).lower() in reserved_lower)
            if bad:
                fail(f"CSV {row.get('Code','?')} chứa reserved CustomProperties: {', '.join(bad)}")

        ggt = next((row for row in csv_rows if str(row.get('Code','')).strip().upper() == 'GGT'), None)
        if ggt and '"code"' in str(ggt.get('CustomProperties','')).lower():
            fail('GGT vẫn lặp Code bên trong CustomProperties.')

    # Thuật ngữ UI đã chốt và state schema mới phải nhất quán.
    web_text = html + '\n' + '\n'.join(p.read_text(encoding='utf-8') for p in js_files)
    if 'CẬP NHẬT BỘ' in web_text:
        fail('UI vẫn còn thuật ngữ cũ CẬP NHẬT BỘ; phải dùng CẬP NHẬT THƯ VIỆN.')
    if 'GTT' in web_text:
        fail('UI web vẫn còn mã GTT cũ.')

    expected_step5_options = (
        '<option value="1.1">Vạch 1.1 (Tim đường - Nét đứt)</option>',
        '<option value="1.2">Vạch 1.2 (Tim đường - Nét liền)</option>',
        '<option value="2.1" selected>Vạch 2.1 (Phân làn - Nét đứt)</option>',
        '<option value="2.2">Vạch 2.2 (Phân làn - Nét liền)</option>'
    )
    for option in expected_step5_options:
        if option not in html:
            fail('UI Bước 5 sai mô tả liền/đứt: ' + option)

    state_text = (ROOT / 'Autoroadmarking_Pro.CadHost' / 'Cad' / 'State' / 'ArmProjectState.cs').read_text(encoding='utf-8')
    if 'SchemaVersion { get; set; } = 11' not in state_text:
        fail('ArmProjectState chưa dùng DWG state schema 11.')
    state_store_text = (ROOT / 'Autoroadmarking_Pro.CadHost' / 'Cad' / 'State' / 'DwgProjectStateStore.cs').read_text(encoding='utf-8')
    if 'normalizedCode == "7.3" || normalizedCode == "GGT"' not in state_store_text:
        fail('Migration chưa ép QuantityMethod GENERATED cho 7.3/GGT.')
    if 'template.Code.Equals("GTT"' not in state_store_text or 'template.Code = "GGT"' not in state_store_text:
        fail('Migration legacy GTT -> canonical GGT chưa đầy đủ.')
    if 'template.Code.Equals("GGT"' in state_store_text and 'template.Code = "GTT"' in state_store_text:
        fail('Migration đang đảo canonical GGT về GTT.')

    if 'SyncSelectedCrossSections' not in executor:
        fail('Thiếu backend action SyncSelectedCrossSections cho nút ĐỒNG BỘ MẶT CẮT ĐÃ CHỌN.')
    if "post('SyncSelectedCrossSections'" not in js_text:
        fail('UI Tab 2 chưa gọi backend action SyncSelectedCrossSections thật.')
    if 'ActiveCrossSectionIds' not in state_text or 'GetEffectiveCrossSections' not in state_text:
        fail('State chưa lưu tập MCN đồng bộ/hoạt động cho Tab 2→3/4.')

    bad_dirs = [p for name in ('bin', 'obj', '.vs') for p in ROOT.rglob(name) if p.is_dir()]
    if bad_dirs:
        fail('Có build/cache directory trong source: ' + ', '.join(str(p.relative_to(ROOT)) for p in bad_dirs[:10]))

    test_project = ROOT / 'Autoroadmarking_Pro.Application.Tests' / 'Autoroadmarking_Pro.Application.Tests.csproj'
    test_file = ROOT / 'Autoroadmarking_Pro.Application.Tests' / 'Intersections' / 'MarkingPlacementPlannerTests.cs'
    if not test_project.exists() or not test_file.exists():
        fail('Thiếu regression tests thuần .NET cho station 7.3/7.1 và Bước 5.')

    verify_manifest()

    if ERRORS:
        print('STATIC QA: FAIL')
        for err in ERRORS:
            print(' -', err)
        return 1

    print('STATIC QA: PASS')
    print(f' - C# files: {len(cs_files)}')
    print(f' - HTML ids: {len(parser.ids)} (unique)')
    print(f' - Modular JS files: {len(js_files)}')
    print(f' - WebView post actions: {len(posts)} (all handled)')
    print(f' - goiAction aliases: {len(ui_alias_calls)} (all mapped)')
    print(f' - Node syntax check: {"pass" if node else "skipped (node unavailable)"}')
    print(' - Tab 1: typed pattern/reference separated from project metadata')
    print(' - Tab 1: reserved metadata keys validated by backend policy + bundled CSV')
    print(' - 7.1↔7.3: Step2 anchor + exact requested station distance')
    print(' - Step 5: 1.1/1.2 centerline, 2.1/2.2 lane boundaries, no station clamp')
    print(' - GGT: canonical speed-hump code; legacy GTT migrates to GGT')
    print(' - DWG state schema: 11')
    print(' - regression test project: present')
    print(' - source manifest: complete and SHA-fresh')
    print(' - build/cache directories: none')
    return 0


if __name__ == '__main__':
    sys.exit(main())
