(function(ARM){
    "use strict";
    const byId = ARM.byId;
    const s5State = {
        roads: [],
        layers: [],
        groups: [],
        quantityRows: [],
        currentRoad: "",
        objectType: "speed_hump",
        speedMode: "uniform",
        scope: "segment",
        selectedBlocks: [],
        manualSelection: [],
        blockRules: [],
        previewValid: false,
        previewStale: true
    };

    const s5LabelForType = type => ({
        speed_hump: ["TẠO VẠCH GỜ GIẢM TỐC", "Chọn phạm vi dọc tuyến, hai đường giới hạn bề rộng và quy tắc rải.", "Bán tự động theo TIM"],
        manual_marking: ["GÁN LAYER CHO ĐỐI TƯỢNG", "Chọn các đối tượng CAD đã có, chọn Layer vạch và hoàn thành.", "Gán Layer cho hình học hiện hữu"],
        manual_block: ["TIẾP NHẬN BLOCK CÓ SẴN", "Chọn BlockReference; hệ thống quản lý theo Tuyến + BlockName và tự đưa vào khối lượng chung.", "Quản lý theo Tuyến + BlockName"]
    }[type] || ["VẠCH PHÁT SINH", "", ""]);

    const s5RoadName = value => {
        const road = s5State.roads.find(x => String(x.id ?? x.key ?? x.name ?? x.Name ?? "") === String(value));
        return road ? String(road.name ?? road.Name ?? road.id ?? value) : String(value || "");
    };

    const s5LayerInfo = value => s5State.layers.find(x => String(x.id ?? x.templateId ?? x.code ?? x.name ?? "") === String(value));

    window.setTab5Roads = function(roads){
        s5State.roads = Array.isArray(roads) ? roads.slice() : [];
        const select = byId("s5RoadSelect");
        if(!select) return;
        const current = select.value;
        select.innerHTML = '<option value="">Chọn tên đường / Alignment...</option>' + s5State.roads.map((r,i)=>{
            const id = String(r.id ?? r.key ?? r.roadKey ?? r.name ?? r.Name ?? `ROAD_${i+1}`);
            const name = String(r.name ?? r.Name ?? r.roadName ?? id);
            const type = String(r.axisType ?? r.type ?? "");
            return `<option value="${id}">${name}${type ? ` · ${type}` : ""}</option>`;
        }).join("");
        if([...select.options].some(o=>o.value===current)) select.value=current;
    };


    window.setTab5ManualSelection = function(items){
        s5State.manualSelection = Array.isArray(items) ? items.slice() : [];
        const count = s5State.manualSelection.length;
        if(byId("s5ManualSelectedCount")) byId("s5ManualSelectedCount").textContent = `${count} đối tượng`;

        const types = [...new Set(s5State.manualSelection.map(x=>String(x.type ?? x.Type ?? "")).filter(Boolean))];
        const layers = [...new Set(s5State.manualSelection.map(x=>String(x.layer ?? x.Layer ?? "")).filter(Boolean))];
        let summary = "Chưa chọn đối tượng.";
        if(count){
            const typeText = types.length ? types.slice(0,3).join(", ") : "CAD Entity";
            const layerText = layers.length ? ` · Layer hiện tại: ${layers.slice(0,2).join(", ")}${layers.length>2?"…":""}` : "";
            summary = `${typeText}${layerText}`;
        }
        if(byId("s5ManualSelectionSummary")) byId("s5ManualSelectionSummary").textContent = summary;
        window.updateTab5ActionState();
    };

    window.resetTab5ManualSelection = function(message){
        s5State.manualSelection = [];
        if(byId("s5ManualSelectedCount")) byId("s5ManualSelectedCount").textContent = "0 đối tượng";
        if(byId("s5ManualSelectionSummary")) byId("s5ManualSelectionSummary").textContent = message || "Chưa chọn đối tượng.";
        window.updateTab5ActionState();
    };

    window.moveTab5LayerSelector = function(type){
        const select = byId("s5LayerTemplate");
        let targetId = "s5LayerTemplateSlot";
        if(type === "speed_hump") targetId = "s5SpeedLayerSlot";
        else if(type === "manual_marking") targetId = "s5ManualLayerSlot";
        const target = byId(targetId);
        if(select && target && select.parentElement !== target) target.appendChild(select);
    };

    window.setTab5LayerTemplates = function(layers){
        s5State.layers = Array.isArray(layers) ? layers.slice() : [];
        const select = byId("s5LayerTemplate");
        if(!select) return;
        const current = select.value;
        select.innerHTML = '<option value="">Chọn layer từ thư viện Tab 1...</option>' + s5State.layers.map((l,i)=>{
            const id = String(l.id ?? l.templateId ?? l.code ?? l.name ?? `LT_${i+1}`);
            const name = String(l.name ?? l.layerName ?? l.code ?? id);
            const desc = String(l.description ?? l.moTa ?? "");
            return `<option value="${id}">${name}${desc ? ` · ${desc}` : ""}</option>`;
        }).join("");
        if([...select.options].some(o=>o.value===current)) select.value=current;
        const blockLayer = byId("s5BlockRuleLayer");
        if(blockLayer){
            const curBlockLayer = blockLayer.value;
            blockLayer.innerHTML = '<option value="">Chọn layer vạch...</option>' + s5State.layers.map((l,i)=>{
                const id = String(l.id ?? l.templateId ?? l.code ?? l.name ?? `LT_${i+1}`);
                const name = String(l.name ?? l.layerName ?? l.code ?? id);
                return `<option value="${id}">${name}</option>`;
            }).join("");
            if([...blockLayer.options].some(o=>o.value===curBlockLayer)) blockLayer.value=curBlockLayer;
        }
        window.onTab5LayerChange();
    };

    window.resetTab5RoadSession = function(){
        s5State.speedMode = "uniform";
        s5State.scope = "segment";
        for(const id of ["s5Boundary1","s5Boundary2","s5StartStation","s5EndStation","s5ClusterAnchor"]){ const el=byId(id); if(el) el.textContent="Chưa chọn"; }
        if(byId("s5ClusterDirectionPoint")){ byId("s5ClusterDirectionPoint").textContent="Chưa xác định"; byId("s5ClusterDirectionPoint").classList.remove("is-ready"); }
        if(byId("s5ClusterDirectionSign")) byId("s5ClusterDirectionSign").value="1";
        for(const id of ["s5PreviewRange","s5PreviewLength","s5PreviewArea"]){ const el=byId(id); if(el) el.textContent="—"; }
        if(byId("s5PreviewCount")) byId("s5PreviewCount").textContent="0";
        if(byId("s5PreviewGroup")) byId("s5PreviewGroup").textContent="Tạo mới";
        if(byId("s5ManualGroupId")) byId("s5ManualGroupId").textContent="Tạo nhóm mới khi đặt vạch đầu tiên";
        s5State.selectedBlocks = [];
        window.renderTab5SelectedBlocks();
        if(byId("s5LayerTemplate")) byId("s5LayerTemplate").value="";
        window.setTab5ObjectType("speed_hump");
        window.setTab5SpeedMode("uniform");
        window.setTab5Scope("segment");
        window.onTab5LayerChange();
    };

    window.onTab5RoadChange = function(){
        const select = byId("s5RoadSelect");
        s5State.currentRoad = select?.value || "";
        window.resetTab5RoadSession();
        const name = s5RoadName(s5State.currentRoad);
        const road = s5State.roads.find(x => String(x.id ?? x.key ?? x.roadKey ?? x.axisKey ?? x.name ?? x.Name ?? "") === String(s5State.currentRoad));
        const axisType = String(road?.axisType ?? road?.type ?? "").toUpperCase();
        const axisLabel = axisType === "ALIGNMENT" ? "Alignment Civil 3D" : axisType === "POLYLINE" ? "Polyline CAD" : "Polyline CAD / Alignment";
        if(byId("s5RoadIdentity")) byId("s5RoadIdentity").textContent = name || "—";
        if(byId("s5RoadAxis")) byId("s5RoadAxis").textContent = s5State.currentRoad ? axisLabel : "—";
        if(byId("s5ManagerRoad")) byId("s5ManagerRoad").textContent = name || "Chưa chọn tuyến";
        
        window.updateTab5ActionState();
        window.renderTab5Groups();
        if(typeof window.refreshQuantityFromCad === "function") window.refreshQuantityFromCad();
    };

    window.setTab5ObjectType = function(type){
        type = ["speed_hump","manual_marking","manual_block"].includes(type) ? type : "speed_hump";
        s5State.objectType = type;
        if(byId("s5ObjectType")) byId("s5ObjectType").value = type;
        const objectDisplay = byId("s5ObjectTypeDisplay");
        if(objectDisplay) objectDisplay.textContent = type === "speed_hump" ? "Gờ giảm tốc" : type === "manual_marking" ? "Vạch mặt bằng" : "Block có sẵn";
        const s5Shell = document.querySelector("#tabPhatSinh .s5-shell");
        if(s5Shell) s5Shell.dataset.blockMode = type === "manual_block" ? "true" : "false";
        if(s5Shell) s5Shell.dataset.objectType = type;
        document.querySelectorAll("#tabPhatSinh [data-s5-type]").forEach(b=>b.classList.toggle("active", b.dataset.s5Type===type));
        if(byId("s5SpeedPanel")) byId("s5SpeedPanel").hidden = type !== "speed_hump";
        if(byId("s5ManualMarkingPanel")) byId("s5ManualMarkingPanel").hidden = type !== "manual_marking";
        if(byId("s5ManualBlockPanel")) byId("s5ManualBlockPanel").hidden = type !== "manual_block";
        const labels=s5LabelForType(type);
        if(byId("s5CreateTitle")) byId("s5CreateTitle").textContent=labels[0];
        if(byId("s5CreateSub")) byId("s5CreateSub").textContent=labels[1];
        if(byId("s5ObjectModeText")) byId("s5ObjectModeText").textContent=labels[2];

        const isBlock = type === "manual_block";
        const isManual = type === "manual_marking";
        const decisions = document.querySelector("#tabPhatSinh .s5pro-decisions");
        if(decisions) decisions.classList.toggle("manual-assign-mode", isManual);

        window.moveTab5LayerSelector(type);

        if(byId("s5LayerTemplate")) byId("s5LayerTemplate").disabled = isBlock;
        if(byId("s5QuickLayerBtn")) byId("s5QuickLayerBtn").disabled = isBlock;
        if(byId("s5LayerCadTitle")) byId("s5LayerCadTitle").textContent = isBlock ? "BLOCKNAME" : "LAYER CAD";
        if(byId("s5LayerResolveLabel")) byId("s5LayerResolveLabel").textContent = isBlock ? "Định danh" : "Layer CAD";
        if(byId("s5ResolvedLayer") && isBlock) byId("s5ResolvedLayer").textContent = "Tuyến + BlockName";

        if(byId("s5SpeedSummary")) byId("s5SpeedSummary").hidden = type !== "speed_hump";
        if(byId("s5SecondaryBtn")) byId("s5SecondaryBtn").hidden = type !== "speed_hump";
        if(byId("s5GenerateBtn")) byId("s5GenerateBtn").hidden = type !== "speed_hump";
        if(byId("s5SecondaryBtn")) byId("s5SecondaryBtn").textContent = "KIỂM TRA BỐ TRÍ";
        if(byId("s5GenerateBtn")) byId("s5GenerateBtn").textContent = "TẠO VẠCH GỜ GIẢM TỐC";

        window.updateTab5ActionState();
        if(!isBlock) window.onTab5LayerChange();
    };

    window.setTab5PanelLocked = function(panel, locked){
        if(!panel) return;
        panel.classList.toggle("is-mode-locked", !!locked);
        panel.classList.toggle("is-active-mode", !locked);
        panel.setAttribute("aria-disabled", locked ? "true" : "false");
        panel.querySelectorAll("input:not([type='hidden']), select, button").forEach(el=>{
            el.disabled = !!locked;
        });
        const mode = panel.dataset.s5ModePanel || "";
        const state = document.querySelector(`#tabPhatSinh [data-s5-mode-state="${mode}"]`);
        if(state) state.textContent = locked ? "ĐANG KHÓA" : "ĐANG SỬ DỤNG";
    };

    window.setTab5ClusterCount = function(value){
        let count = Math.max(1, Math.min(3, Number(value) || 1));
        const input = byId("s5ClusterCount");
        if(input) input.value = String(count);

        document.querySelectorAll("#tabPhatSinh [data-s5-cluster-count]").forEach(btn=>{
            btn.classList.toggle("active", Number(btn.dataset.s5ClusterCount) === count);
        });

        const patterns = {
            1: "1 cụm: 7 gờ gần vùng nguy hiểm · khoảng cách nội cụm 0.40 m.",
            2: "2 cụm: xe gặp 6 → 7 gờ trước vùng nguy hiểm · khoảng cách nội cụm 0.40 m.",
            3: "3 cụm: xe gặp 5 → 6 → 7 gờ trước vùng nguy hiểm · khoảng cách nội cụm 0.40 m."
        };
        if(byId("s5ClusterRuleSummary")) byId("s5ClusterRuleSummary").textContent = patterns[count];

        const btn = byId("s5ClusterDirectionBtn");
        if(btn) btn.disabled = byId("s5ClusterPanel")?.classList.contains("is-mode-locked") || false;

        window.invalidateTab5Preview("Số cụm đã thay đổi");
    };

    window.setTab5SpeedMode = function(mode){
        mode = mode === "cluster" ? "cluster" : "uniform";
        s5State.speedMode = mode;

        const speedPanel = byId("s5SpeedPanel");
        if(speedPanel) speedPanel.dataset.speedMode = mode;

        document.querySelectorAll("#tabPhatSinh [data-s5-speed-mode]").forEach(b=>{
            const active = b.dataset.s5SpeedMode===mode;
            b.classList.toggle("active", active);
            b.setAttribute("aria-pressed", active ? "true" : "false");
        });

        const uniformPanel = byId("s5UniformPanel");
        const clusterPanel = byId("s5ClusterPanel");

        const applyModeVisibility = (panel, active)=>{
            if(!panel) return;
            window.setTab5PanelLocked(panel, !active);
            panel.hidden = !active;
            panel.setAttribute("aria-hidden", active ? "false" : "true");
            panel.style.display = active ? "" : "none";
        };

        applyModeVisibility(uniformPanel, mode === "uniform");
        applyModeVisibility(clusterPanel, mode === "cluster");

        if(mode === "cluster"){
            window.setTab5ClusterCount(Number(byId("s5ClusterCount")?.value || 3));
        }

        if(byId("s5PreviewRange")) byId("s5PreviewRange").textContent = "—";
        if(byId("s5PreviewCount")) byId("s5PreviewCount").textContent = "0";
        if(byId("s5PreviewLength")) byId("s5PreviewLength").textContent = "—";
        if(byId("s5PreviewArea")) byId("s5PreviewArea").textContent = "—";
        if(byId("s5PreviewGroup")) byId("s5PreviewGroup").textContent = "Tạo mới";

        s5State.previewValid=false;
        s5State.previewStale=true;
        window.updateTab5ActionState(
            mode === "uniform"
                ? "Đang cấu hình Rải đều"
                : "Đang cấu hình Theo cụm"
        );
    };

    window.setTab5SpeedPreviewStats = function(data){
        data = data || {};
        const text = (value, fallback="—") => value === undefined || value === null || value === "" ? fallback : String(value);
        if(byId("s5PreviewRange")) byId("s5PreviewRange").textContent = text(data.range ?? data.rangeText ?? data.stationRange);
        if(byId("s5PreviewCount")) byId("s5PreviewCount").textContent = text(data.count ?? data.barCount, "0");
        if(byId("s5PreviewLength")) byId("s5PreviewLength").textContent = text(data.lengthText ?? (Number.isFinite(Number(data.totalLength)) ? `${Number(data.totalLength).toFixed(2)} m` : null));
        if(byId("s5PreviewArea")) byId("s5PreviewArea").textContent = text(data.areaText ?? (Number.isFinite(Number(data.totalArea)) ? `${Number(data.totalArea).toFixed(2)} m²` : null));
        if(byId("s5PreviewGroup")) byId("s5PreviewGroup").textContent = text(data.groupId ?? data.group, "Tạo mới");
        const policyValid = data.policyValid !== false;
        s5State.previewValid = policyValid;
        s5State.previewStale = false;
        window.updateTab5ActionState(policyValid ? "" : (data.policyMessage || "Không đạt quy tắc bố trí"));
    };

    window.setTab5Scope = function(){
        s5State.scope = "segment";
        if(byId("s5SegmentFields")) byId("s5SegmentFields").style.display = "grid";
        s5State.previewValid=false; s5State.previewStale=true;
        window.updateTab5ActionState("Đoạn cần giảm tốc đã thay đổi");
    };

    window.onTab5LayerChange = function(){
        const layer = s5LayerInfo(byId("s5LayerTemplate")?.value || "");
        const road = s5RoadName(s5State.currentRoad);
        const templateName = layer ? String(layer.name ?? layer.layerName ?? layer.code ?? layer.id ?? "") : "";
        const code = layer ? String(layer.markingCode ?? layer.code ?? layer.maVach ?? templateName) : "";
        if(byId("s5MarkingCode")) byId("s5MarkingCode").textContent = code || "—";
        if(byId("s5ManualMarkingCode")) byId("s5ManualMarkingCode").textContent = code || "Lấy từ layer vạch đang chọn";
        const resolvedLayer = road && templateName ? `${road}__${templateName}` : "Chọn tuyến và layer vạch";
        if(byId("s5ResolvedLayer")){
            byId("s5ResolvedLayer").textContent = s5State.objectType === "manual_block"
                ? "Giữ layer CAD hiện tại · ánh xạ qua Block Rule"
                : resolvedLayer;
        }
        if(byId("s5ManualResolvedLayer")) byId("s5ManualResolvedLayer").textContent = resolvedLayer;
        window.updateTab5ActionState();
    };

    const s5Picked = id => { const t=(byId(id)?.textContent || "").trim(); return !!t && t!=="Chưa chọn" && t!=="—"; };
    window.invalidateTab5Preview = function(reason){
        if(s5State.objectType !== "speed_hump") return;
        s5State.previewValid=false; s5State.previewStale=true;
        if(byId("s5PreviewRange")) byId("s5PreviewRange").textContent="CẦN KIỂM TRA LẠI";
        if(byId("s5PreviewCount")) byId("s5PreviewCount").textContent="—";
        if(byId("s5PreviewLength")) byId("s5PreviewLength").textContent="—";
        if(byId("s5PreviewArea")) byId("s5PreviewArea").textContent="—";
        window.updateTab5ActionState(reason || "Tham số đã thay đổi");
    };
    window.updateTab5ActionState = function(reason){
        const missing=[];
        const hasRoad=!!s5State.currentRoad;
        const layerSelected=!!(byId("s5LayerTemplate")?.value || "");
        if(!hasRoad) missing.push("Tuyến");
        if(s5State.objectType !== "manual_block" && !layerSelected) missing.push("Layer vạch");

        if(s5State.objectType === "speed_hump"){
            if(!s5Picked("s5Boundary1")) missing.push("Giới hạn 1");
            if(!s5Picked("s5Boundary2")) missing.push("Giới hạn 2");
            if(s5State.speedMode === "uniform"){
                if(!s5Picked("s5StartStation")) missing.push("Điểm đầu");
                if(!s5Picked("s5EndStation")) missing.push("Điểm cuối");
                const us=Number(byId("s5UniformSpacing")?.value);
                if(!(us>=3 && us<=5)) missing.push("Khoảng cách gờ 3–5 m");
            }else{
                if(!s5Picked("s5ClusterAnchor")) missing.push("Mốc vùng nguy hiểm");
                const cc=Number(byId("s5ClusterCount")?.value); if(!(cc>=1 && cc<=3)) missing.push("Số cụm 1–3");
                if(!s5Picked("s5ClusterDirectionPoint")) missing.push("Phía xe tiếp cận");
                const cg=Number(byId("s5ClusterSpacing")?.value);
                if(!(cg>=15 && cg<=30)) missing.push("Khoảng trống cụm 15–30 m");
                const hz=Number(byId("s5ClusterOffset")?.value);
                if(!(hz>=0 && hz<=20)) missing.push("Khoảng cách tới vùng nguy hiểm ≤20 m");
            }
            if(!s5State.previewValid) missing.push("Preview hợp lệ");
        }
        if(s5State.objectType === "manual_marking"){
            const items=Array.isArray(s5State.manualSelection)?s5State.manualSelection:[];
            if(!items.length) missing.push("Đối tượng đã chọn");
        }
        if(s5State.objectType === "manual_block"){
            const items=Array.isArray(s5State.selectedBlocks)?s5State.selectedBlocks:[];
            if(!items.length) missing.push("Block đã chọn");
        }
        const primary=byId("s5GenerateBtn"); if(primary) primary.disabled = s5State.objectType !== "speed_hump" || missing.length>0;
        const manualFinish=byId("s5ManualFinishBtn"); if(manualFinish) manualFinish.disabled = s5State.objectType !== "manual_marking" || missing.length>0;
        const blockFinish=byId("s5BlockSyncButton"); if(blockFinish) blockFinish.disabled = s5State.objectType !== "manual_block" || missing.length>0;
        const secondary=byId("s5SecondaryBtn"); if(secondary) secondary.disabled = s5State.objectType !== "speed_hump" || !hasRoad || !layerSelected;
        const msg=byId("s5ValidationMessage");
        if(msg){
            if(!missing.length){msg.className="s5-validation-line ready";msg.textContent=s5State.objectType==="speed_hump"?"Sẵn sàng sinh/cập nhật · Preview đang khớp tham số hiện tại.":"Đủ điều kiện thực hiện tác vụ.";}
            else {msg.className="s5-validation-line warning";msg.textContent=`Còn thiếu: ${missing.join(" · ")}${reason?` · ${reason}`:""}`;}
        }
    };

    window.tab5SecondaryAction = function(){
        if(s5State.objectType === "manual_block") return window.goiAction('Tab5_CheckSelectedBlocks');
        return window.goiAction('Tab5_Preview');
    };

    window.tab5PrimaryAction = function(){
        if(s5State.objectType === "manual_block") return window.goiAction('Tab5_SyncExistingBlockProperties');
        return window.goiAction('Tab5_GenerateOrUpdate');
    };

    window.setTab5SelectedExistingBlocks = function(items){
        s5State.selectedBlocks = Array.isArray(items) ? items.slice() : [];
        window.renderTab5SelectedBlocks();
        window.updateTab5ActionState();
    };

    window.setTab5BlockRules = function(rules){
        // Legacy state is retained for backward compatibility only.
        // UI 030 no longer uses BlockName -> Rule/Layer mapping.
        s5State.blockRules = Array.isArray(rules) ? rules.slice() : [];
    };

    const s5BlockNameKey = item => String(item.effectiveBlockName ?? item.blockName ?? item.name ?? item.BlockName ?? "UNKNOWN_BLOCK");
    const s5IsManagedBlock = item => !!(item.isManaged ?? item.armManaged ?? item.entityId ?? item.armEntityId ?? item.ARMEntityId);

    window.renderTab5SelectedBlocks = function(){
        const body = byId("s5SelectedBlockBody");
        const items = Array.isArray(s5State.selectedBlocks) ? s5State.selectedBlocks : [];
        const groups = new Map();

        for(const item of items){
            const name = s5BlockNameKey(item);
            if(!groups.has(name)) groups.set(name,{name,count:0,managed:0});
            const g = groups.get(name);
            g.count++;
            if(s5IsManagedBlock(item)) g.managed++;
        }

        const rows=[...groups.values()].sort((a,b)=>a.name.localeCompare(b.name,"vi"));
        if(body){
            body.innerHTML = rows.length ? rows.map(g=>{
                const status = g.managed===g.count
                    ? '<span class="text-green">ĐÃ QUẢN LÝ</span>'
                    : g.managed
                        ? `<span class="text-yellow">${g.managed}/${g.count} ĐÃ QL</span>`
                        : '<span class="text-yellow">CHƯA QUẢN LÝ</span>';
                return `<tr><td title="${g.name}">${g.name}</td><td>${g.count}</td><td>${status}</td></tr>`;
            }).join("") : '<tr><td colspan="3" class="s5-block-empty">Chưa chọn Block trên CAD.</td></tr>';
        }

        const managed=items.filter(s5IsManagedBlock).length;
        const unmanaged=items.length-managed;
        if(byId("s5BlockSelectedCount")) byId("s5BlockSelectedCount").textContent=`${items.length} Block`;
        if(byId("s5BlockSelectedStat")) byId("s5BlockSelectedStat").textContent=String(items.length);
        if(byId("s5BlockManagedCount")) byId("s5BlockManagedCount").textContent=String(managed);
        if(byId("s5BlockUnmanagedCount")) byId("s5BlockUnmanagedCount").textContent=String(unmanaged);
        if(byId("s5BlockTypeCount")) byId("s5BlockTypeCount").textContent=String(rows.length);

        if(byId("s5ExistingBlockStatus")){
            byId("s5ExistingBlockStatus").textContent = items.length
                ? `${rows.length} BlockName · ${items.length} Block`
                : "Chưa chọn Block.";
        }

        const readyLine=byId("s5BlockSyncReady");
        if(readyLine){
            readyLine.textContent = items.length
                ? `${items.length} Block sẽ được quản lý theo Tuyến + BlockName. Layer CAD chỉ là thuộc tính hiển thị.`
                : "Chọn Block để nạp vào hệ thống quản lý.";
        }

        const syncBtn=byId("s5BlockSyncButton");
        if(syncBtn) syncBtn.textContent = items.length
            ? `NẠP ${items.length} BLOCK VÀO QUẢN LÝ`
            : "NẠP BLOCK VÀO QUẢN LÝ";

        window.updateTab5ActionState();
    };

    window.loadTab5BlockRuleTarget = function(){};
    window.loadTab5ExistingBlockRule = function(){};

    window.toggleTab5QuickLayer = function(force){
        const box=byId("s5QuickLayer"); if(!box) return;
        box.hidden = typeof force === "boolean" ? !force : !box.hidden;
    };

    window.createTab5QuickLayer = function(){
        const code=(byId("s5QuickLayerCode")?.value || "").trim();
        if(!code) return;
        const id=`LT_TAB5_${Date.now()}`;
        const template={id,name:code,code,description:(byId("s5QuickLayerDescription")?.value || "").trim(),linetype:(byId("s5QuickLayerLinetype")?.value || "CONTINUOUS"),width:Number(byId("s5QuickLayerWidth")?.value || 0),colorHex:(byId("s5QuickLayerColor")?.value || "#ffffff"),scale:Number(byId("s5QuickLayerScale")?.value || 1),standardRef:(byId("s5QuickLayerStandardRef")?.value || "").trim(),createdFrom:"TAB5"};
        s5State.layers.push(template);
        window.setTab5LayerTemplates(s5State.layers);
        if(byId("s5LayerTemplate")) byId("s5LayerTemplate").value=id;
        if(byId("s5LayerSource")) byId("s5LayerSource").textContent="PHÁT SINH · Shared Layer Library";
        window.onTab5LayerChange();
        window.toggleTab5QuickLayer(false);
        if(typeof window.goiAction === "function") window.goiAction("Tab5_RegisterQuickLayerTemplate");
    };

    window.setTab5ManagedGroups = function(groups){
        // ManagedGroups vẫn được giữ để các thao tác legacy (lock/zoom/remove) hoạt động.
        // Bảng khối lượng bên phải KHÔNG dùng danh sách này nữa.
        s5State.groups = Array.isArray(groups) ? groups.slice() : [];
    };

    window.setTab5QuantityRows = function(rows){
        s5State.quantityRows = Array.isArray(rows) ? rows.slice() : [];
        window.renderTab5Groups();
    };

    const s5QuantityProp = (row,...names)=>{
        for(const name of names){
            if(row && row[name] !== undefined && row[name] !== null) return row[name];
            const camel=name.charAt(0).toLowerCase()+name.slice(1);
            if(row && row[camel] !== undefined && row[camel] !== null) return row[camel];
        }
        return "";
    };

    const s5QuantitySourceLabel = source=>{
        const value=String(source||"").toUpperCase();
        if(value==="AUTO_LONGITUDINAL") return "TAB 3 · VẠCH DỌC";
        if(value==="AUTO_EDGE") return "TAB 3 · VẠCH MÉP";
        if(value==="AUTO_INTERSECTION") return "TAB 3 · NÚT GIAO";
        if(value==="AUTO_BLOCK") return "TAB 4 · 7.6/9.3";
        if(value==="SUPPLEMENTARY_SPEED_HUMP") return "TAB 5 · GỜ GIẢM TỐC";
        if(value==="SUPPLEMENTARY_MANUAL") return "TAB 5 · VẠCH MẶT BẰNG";
        if(value==="SUPPLEMENTARY_EXTERNAL_BLOCK") return "TAB 5 · BLOCK CÓ SẴN";
        if(value==="MANUAL_LAYER_RULE") return "CAD · LAYER RULE";
        if(value==="ARM_METADATA") return "ARM METADATA";
        return value || "ARM";
    };

    const s5QuantityNumber = value=>{
        const n=Number(value);
        return Number.isFinite(n) ? n : 0;
    };

    window.renderTab5Groups = function(){
        const body=byId("s5ManagedBody");
        if(!body) return;

        const roadValue=String(s5State.currentRoad || "");
        const road=s5State.roads.find(x =>
            String(x.id ?? x.key ?? x.roadKey ?? x.axisKey ?? x.effectiveAxisKey ?? x.name ?? x.Name ?? "")===roadValue
        );
        const roadName=s5RoadName(roadValue);
        const roadKey=String(
            road?.effectiveAxisKey ?? road?.axisKey ?? road?.roadKey ?? road?.key ?? road?.id ?? roadValue
        );

        let rows=(Array.isArray(s5State.quantityRows)?s5State.quantityRows:[]).filter(r=>{
            if(!roadValue) return false;
            const axisKey=String(s5QuantityProp(r,"AxisKey","RoadKey")||"");
            const rName=String(s5QuantityProp(r,"RoadName","OwnerName","Road")||"");
            return (!!roadKey && axisKey.toLowerCase()===roadKey.toLowerCase()) ||
                   (!!roadName && rName.toLowerCase()===roadName.toLowerCase());
        });

        rows.sort((a,b)=>{
            const sa=s5QuantitySourceLabel(s5QuantityProp(a,"Source"));
            const sb=s5QuantitySourceLabel(s5QuantityProp(b,"Source"));
            if(sa!==sb) return sa.localeCompare(sb,"vi");
            const ca=String(s5QuantityProp(a,"BlockName","MarkingCode","Code"));
            const cb=String(s5QuantityProp(b,"BlockName","MarkingCode","Code"));
            return ca.localeCompare(cb,"vi");
        });

        if(!roadValue){
            body.innerHTML='<tr class="s5-empty-row"><td colspan="9">Chọn tuyến để xem khối lượng dùng chung.</td></tr>';
        }else if(!rows.length){
            body.innerHTML='<tr class="s5-empty-row"><td colspan="9">Tuyến này chưa có bản ghi khối lượng ARM. Tạo đối tượng ở Tab 3/4/5 rồi dữ liệu sẽ tự xuất hiện tại đây.</td></tr>';
        }else{
            body.innerHTML=rows.map((r,i)=>{
                const source=s5QuantitySourceLabel(s5QuantityProp(r,"Source"));
                const count=s5QuantityNumber(s5QuantityProp(r,"Count"));
                const unit=String(s5QuantityProp(r,"QuantityUnit") || (count>0?"cái":"m²"));
                const blockName=String(s5QuantityProp(r,"BlockName")||"");
                const code=String(blockName || s5QuantityProp(r,"MarkingCode","Code") || "—");
                const layer=String(s5QuantityProp(r,"CadLayer")||"—");
                const paintedLength=s5QuantityNumber(s5QuantityProp(r,"PaintedLength","GeometryLength","Length"));
                const paintedArea=s5QuantityNumber(s5QuantityProp(r,"PaintedArea","Area"));
                const qty=unit.toLowerCase().includes("cái") ? Math.max(1,count) : 1;
                const recordId=String(s5QuantityProp(r,"RecordId","Id")||"");
                const handle=String(s5QuantityProp(r,"ObjectHandle","Handle")||"");

                return `<tr>
                    <td>${i+1}</td>
                    <td title="${source}">${source}</td>
                    <td title="${code}">${code}</td>
                    <td title="${layer}">${layer}</td>
                    <td>${qty}</td>
                    <td>${unit}</td>
                    <td>${paintedLength.toFixed(2)} m</td>
                    <td>${paintedArea.toFixed(2)} m²</td>
                    <td><button class="btn-outline" onclick="window.armZoomQuantity?.('${recordId}','${handle}')">⌖</button></td>
                </tr>`;
            }).join("");
        }

        const blockCount=rows
            .filter(r=>String(s5QuantityProp(r,"QuantityUnit")).toLowerCase().includes("cái") || s5QuantityNumber(s5QuantityProp(r,"Count"))>0)
            .reduce((sum,r)=>sum+Math.max(1,s5QuantityNumber(s5QuantityProp(r,"Count"))),0);
        const lengthTotal=rows.reduce((sum,r)=>sum+s5QuantityNumber(s5QuantityProp(r,"PaintedLength","GeometryLength","Length")),0);
        const areaTotal=rows.reduce((sum,r)=>sum+s5QuantityNumber(s5QuantityProp(r,"PaintedArea","Area")),0);

        if(byId("s5GroupCount")) byId("s5GroupCount").textContent=`${rows.length} bản ghi`;
        if(byId("s5EntityCount")) byId("s5EntityCount").textContent=`${blockCount} block / ký hiệu`;
        if(byId("s5ManagerGroupStat")) byId("s5ManagerGroupStat").textContent=String(rows.length);
        if(byId("s5ManagerEntityStat")) byId("s5ManagerEntityStat").textContent=String(blockCount);
        if(byId("s5ManagerLengthStat")) byId("s5ManagerLengthStat").textContent=`${lengthTotal.toFixed(2)} m`;
        if(byId("s5ManagerAreaStat")) byId("s5ManagerAreaStat").textContent=`${areaTotal.toFixed(2)} m²`;
    };

})(window.ARM);
