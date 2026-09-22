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
    for token in ('CreateCrosswalkZebra', 'PEDESTRIAN_CROSSWALK_ZEBRA', '["PaintRatio"] = "1"',
                  'STEP2_POLYGON_AXIS_INTERSECTION', 'double crosswalkAnchorStation = boundaryStation;'):
        if token not in stop_cross:
            fail(f'Generator 7.3 thiếu {token}.')
    if 'ResolveBullhornEndStation(' in stop_cross:
        fail('Generator 7.3 vẫn còn heuristic ResolveBullhornEndStation trong source.')

    # 7.3 Mẫu 1: dải sơn phải chạy ngang giữa hai MÉP, xếp theo station trong crossingLength.
    for token in (
        'PEDESTRIAN_CROSSWALK_ZEBRA_TRANSVERSE_BARS',
        'double stripeStation =',
        'axis, stripeStation, stripeLeft',
        'axis, stripeStation, stripeRight',
        'Math.Floor((crossingLength + stripeGap) / pitch)',
    ):
        if token not in stop_cross:
            fail(f'Generator 7.3 chưa khóa hình học zebra ngang đúng chuẩn: thiếu {token}.')
    if 'PEDESTRIAN_CROSSWALK_ZEBRA_LONGITUDINAL_BARS' in stop_cross:
        fail('Generator 7.3 vẫn còn contract dải zebra chạy dọc TIM.')
    if 'Math.Floor((roadWidth + stripeGap) / pitch)' in stop_cross:
        fail('Generator 7.3 vẫn tính số dải theo bề rộng mặt đường thay vì crossingLength.')

    # Khoảng cách 7.3 -> 7.1 của dự án được khóa là TIM-ĐẾN-TIM.
    if 'double stopStation = crosswalkAnchorStation + outwardSign * distance;' not in stop_cross:
        fail('Generator 7.1/7.3 không còn giữ khoảng cách tim-đến-tim.')
    if 'Khoảng cách tim 7.3 → tim 7.1' not in html:
        fail('UI Tab 3 chưa thể hiện rõ khoảng cách 7.3↔7.1 là tim-đến-tim.')

    # Queue WebView -> CAD phải gắn request với đúng Document và UI chặn gửi lặp tác vụ nặng.
    queue_path = ROOT / 'Autoroadmarking_Pro.CadHost' / 'Commands' / 'CadCommandQueue.cs'
    command_path = ROOT / 'Autoroadmarking_Pro.CadHost' / 'Commands' / 'RoadMarkingCommands.cs'
    if not queue_path.exists() or not command_path.exists():
        fail('Thiếu CadCommandQueue/RoadMarkingCommands.')
    else:
        queue_text = queue_path.read_text(encoding='utf-8')
        command_text = command_path.read_text(encoding='utf-8')
        for token in ('public Document? Document', 'TryDequeue(Document document', 'Document = doc', 'SameDocument('):
            if token not in queue_text:
                fail(f'CadCommandQueue chưa khóa request theo DWG: thiếu {token}.')
        if 'CadCommandQueue.TryDequeue(out QueuedCadRequest request)' not in command_text and \
           'CadCommandQueue.TryDequeue(commandDocument, out QueuedCadRequest request)' not in command_text:
            fail('ARM_INTERNAL_EXEC chưa dequeue request qua CadCommandQueue.')
        if 'return TryDequeue(document, out request);' not in queue_text:
            fail('Overload TryDequeue tương thích chưa chuyển tiếp qua document-bound dequeue.')
    if 'singleFlightCadActions' not in js_text or 'arm.pendingCadActions.has(action)' not in js_text:
        fail('WebView chưa chặn gửi lặp các CAD action nặng.')

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

    state_text = (ROOT / 'Autoroadmarking_Pro.CadHost' / 'Cad' / 'State' / 'ArmProjectState.cs').read_text(encoding='utf-8')
    if 'SchemaVersion { get; set; } = 11' not in state_text:
        fail('ArmProjectState chưa dùng DWG state schema 11.')
    if 'normalizedCode == "7.3" || normalizedCode == "GGT"' not in (ROOT / 'Autoroadmarking_Pro.CadHost' / 'Cad' / 'State' / 'DwgProjectStateStore.cs').read_text(encoding='utf-8'):
        fail('Migration chưa ép QuantityMethod GENERATED cho 7.3/GGT.')

    if 'SyncSelectedCrossSections' not in executor:
        fail('Thiếu backend action SyncSelectedCrossSections cho nút ĐỒNG BỘ MẶT CẮT ĐÃ CHỌN.')
    if "post('SyncSelectedCrossSections'" not in js_text:
        fail('UI Tab 2 chưa gọi backend action SyncSelectedCrossSections thật.')
    if 'ActiveCrossSectionIds' not in state_text or 'GetEffectiveCrossSections' not in state_text:
        fail('State chưa lưu tập MCN đồng bộ/hoạt động cho Tab 2→3/4.')

    # Step 5: UI code/description phải khớp template và backend không silent-clamp station.
    expected_step5_labels = (
        'value="1.1">Vạch 1.1 (Tim đường - Nét đứt)',
        'value="1.2">Vạch 1.2 (Tim đường - Nét liền)',
        'value="2.1" selected>Vạch 2.1 (Phân làn - Nét đứt)',
        'value="2.2">Vạch 2.2 (Phân làn - Nét liền)',
    )
    for token in expected_step5_labels:
        if token not in html:
            fail(f'Step 5 UI sai/thiếu nhãn: {token}')

    approach_generator_path = ROOT / 'Autoroadmarking_Pro.CadHost' / 'Cad' / 'Markings' / 'BatchApproachLaneGenerator.cs'
    if not approach_generator_path.exists():
        fail('Thiếu BatchApproachLaneGenerator.cs.')
    else:
        approach_generator = approach_generator_path.read_text(encoding='utf-8')
        if 'startStation = Math.Max(axisStart, startStation)' in approach_generator or \
           'endStation = Math.Min(axisEnd, endStation)' in approach_generator:
            fail('Step 5 vẫn silent-clamp candidate vào miền RoadAxis.')
        for token in ('"1.1", "1.2", "2.1", "2.2"', 'RequestedDistance', 'AnchorStopLineRecordId'):
            if token not in approach_generator:
                fail(f'Step 5 thiếu contract/metadata {token}.')

    # SOURCE_MANIFEST.sha256 phải phản ánh đúng source hiện tại.
    manifest_path = ROOT / 'SOURCE_MANIFEST.sha256'
    if not manifest_path.exists():
        fail('Thiếu SOURCE_MANIFEST.sha256.')
    else:
        manifest_entries = {}
        for line in manifest_path.read_text(encoding='utf-8').splitlines():
            line = line.strip()
            if not line:
                continue
            match = re.match(r'^([0-9a-fA-F]{64})\s{2}(.+)$', line)
            if not match:
                fail(f'Manifest line không hợp lệ: {line}')
                continue
            manifest_entries[match.group(2)] = match.group(1).lower()

        try:
            tracked_raw = subprocess.check_output(['git', 'ls-files', '-z'], cwd=ROOT)
            tracked_files = {
                p for p in tracked_raw.decode('utf-8').split('\0')
                if p and p != 'SOURCE_MANIFEST.sha256'
                and not any(part in {'.git', '.vs', 'bin', 'obj'} for part in Path(p).parts)
            }
        except Exception as exc:
            tracked_files = set()
            fail(f'Không đọc được danh sách git tracked files: {exc}')

        manifest_files = set(manifest_entries)
        missing_manifest = sorted(tracked_files - manifest_files)
        extra_manifest = sorted(manifest_files - tracked_files)
        if missing_manifest:
            fail('Manifest thiếu tracked files: ' + ', '.join(missing_manifest[:20]))
        if extra_manifest:
            fail('Manifest có file không còn tracked: ' + ', '.join(extra_manifest[:20]))

        for rel, expected_hash in manifest_entries.items():
            target = ROOT / rel
            if not target.exists() or not target.is_file():
                fail(f'Manifest trỏ tới file không tồn tại: {rel}')
                continue
            actual_hash = hashlib.sha256(target.read_bytes()).hexdigest()
            if actual_hash != expected_hash:
                fail(f'Manifest stale: {rel}')

    bad_dirs = [p for name in ('bin', 'obj', '.vs') for p in ROOT.rglob(name) if p.is_dir()]
    if bad_dirs:
        fail('Có build/cache directory trong source: ' + ', '.join(str(p.relative_to(ROOT)) for p in bad_dirs[:10]))

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
    print(' - 7.1↔7.3: configurable, center-to-center contract preserved')
    print(' - 7.3 zebra: transverse bars, stripe count driven by crossing length')
    print(' - CAD queue: document-bound + heavy-action single-flight')
    print(' - DWG state schema: 11')
    print(' - build/cache directories: none')
    return 0


if __name__ == '__main__':
    sys.exit(main())
