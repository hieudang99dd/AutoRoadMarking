(function(ARM){
    "use strict";
    const byId = ARM.byId;
    const dashboard = document.querySelector("#tabVach2D .t3-dashboard");
    if(dashboard && !dashboard.dataset.compactStep){
        dashboard.dataset.compactStep = "1";
    }

    // TAB 5 init: áp dụng trạng thái hiển thị thật sau khi toàn bộ hàm Tab 5 đã được khai báo.
    window.setTab5ObjectType("speed_hump");
    window.setTab5SpeedMode("uniform");
    window.setTab5Scope("segment");


    // ===== UI 6.1 ergonomic helpers =====
    window.toggleArmDensity = function(force){
        const compact = typeof force === "boolean" ? force : !document.body.classList.contains("density-compact");
        document.body.classList.toggle("density-compact",compact);
        localStorage.setItem("arm_density",compact?"compact":"comfortable");
        const btn=byId("armDensityBtn"); if(btn) btn.textContent=compact?"GỌN":"THOẢI MÁI";
    };
    if(localStorage.getItem("arm_density")==="compact") window.toggleArmDensity(true);

    window.setCadInteractionState = function(state){
        state=state||{}; const box=byId("armCadInteraction"); if(!box) return;
        box.hidden=!state.active;
        if(byId("armCadInteractionTitle")) byId("armCadInteractionTitle").textContent=state.title||"ĐANG CHỜ THAO TÁC TRÊN CAD";
        if(byId("armCadInteractionDetail")) byId("armCadInteractionDetail").textContent=state.detail||"Chọn đối tượng trên mặt bằng · ESC để hủy";
    };
    window.cancelArmCadInteraction = function(){ window.setCadInteractionState({active:false}); if(typeof window.goiAction==="function") window.goiAction("Cancel_Cad_Interaction"); };
    window.acknowledgeArmCadPick = function(fieldId,text){ const el=byId(fieldId); if(el && text!==undefined) el.textContent=String(text); if(el){el.classList.remove("arm-picked-flash");void el.offsetWidth;el.classList.add("arm-picked-flash");el.scrollIntoView({block:"nearest",behavior:"smooth"});} window.setCadInteractionState({active:false}); if(/^s5/.test(fieldId)) window.invalidateTab5Preview("Hình học vừa thay đổi"); };
    window.setArmProgress = function(data){ data=data||{}; const box=byId("armProgress"); if(!box) return; box.hidden=!data.active; const v=Math.max(0,Math.min(100,Number(data.value)||0)); if(byId("armProgressTitle")) byId("armProgressTitle").textContent=data.title||"ĐANG XỬ LÝ"; if(byId("armProgressDetail")) byId("armProgressDetail").textContent=data.detail||"Vui lòng chờ..."; if(byId("armProgressBar")) byId("armProgressBar").style.width=`${v}%`; if(byId("armProgressValue")) byId("armProgressValue").textContent=`${Math.round(v)}%`; };

    window.toggleTab2Preview = function(){ const panel=byId("t2PreviewPanel"), btn=byId("t2PreviewToggle"); if(!panel) return; const collapsed=panel.classList.toggle("is-collapsed"); if(btn) btn.textContent=collapsed?"MỞ PREVIEW":"THU GỌN"; localStorage.setItem("arm_t2_preview",collapsed?"collapsed":"open"); };
    localStorage.removeItem("arm_t2_preview"); byId("t2PreviewPanel")?.classList.remove("is-collapsed");

    const t1ReservedPropertyKeys = new Set([
        "code","markingcode","name","description","category","geometry","layer","layername","width",
        "pattern","linetype","linetypename","linetypescale","scale","dashlength","gaplength",
        "customdash1","customgap1","customdash2","customgap2","customphase","customcycle","cycle",
        "paintratio","color","colorhex","rgb","reference","standardref","standardreference","standardsource",
        "standardclause","verificationstatus","verified","id","templateid","entityid","groupid","handle",
        "roadidentity","roadname","roadnamesnapshot","roadkey","axiskey","axishandle","axistype",
        "nodeid","nodekey","approachid","approachkey","ownerid","ownertype","generationmode",
        "quantity","length","area","count"
    ]);
    const t1CanonicalPropertyKey = key => String(key||"").trim().toLowerCase();
    window.isTab1ReservedPropertyKey = key => t1ReservedPropertyKeys.has(t1CanonicalPropertyKey(key));

    function validateTab1PropertyRows(showToast=false){
        const rows=[...document.querySelectorAll("#t1PropertyRows .arm-property-row")];
        const seen=new Set(); let valid=true;
        rows.forEach(row=>{
            row.classList.remove("is-invalid"); row.querySelector(".arm-prop-error")?.remove();
            const input=row.querySelector(".arm-prop-key"); const key=input?.value.trim()||""; const canon=t1CanonicalPropertyKey(key);
            let message="";
            if(!key) message="Nhập tên thuộc tính hoặc xóa dòng này.";
            else if(window.isTab1ReservedPropertyKey(key)) message=`${key} là thuộc tính hệ thống; hãy chỉnh tại trường kỹ thuật tương ứng.`;
            else if(seen.has(canon)) message=`${key} bị trùng trong metadata mở rộng.`;
            if(canon) seen.add(canon);
            if(message){ valid=false; row.classList.add("is-invalid"); const error=document.createElement("div"); error.className="arm-prop-error"; error.textContent=message; row.appendChild(error); }
        });
        if(!valid && showToast) window.showArmToast?.("Metadata mở rộng có key không hợp lệ hoặc trùng. Hãy sửa trước khi lưu.","warning");
        return valid;
    }
    window.validateTab1CustomProperties=validateTab1PropertyRows;

    window.addTab1CustomProperty = function(key="",value=""){
        const host=byId("t1PropertyRows"); if(!host) return;
        const row=document.createElement("div"); row.className="arm-property-row";
        row.innerHTML=`<input class="arm-prop-key" placeholder="Ví dụ: PaintType" value="${String(key).replace(/"/g,'&quot;')}"><input class="arm-prop-value" placeholder="Giá trị" value="${String(value).replace(/"/g,'&quot;')}"><button class="btn-icon" type="button" title="Xóa thuộc tính">×</button>`;
        row.querySelector("button").onclick=()=>{row.remove();syncTab1PropertyJson();validateTab1PropertyRows(false);};
        row.querySelectorAll("input").forEach(x=>x.addEventListener("input",()=>{syncTab1PropertyJson();validateTab1PropertyRows(false);}));
        host.appendChild(row); syncTab1PropertyJson(); validateTab1PropertyRows(false);
    };
    function syncTab1PropertyJson(){
        const obj={}; document.querySelectorAll("#t1PropertyRows .arm-property-row").forEach(r=>{
            const k=r.querySelector(".arm-prop-key")?.value.trim(),v=r.querySelector(".arm-prop-value")?.value??"";
            if(k)obj[k]=v;
        });
        if(byId("t1_CustomProperties")) byId("t1_CustomProperties").value=JSON.stringify(obj);
    }
    function hydrateTab1PropertyEditor(){ const host=byId("t1PropertyRows"); if(!host||host.children.length) return; let obj={}; try{obj=JSON.parse(byId("t1_CustomProperties")?.value||"{}");}catch{} Object.entries(obj).forEach(([k,v])=>window.addTab1CustomProperty(k,v)); }
    hydrateTab1PropertyEditor();

    function syncTab1PatternSource(){
        const type=byId("t1_KieuNet")?.value;
        const isCustom=type==="CUSTOM_REAL";
        const isStandardPattern=type==="DASHED"||type==="DASHEDX2"||type==="HIDDEN";
        const custom=byId("t1CustomRealFields"), standard=byId("t1StandardPatternFields");
        if(custom) custom.hidden=!isCustom;
        if(standard) standard.hidden=!isStandardPattern;
        const dash=byId("t1_DashLength"), gap=byId("t1_GapLength");
        if(dash) dash.disabled=!isStandardPattern; if(gap) gap.disabled=!isStandardPattern;
    }
    byId("t1_KieuNet")?.addEventListener("change",syncTab1PatternSource); syncTab1PatternSource();

    // Mark Tab 5 preview stale whenever an engineering parameter changes.
    document.querySelectorAll("#s5SpeedPanel input,#s5SpeedPanel select").forEach(el=>el.addEventListener("change",()=>window.invalidateTab5Preview("Tham số đã thay đổi")));
    ["s5Boundary1","s5Boundary2","s5StartStation","s5EndStation","s5ClusterAnchor","s5ClusterDirectionPoint"].forEach(id=>{const el=byId(id); if(el) new MutationObserver(()=>window.invalidateTab5Preview("Hình học đã thay đổi")).observe(el,{childList:true,characterData:true,subtree:true});});

    // Tab 4 dirty tracking.
    document.querySelector("#tabKyHieuBlock .b4-lane-workspace")?.addEventListener("change",e=>{if(e.target.matches("select,input")) ARM.tab4?.setDirty?.(true);});
    byId("b4SaveProfileBtn")?.addEventListener("click",()=>setTimeout(()=>ARM.tab4?.setDirty?.(false),0));

    // Quantity snapshot freshness API: host marks dirty whenever CAD geometry changes.
    window.setQuantitySnapshotState = function(data){ data=data||{}; const dirty=!!data.dirty; const badge=byId("q6DataFreshness"); if(badge){badge.textContent=dirty?"CAD ĐÃ THAY ĐỔI":"DỮ LIỆU MỚI";badge.className=`status-pill ${dirty?"status-running":"status-success"}`;} if(byId("q6DataTimestamp") && data.timestamp) byId("q6DataTimestamp").textContent=String(data.timestamp); ["q6ExportFiltered","q6ExportAll"].forEach(id=>{const b=byId(id); if(b) b.dataset.quantityDirty=dirty?"true":"false";}); };
    ["q6ExportFiltered","q6ExportAll"].forEach(id=>byId(id)?.addEventListener("click",e=>{if(e.currentTarget.dataset.quantityDirty==="true"&&!window.confirm("CAD đã thay đổi sau lần đọc khối lượng. Vẫn xuất dữ liệu snapshot cũ?")){e.preventDefault();e.stopImmediatePropagation();}} ,true));

    // Generic sortable tables. Sorting is local to visible rows, preserving engineering filters.
    document.querySelectorAll("table").forEach(table=>{ table.querySelectorAll("thead th").forEach((th,index)=>{ const label=(th.textContent||"").trim(); if(th.hasAttribute("data-no-sort")||th.querySelector("input,button,select")||!label) return; th.classList.add("arm-sortable"); th.addEventListener("click",()=>{const tbody=table.tBodies[0];if(!tbody||tbody.rows.length<2)return; const dir=th.dataset.sortDir==="asc"?"desc":"asc"; table.querySelectorAll("th").forEach(x=>delete x.dataset.sortDir); th.dataset.sortDir=dir; const rows=[...tbody.rows].filter(r=>!r.classList.contains("s5-empty-row")&&!r.classList.contains("q6-empty-row")&&!r.classList.contains("compare-empty")&&!r.classList.contains("t0-empty-row")); rows.sort((a,b)=>{const A=(a.cells[index]?.innerText||"").trim(),B=(b.cells[index]?.innerText||"").trim(); const na=Number(A.replace(/[^0-9,.-]/g,"").replace(",",".")),nb=Number(B.replace(/[^0-9,.-]/g,"").replace(",",".")); const cmp=Number.isFinite(na)&&Number.isFinite(nb)&&A.match(/\d/)&&B.match(/\d/)?na-nb:A.localeCompare(B,"vi",{numeric:true,sensitivity:"base"}); return dir==="asc"?cmp:-cmp;}); rows.forEach(r=>tbody.appendChild(r)); }); }); });

    // Bind labels to controls automatically where the DOM structure makes the target unambiguous.
    document.querySelectorAll("label:not([for])").forEach(label=>{ let control=label.parentElement?.querySelector(":scope > input[id],:scope > select[id],:scope > textarea[id]"); if(!control && label.nextElementSibling) control=label.nextElementSibling.matches?.("input[id],select[id],textarea[id]")?label.nextElementSibling:label.nextElementSibling.querySelector?.("input[id],select[id],textarea[id]"); if(control?.id) label.htmlFor=control.id; });

    // Keyboard acceleration for long sessions. Alt+0…6 switches module; Ctrl+F focuses search in current tab.
    document.addEventListener("keydown",e=>{ if(e.altKey && /^[0-6]$/.test(e.key)){ const btn=document.querySelectorAll(".tab-btn[data-tab]")[Number(e.key)]; if(btn){e.preventDefault();btn.click();} } if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==="f"){ const active=document.querySelector(".tab-content.active"); const search=active?.querySelector('input[placeholder*="Tìm"],input[type="search"]'); if(search){e.preventDefault();search.focus();search.select?.();} } if(e.key==="Escape"&&!byId("armCadInteraction")?.hidden) window.cancelArmCadInteraction(); });

    // Splitters for dense CAD workspaces, persisted per workstation.
    function installSplitter(container, storageKey, minLeft, maxLeft, defaultLeft, minRight=360){
        if(!container||container.querySelector(":scope > .arm-splitter"))return;
        const children=[...container.children]; if(children.length<2)return;
        const splitter=document.createElement("div"); splitter.className="arm-splitter"; children[0].after(splitter);
        const effectiveMax=()=>{
            const width=container.getBoundingClientRect().width||container.clientWidth||0;
            return Math.max(minLeft,Math.min(maxLeft,width>0?width-minRight:maxLeft));
        };
        const applyWidth=value=>{
            const resolved=Math.max(minLeft,Math.min(effectiveMax(),Number(value)||defaultLeft));
            container.style.setProperty("--arm-split-left",`${resolved}px`);
            localStorage.setItem(storageKey,String(Math.round(resolved)));
            return resolved;
        };
        const saved=Number(localStorage.getItem(storageKey));
        applyWidth(Number.isFinite(saved)&&saved>0?saved:defaultLeft);
        const down=e=>{
            e.preventDefault(); splitter.classList.add("is-dragging");
            const rect=container.getBoundingClientRect();
            const move=ev=>{applyWidth(ev.clientX-rect.left);};
            const up=()=>{splitter.classList.remove("is-dragging");window.removeEventListener("pointermove",move);window.removeEventListener("pointerup",up);};
            window.addEventListener("pointermove",move); window.addEventListener("pointerup",up);
        };
        splitter.addEventListener("pointerdown",down);
        window.addEventListener("resize",()=>applyWidth(Number(localStorage.getItem(storageKey))||defaultLeft));
    }
    document.querySelectorAll(".tab-body").forEach((x,i)=>{
        x.classList.add("arm-split-grid");
        const tabId=x.closest(".tab-content")?.id || x.id || `workspace_${i}`;
        const isTab2=tabId==="tabThietKe";
        const keyMap={tabLayer:"arm_split_tab1",tabThietKe:"arm_split_tab2"};
        const storageKey=keyMap[tabId] || `arm_split_${tabId}`;
        const legacyKey=`arm_split_tabbody_${i}`;
        if(localStorage.getItem(storageKey)===null && localStorage.getItem(legacyKey)!==null){
            localStorage.setItem(storageKey,localStorage.getItem(legacyKey));
        }
        installSplitter(x,storageKey,isTab2?360:330,isTab2?900:520,isTab2?460:390,isTab2?420:400);
    });
    const s5work=document.querySelector("#tabPhatSinh .s5-workspace"); if(s5work) installSplitter(s5work,"arm_split_tab5",420,720,470);

    // UI APIs for host-driven workflow gating without guessing engineering prerequisites in HTML.
    window.setTab3WorkflowGate = function(state){ state=state||{}; const map={step1:"btnAnalyzePair",step2:"btnStep2DrawPolygon",step3:"btnStep3",step4:"btnStep4"}; Object.entries(map).forEach(([k,id])=>{const b=byId(id); if(b&&state[k]!==undefined)b.disabled=!state[k];}); };

    // Start with current validation and no stale quantity export claim.
    window.updateTab5ActionState();

    // Standalone preview only. Khi C# truyền danh sách MCN thật qua setTab4AssembliesFromTab2(), dữ liệu minh họa sẽ được thay thế hoàn toàn.
    window.setTab4AssemblyProfiles(ARM.tab4?.previewProfiles || []);
})(window.ARM);
