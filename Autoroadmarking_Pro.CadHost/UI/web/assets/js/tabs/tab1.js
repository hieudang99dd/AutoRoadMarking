(function(ARM){
    "use strict";
    const byId = ARM.byId;
    // ===== TAB 1 · MANAGEMENT TABLE ADAPTER =====
    // Tab 1 khởi động với thư viện rỗng. Dữ liệu chỉ đến từ:
    // 1) người dùng tạo thủ công ở form bên trái; hoặc
    // 2) người dùng chủ động nạp CSV.
    // Host cập nhật danh sách bằng armSetTab1LayerRows(realRows, { source:"host" }).
    window.__armTab1DemoMode = false;
    window.__armTab1LayerRows = [];

    const t1esc = v => String(v ?? "")
        .replaceAll("&","&amp;").replaceAll("<","&lt;")
        .replaceAll(">","&gt;").replaceAll('"',"&quot;").replaceAll("'","&#39;");

    const t1num = (v, digits=2) => {
        const n=Number(v);
        return Number.isFinite(n) ? n.toFixed(digits) : "—";
    };

    window.armSetTab1LayerRows = function(rows, options){
        options=options||{};
        window.__armTab1LayerRows = Array.isArray(rows) ? rows.map(r=>({...r})) : [];
        // Không còn dữ liệu demo: mọi dòng hiển thị đều là dữ liệu thư viện thật.
        window.__armTab1DemoMode = false;
        window.renderLayerManager?.();
    };

    window.armGetTab1LayerRows = function(){
        return Array.isArray(window.__armTab1LayerRows)
            ? window.__armTab1LayerRows.map(r=>({...r}))
            : [];
    };

    window.t1LineConfigLabel = function(r){
        const d1=Number(r.CustomDash1 ?? r.Dash ?? 0);
        const g1=Number(r.CustomGap1 ?? r.Gap ?? 0);
        const d2=Number(r.CustomDash2 ?? 0);
        const g2=Number(r.CustomGap2 ?? 0);
        const phase=Number(r.CustomPhase ?? 0);

        let main="";
        if(g1<=0 && d2<=0 && g2<=0){
            main="LIỀN";
        }else{
            const parts=[d1,g1];
            if(d2>0 || g2>0) parts.push(d2,g2);
            main=parts.map(n=>Number(n).toFixed(2)).join(" / ");
        }

        const notes=[];
        if(phase) notes.push(`P=${phase.toFixed(2)}`);
        if(String(r.PresentationRule||"").includes("DUPLICATE")) notes.push("MCN ×2");
        if(String(r.GeometryType||"").includes("CROSSWALK")) notes.push("GEN 7.3");
        if(String(r.Code||"").toUpperCase()==="GGT") notes.push("GEN GGT");
        return {main, sub:notes.join(" · ") || "CUSTOM_REAL"};
    };

    window.t1PreviewSvg = function(r){
        const rgb=String(r.RGB||"255,255,255").split(",").map(x=>Math.max(0,Math.min(255,Number(x)||0)));
        const color=`rgb(${rgb[0]},${rgb[1]},${rgb[2]})`;
        const g1=Math.max(0,Number(r.CustomGap1 ?? r.Gap ?? 0));
        const d1=Math.max(.01,Number(r.CustomDash1 ?? r.Dash ?? 1));
        let dash="";
        if(g1>0){
            const scale=7;
            dash=`stroke-dasharray="${Math.max(2,d1*scale)} ${Math.max(2,g1*scale)}"`;
        }
        return `<div class="arm-layer-preview">
            <div class="arm-layer-preview-track">
                <svg viewBox="0 0 72 14" aria-hidden="true">
                    <line x1="2" y1="7" x2="70" y2="7" stroke="${color}" stroke-width="4" stroke-linecap="butt" ${dash}/>
                </svg>
            </div>
            <span class="arm-layer-preview-ratio">${g1>0 ? `${t1num(d1)}/${t1num(g1)}` : "LIỀN"}</span>
        </div>`;
    };

    window.renderLayerManager = function(){
        const body=byId("layerBody");
        if(!body) return;
        const q=String(byId("t1_Search")?.value||"").trim().toLowerCase();
        const sourceRows=Array.isArray(window.__armTab1LayerRows) ? window.__armTab1LayerRows : [];
        // Giữ sourceIndex của danh sách gốc để SỬA/NHÂN BẢN/XÓA luôn tác động
        // đúng layer, kể cả khi bảng đang được lọc bằng ô tìm kiếm.
        const rows=sourceRows
            .map((r,sourceIndex)=>({r,sourceIndex}))
            .filter(({r})=>{
                if(!q) return true;
                return [r.Code,r.TemplateId,r.Layer,r.Description,r.RGB,r.ManagementState,r.QuantityMethod]
                    .some(v=>String(v||"").toLowerCase().includes(q));
            });

        if(byId("lblTotalLayers")) byId("lblTotalLayers").textContent=String(rows.length);
        if(!rows.length){
            body.innerHTML=q
                ? '<tr><td colspan="12" class="empty-state">Không có layer phù hợp với từ khóa tìm kiếm.</td></tr>'
                : '<tr><td colspan="12" class="empty-state">Thư viện đang trống. Hãy tạo layer ở khung bên trái hoặc nạp CSV thư viện.</td></tr>';
            return;
        }

        body.innerHTML=rows.map(({r,sourceIndex})=>{
            const cfg=window.t1LineConfigLabel(r);
            const st=String(r.ManagementState||"NEW").toUpperCase();
            const stClass=st==="READY" ? "ready" : st==="ERROR" ? "error" : st==="DIRTY" ? "dirty" : "new";
            const stText=st==="READY" ? "ĐÃ CẬP NHẬT" : st==="ERROR" ? "LỖI" : st==="DIRTY" ? "CẦN CẬP NHẬT" : "CHƯA CẬP NHẬT";
            const handle=String(r.LayerHandle||r.Handle||"").trim();
            return `<tr data-index="${sourceIndex}">
                <td class="arm-cell-check"><input class="cbx-custom t1-row-check" type="checkbox" data-index="${sourceIndex}"/></td>
                <td title="${t1esc(r.TemplateId||"")}">
                    <div class="t1-code-cell">
                        <span class="t1-code-main">${t1esc(r.Code||r.MarkingCode||"—")}</span>
                        <small class="t1-template-id">${t1esc(r.TemplateId||"CHƯA SINH ID")}</small>
                    </div>
                </td>
                <td class="t1-layer-name" title="${t1esc(r.Layer||"")}">${t1esc(r.Layer||"—")}</td>
                <td title="${t1esc(r.Description||"")}">${t1esc(r.Description||"—")}</td>
                <td>
                    <div class="t1-config-cell">
                        <span class="t1-config-main">${t1esc(cfg.main)}</span>
                        <small class="t1-config-sub">${t1esc(cfg.sub)}</small>
                    </div>
                </td>
                <td>${t1num(r.Width)}</td>
                <td>${t1num(r.Scale ?? 1,2)}</td>
                <td>${t1esc(r.RGB||"—")}</td>
                <td>
                    <span class="t1-management-pill ${stClass}">${stText}</span>
                    <small class="t1-management-meta">${t1esc(r.QuantityMethod||"—")}</small>
                </td>
                <td><span class="t1-layer-handle ${handle ? "" : "empty"}">${t1esc(handle||"—")}</span></td>
                <td class="arm-preview-cell">${window.t1PreviewSvg(r)}</td>
                <td class="arm-cell-action">
                    <div class="arm-layer-actions">
                        <button class="btn-outline arm-layer-action edit" onclick="window.armDemoEditLayer(${sourceIndex})">SỬA</button>
                        <button class="btn-outline arm-layer-action duplicate" onclick="window.armDemoDuplicateLayer(${sourceIndex})">NHÂN BẢN</button>
                        <button class="btn-danger arm-layer-action delete" onclick="window.armDemoDeleteLayer(${sourceIndex})">XÓA</button>
                    </div>
                </td>
            </tr>`;
        }).join("");
    };

    window.toggleSelectAllLayers = function(cb){
        document.querySelectorAll("#layerBody .t1-row-check").forEach(x=>x.checked=!!cb?.checked);
    };

    // Các hàm tên armDemo* được giữ lại như compatibility alias vì bridge cũ đang gọi tên này.
    // Không còn nhánh dữ liệu demo/local; mọi thao tác nghiệp vụ phải đi qua host.
    window.armDemoEditLayer = function(){ window.goiAction?.("Tab1_EditLayer"); };
    window.armDemoDuplicateLayer = function(){ window.goiAction?.("Tab1_DuplicateLayer"); };
    window.armDemoDeleteLayer = function(){ window.goiAction?.("Tab1_DeleteLayer"); };
    window.xoaLayersDaChon = function(){ window.goiAction?.("Tab1_DeleteSelectedLayers"); };

    // Khởi tạo bảng rỗng trước khi host trả dữ liệu thật.
    setTimeout(()=>window.renderLayerManager(),0);



    // ===== TAB 1 · SHARED LAYER SET / SYSTEM-WIDE APPLY =====
    // One action owns three responsibilities:
    // 1) normalize selected template metadata,
    // 2) create/update the corresponding CAD Layers,
    // 3) distribute the same normalized layer set to every consuming tab.

    window.__armSharedLayerSet = [];

    window.armNormalizeSharedLayer = function(r){
        r=r||{};
        const code=String(r.Code ?? r.code ?? r.MarkingCode ?? r.markingCode ?? "");
        const templateId=String(r.TemplateId ?? r.templateId ?? r.Id ?? r.id ?? "");
        const layerName=String(r.Layer ?? r.layer ?? r.layerName ?? r.name ?? "");
        return {
            ...r,
            id: templateId || code || layerName,
            templateId: templateId,
            TemplateId: templateId,
            code: code,
            Code: code,
            markingCode: code,
            MarkingCode: code,
            name: layerName,
            layerName: layerName,
            Layer: layerName,
            description: String(r.Description ?? r.description ?? ""),
            Description: String(r.Description ?? r.description ?? ""),
            pattern: String(r.Pattern ?? r.pattern ?? "CUSTOM_REAL"),
            Pattern: String(r.Pattern ?? r.pattern ?? "CUSTOM_REAL"),
            width: Number(r.Width ?? r.width ?? 0),
            Width: Number(r.Width ?? r.width ?? 0),
            scale: Number(r.Scale ?? r.scale ?? 1),
            Scale: Number(r.Scale ?? r.scale ?? 1),
            rgb: String(r.RGB ?? r.rgb ?? ""),
            RGB: String(r.RGB ?? r.rgb ?? ""),
            quantityMethod: String(r.QuantityMethod ?? r.quantityMethod ?? ""),
            QuantityMethod: String(r.QuantityMethod ?? r.quantityMethod ?? ""),
            managementState: String(r.ManagementState ?? r.managementState ?? ""),
            ManagementState: String(r.ManagementState ?? r.managementState ?? ""),
            layerHandle: String(r.LayerHandle ?? r.layerHandle ?? r.Handle ?? r.handle ?? ""),
            LayerHandle: String(r.LayerHandle ?? r.layerHandle ?? r.Handle ?? r.handle ?? "")
        };
    };

    window.armGetSelectedTab1LayerRows = function(){
        const all=Array.isArray(window.__armTab1LayerRows) ? window.__armTab1LayerRows : [];
        const checked=[...document.querySelectorAll("#layerBody .t1-row-check:checked")];
        const selected=[];
        checked.forEach(cb=>{
            const tr=cb.closest("tr");
            const code=String(tr?.querySelector(".t1-code-main")?.textContent||"").trim();
            const templateId=String(tr?.querySelector(".t1-template-id")?.textContent||"").trim();
            let row=all.find(x=>String(x.TemplateId||x.templateId||"")===templateId && templateId && templateId!=="CHƯA SINH ID");
            if(!row) row=all.find(x=>String(x.Code||x.code||x.MarkingCode||"")===code);
            if(row && !selected.includes(row)) selected.push(row);
        });
        return selected.map(window.armNormalizeSharedLayer);
    };

    window.getTab1SharedLayerDeploymentRequest = function(){
        const layers=window.armGetSelectedTab1LayerRows();
        return {
            action:"APPLY_SHARED_LAYER_SET",
            sourceTab:"TAB1_LAYER",
            layers,
            options:{
                ensureManagementMetadata:true,
                createOrUpdateCadLayers:true,
                distributeToConsumingTabs:true,
                preserveExistingCadLayerObjects:true,
                generateObjectQuantityIds:false
            },
            consumers:[
                "TAB2_CROSS_SECTION",
                "TAB3_MARKING_GENERATOR",
                "TAB4_7_6_9_3",
                "TAB5_SUPPLEMENTARY",
                "TAB6_QUANTITY"
            ]
        };
    };

    window.setTab2LayerTemplates = function(layers){
        const select=byId("KieuVach");
        if(!select) return;
        const current=select.value;
        select.innerHTML='<option value="">Chọn layer vạch từ Tab 1...</option>' +
            layers.map(l=>`<option value="${t1esc(l.templateId||l.id)}">${t1esc(l.code)} · ${t1esc(l.layerName)}</option>`).join("");
        if([...select.options].some(o=>o.value===current)) select.value=current;
    };

    window.setTab3SharedLayerTemplates = function(layers){
        window.__armTab3LayerTemplates=layers.slice();
        const fill=(id, predicate, fallbackText)=>{
            const select=byId(id);
            if(!select) return;
            const current=select.value;
            const candidates=layers.filter(predicate);
            if(!candidates.length) return;
            select.innerHTML=candidates.map(l=>`<option value="${t1esc(l.layerName)}">${t1esc(l.code)} · ${t1esc(l.layerName)}</option>`).join("");
            if([...select.options].some(o=>o.value===current)) select.value=current;
            else if(select.options.length) select.selectedIndex=0;
            select.title=fallbackText||"Layer dùng chung từ Tab 1";
        };
        fill("t3_edgeLayer", l=>/^3\.1/i.test(l.code)||/^3\.2/i.test(l.code)||/^3\.3/i.test(l.code), "Layer vạch mép/chuyển tiếp từ Tab 1");
        fill("t3_stopLineLayer", l=>String(l.code)==="7.1", "Layer vạch dừng 7.1 từ Tab 1");
        fill("t3_pedLineLayer", l=>String(l.code)==="7.3", "Layer vạch đi bộ 7.3 từ Tab 1");
    };

    window.setTab4SharedLayerTemplates = function(layers){
        // Tab 4 currently has no visible Layer selector. Keep the complete library
        // available to its analysis/generation bridge and future 7.6/9.3 templates.
        window.__armTab4LayerTemplates=layers.slice();
    };

    window.setTab6SharedLayerTemplates = function(layers){
        // Tab 6 receives template metadata (Code, TemplateId, QuantityMethod, Layer)
        // but does not create quantity records until CAD objects exist.
        window.__armTab6LayerTemplates=layers.slice();
        window.__armTab6LayerTemplateMap=Object.fromEntries(
            layers.map(l=>[String(l.templateId||l.code||l.layerName),l])
        );
    };

    window.armBroadcastSharedLayerSet = function(layers, meta){
        const normalized=(Array.isArray(layers)?layers:[]).map(window.armNormalizeSharedLayer);
        window.__armSharedLayerSet=normalized;

        window.setTab2LayerTemplates?.(normalized);
        window.setTab3SharedLayerTemplates?.(normalized);
        window.setTab4SharedLayerTemplates?.(normalized);
        window.setTab5LayerTemplates?.(normalized);
        window.setTab6SharedLayerTemplates?.(normalized);

        window.dispatchEvent(new CustomEvent("arm:shared-layer-set-updated",{
            detail:{
                layers:normalized,
                meta:{source:"TAB1_LAYER",...(meta||{})}
            }
        }));
        return normalized;
    };

    window.setTab1SharedLayerStatus = function(data){
        data=data||{};
        const badge=byId("t1SharedLayerStatus");
        const btn=byId("btnSyncLayer");
        const state=String(data.state||data.status||"ready").toLowerCase();
        if(btn) btn.disabled=state==="running";
        if(!badge) return;

        if(state==="running"){
            badge.textContent="ĐANG ĐỒNG BỘ";
            badge.className="status-pill status-running";
        }else if(state==="error"){
            badge.textContent="ĐỒNG BỘ LỖI";
            badge.className="status-pill status-error";
        }else if(state==="dirty"){
            badge.textContent="CẦN ĐỒNG BỘ";
            badge.className="status-pill status-running";
        }else{
            const n=Number(data.applied ?? data.total ?? 0);
            badge.textContent=n ? `ĐÃ ĐỒNG BỘ ${n}` : "ĐÃ ĐỒNG BỘ";
            badge.className="status-pill status-success";
        }
        if(data.message) badge.title=String(data.message);
    };

    window.onTab1SharedLayerSetApplied = function(result){
        result=result||{};
        const success=result.success!==false && String(result.state||"").toLowerCase()!=="error";
        if(!success){
            window.setTab1SharedLayerStatus({state:"error",message:result.message||"Host báo lỗi khi áp dụng bộ layer."});
            window.showArmToast?.(result.message||"Áp dụng và đồng bộ Layer không thành công.","error");
            return;
        }

        const returned=Array.isArray(result.layers) ? result.layers : window.armGetSelectedTab1LayerRows();
        const normalized=window.armBroadcastSharedLayerSet(returned,{
            cad:result.cad||null,
            appliedAt:result.appliedAt||new Date().toISOString()
        });

        // Merge returned LayerHandle / normalized metadata back into Tab 1 rows.
        const all=window.__armTab1LayerRows||[];
        normalized.forEach(n=>{
            const target=all.find(x=>
                (n.templateId && String(x.TemplateId||x.templateId||"")===String(n.templateId)) ||
                (n.code && String(x.Code||x.code||"")===String(n.code))
            );
            if(target){
                if(n.templateId) target.TemplateId=n.templateId;
                target.MarkingCode=n.code;
                target.ManagementState="READY";
                if(n.quantityMethod) target.QuantityMethod=n.quantityMethod;
                if(n.layerHandle) target.LayerHandle=n.layerHandle;
            }
        });
        window.renderLayerManager?.();

        const cad=result.cad||{};
        const created=Number(cad.created||0);
        const updated=Number(cad.updated||0);
        const failed=Number(cad.failed||0);
        window.setTab1SharedLayerStatus({
            state:failed>0?"error":"ready",
            applied:normalized.length,
            message:`CAD tạo ${created}, cập nhật ${updated}, lỗi ${failed}. Các tab dùng dữ liệu đã nhận bộ layer mới.`
        });
        window.showArmToast?.(
            failed>0
                ? `Đã phân phối ${normalized.length} layer; CAD còn ${failed} lỗi.`
                : `Đã áp dụng ${normalized.length} layer cho CAD và các tab dùng chung.`,
            failed>0 ? "warning" : "success"
        );
    };

    window.apDungBoLayerDaChon = function(){
        const request=window.getTab1SharedLayerDeploymentRequest();
        const layers=request.layers||[];
        if(!layers.length){
            window.showArmToast?.("Hãy chọn ít nhất một Layer để áp dụng.","warning");
            return;
        }

        window.setTab1SharedLayerStatus({state:"running",message:"Đang áp dụng Layer vào CAD và đồng bộ dữ liệu dùng chung."});

        // Host C#/WebView2 sở hữu thao tác tạo/cập nhật Layer CAD:
        // Host receives "Tab1_ApplySharedLayerSet", then reads
        // getTab1SharedLayerDeploymentRequest() and must create/update CAD layers.
        try{
            window.goiAction?.("Tab1_ApplySharedLayerSet");
        }catch(err){
            window.setTab1SharedLayerStatus({state:"error",message:String(err?.message||err)});
            window.showArmToast?.("Không gọi được chức năng ÁP DỤNG VÀ ĐỒNG BỘ từ host.","error");
        }
    };

    // Compatibility alias for old bridge calls.
    window.dongBoLayerDaChon = window.apDungBoLayerDaChon;

    // Any template/library edit makes the applied shared set stale.
    const markSharedDirty=()=>window.setTab1SharedLayerStatus({
        state:"dirty",
        message:"Thư viện Layer đã thay đổi; cần ÁP DỤNG VÀ ĐỒNG BỘ lại để CAD và các Tab nhận cùng dữ liệu."
    });
    byId("csvFileInput")?.addEventListener("change",markSharedDirty);
    byId("btnLoadBundledCsv")?.addEventListener("click",markSharedDirty);
    byId("btnSubmitLayer")?.addEventListener("click",markSharedDirty);
    byId("btnDeleteSelectedLayers")?.addEventListener("click",markSharedDirty);
    byId("layerBody")?.addEventListener("click",e=>{
        if(e.target.closest(".arm-layer-action")) setTimeout(markSharedDirty,0);
    });


    // ===== TAB 1 · MANAGEMENT SET CONTRACT =====
    // CSV/template import is intentionally lean. Management fields are generated
    // only when the user explicitly presses "CẬP NHẬT THƯ VIỆN".
    window.getTab1LayerDataContract = function(){
        return {
            mode: "TEMPLATE_TECHNICAL_ONLY",
            importFields: [
                "Code","Description","Layer","Pattern","Width","Scale",
                "Color","RGB","Dash","Gap",
                "CustomDash1","CustomGap1","CustomDash2","CustomGap2","CustomPhase",
                "Reference","StandardClause","CustomProperties"
            ],
            generatedOnSetUpdate: [
                "TemplateId","MarkingCode","ManagementState","QuantityMethod","ManagementSchema","ManagementVersion","ManagementUpdatedAt"
            ],
            generatedOnCadObject: [
                "RecordId","EntityId","ObjectHandle",
                "OwnerId","OwnerType","RoadKey","AxisKey",
                "GenerationMode","Quantity","Length","Area","Count"
            ],
            rules: {
                templateId: "STABLE_PER_TEMPLATE",
                markingCode: "FROM_CODE_OR_LAYER_SUFFIX",
                objectIds: "NEVER_GENERATE_AT_TEMPLATE_IMPORT",
                quantities: "MEASURE_FROM_CAD_GEOMETRY"
            }
        };
    };

    window.markTab1ManagementSetDirty = function(reason){
        const badge=byId("t1ManagementSetStatus");
        if(badge){
            badge.textContent="CẦN CẬP NHẬT";
            badge.className="status-pill status-running";
            badge.title=reason || "Thư viện layer đã thay đổi; cần cập nhật lại metadata quản lý.";
        }
    };

    window.setTab1ManagementSetState = function(data){
        data=data||{};
        const badge=byId("t1ManagementSetStatus");
        const btn=byId("btnUpdateLayerSet");
        const state=String(data.state||data.status||"ready").toLowerCase();
        const total=Number(data.total ?? data.totalTemplates ?? 0);
        const updated=Number(data.updated ?? data.updatedTemplates ?? total);
        const failed=Number(data.failed ?? 0);
        if(btn) btn.disabled=state==="running";

        if(!badge) return;
        if(state==="running"){
            badge.textContent=total ? `ĐANG CẬP NHẬT ${updated}/${total}` : "ĐANG CẬP NHẬT";
            badge.className="status-pill status-running";
        }else if(state==="error" || failed>0){
            badge.textContent=failed ? `LỖI ${failed}` : "CẬP NHẬT LỖI";
            badge.className="status-pill status-error";
        }else if(state==="dirty" || state==="stale"){
            badge.textContent="CẦN CẬP NHẬT";
            badge.className="status-pill status-running";
        }else{
            badge.textContent=total ? `ĐÃ CẬP NHẬT ${updated}/${total}` : "ĐÃ CẬP NHẬT";
            badge.className="status-pill status-success";
        }
        if(data.message) badge.title=String(data.message);
    };

    window.updateTab1ManagementSet = function(){
        const btn=byId("btnUpdateLayerSet");
        const badge=byId("t1ManagementSetStatus");
        if(btn) btn.disabled=true;
        if(badge){
            badge.textContent="ĐANG CẬP NHẬT";
            badge.className="status-pill status-running";
            badge.title="Đang chuẩn hóa metadata quản lý cho toàn bộ thư viện layer.";
        }

        // Host C#/WebView2 owns the persistent library and therefore owns the
        // actual generation/normalization. It may query getTab1LayerDataContract().
        if(typeof window.goiAction==="function"){
            try{
                window.goiAction("Tab1_UpdateLayerManagementSet");
                return;
            }catch(err){
                if(btn) btn.disabled=false;
                if(badge){
                    badge.textContent="CẬP NHẬT LỖI";
                    badge.className="status-pill status-error";
                    badge.title=String(err?.message||err||"Không gọi được host.");
                }
                window.showArmToast?.("Không thể yêu cầu CẬP NHẬT THƯ VIỆN từ host.","error");
                return;
            }
        }

        if(btn) btn.disabled=false;
        if(badge){
            badge.textContent="CHƯA KẾT NỐI";
            badge.className="status-pill status-error";
        }
        window.showArmToast?.("Host C#/WebView2 chưa kết nối chức năng CẬP NHẬT THƯ VIỆN.","warning");
    };

    // Any library-changing action makes management metadata stale.
    byId("csvFileInput")?.addEventListener("change",()=>window.markTab1ManagementSetDirty("Vừa nạp template/CSV."));
    byId("btnSubmitLayer")?.addEventListener("click",()=>window.markTab1ManagementSetDirty("Vừa thêm hoặc sửa template layer."));
    byId("btnDeleteSelectedLayers")?.addEventListener("click",()=>window.markTab1ManagementSetDirty("Vừa xóa template layer."));
    byId("layerBody")?.addEventListener("click",e=>{
        if(e.target.closest(".arm-layer-action")){
            setTimeout(()=>window.markTab1ManagementSetDirty("Thư viện layer vừa được chỉnh sửa."),0);
        }
    });


    for(const name of ["analyzeSymbolPlacement", "assignCadAxisName", "changeSymbolApproach", "changeSymbolNode", "changeTab4AssemblyProfile", "chonMepCauKienTuCAD", "chonMepTuCAD", "chonTimTuCAD", "clearCadAxisSelection", "doiXungQuaTim", "dongBoAssemblyDaChon", "dongBoLayerDaChon", "dongBoThuVienTab3", "exportQuantityExcel", "filterCadAxisIdentities", "filterComparisonResults", "filterQuantityOwners", "filterQuantityTable", "generateSymbolBlocks", "goiAction", "huyCheDoSua", "lamMoiLayerTab2", "luuMatCatVaoDanhSach", "nhapCSV", "nhapJSON", "onLoaiChange", "openTab", "previewCadAxisName", "refreshCadAxisIdentities", "refreshQuantityFromCad", "refreshSymbolBlockLibrary", "refreshSymbolPlacementWorkspace", "reloadTab4AssemblyProfiles", "renderDanhSachDaLuu", "renderLayerManager", "renderSymbolProposals", "resetFormTab1", "resetSymbolPlacementRules", "resetTab4AssemblyProfile", "saveSymbolPlacementDefaults", "saveTab4AssemblyProfile", "scanSymbolBlockLibrary", "selectAllSymbolProposals", "selectCadAxisForNaming", "selectSymbolLibraryFolder", "setQuantityOwnerType", "setTab3CompactStep", "syncColorToRGB", "syncRGBToHex", "themLayerThuCong", "themThanhPhan", "toggleCustomLineTypeFields", "toggleSelectAllAssemblies", "toggleSelectAllLayers", "updateCustomRealCycle", "updateSymbolRules", "vePreviewLayerTab1", "xoaAssemblyDaChon", "xoaLayersDaChon", "xuatCSV", "xuatJSONDaChon"]){
        if(name === "openTab" || name === "setTab3CompactStep") continue;
        if(typeof window[name] !== "function"){
            window[name] = name === "goiAction"
                ? function(action){ window.showArmToast(`Host chưa kết nối action: ${action || "không xác định"}`,"warning"); return false; }
                : function(){ window.showArmToast(`Host chưa kết nối chức năng: ${name}`,"warning"); return false; };
        }
    }


})(window.ARM);