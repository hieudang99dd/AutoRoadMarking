(function(ARM){
    "use strict";
    const byId = ARM.byId;
    const b4Marking93Options = [
        ["none","KHÔNG BỐ TRÍ 9.3"],
        ["straight","9.3 · ĐI THẲNG"],
        ["left","9.3 · RẼ TRÁI"],
        ["right","9.3 · RẼ PHẢI"],
        ["straight_left","9.3 · THẲNG VÀ TRÁI"],
        ["straight_right","9.3 · THẲNG VÀ PHẢI"],
        ["left_right","9.3 · TRÁI VÀ PHẢI"]
    ];

    const b4TextKey = value => String(value ?? "")
        .normalize("NFD").replace(/[\u0300-\u036f]/g, "")
        .toLowerCase().replace(/đ/g,"d").trim();
    const b4Number = value => {
        const n = Number(String(value ?? "").replace(",","."));
        return Number.isFinite(n) ? n : 0;
    };
    const b4Pick = (obj, keys, fallback = undefined) => {
        if(!obj) return fallback;
        for(const key of keys){
            if(obj[key] !== undefined && obj[key] !== null && obj[key] !== "") return obj[key];
        }
        return fallback;
    };
    const b4AssemblyId = a => String(b4Pick(a,["id","Id","ID","assemblyId","AssemblyId","key","Key","TenMatCat","name","Name"],""));
    const b4AssemblyName = a => String(b4Pick(a,["name","Name","TenMatCat","tenMatCat","assemblyName","AssemblyName","id","Id"],"MCN"));

    const b4GetAssemblyComponents = assembly => {
        const direct = b4Pick(assembly,["components","Components","thanhPhan","ThanhPhan","thanhPhans","ThanhPhans","items","Items","cauKien","CauKien"],null);
        if(Array.isArray(direct)) return direct.slice();
        const result=[];
        const left=b4Pick(assembly,["leftComponents","LeftComponents","left","Left"],null);
        const right=b4Pick(assembly,["rightComponents","RightComponents","right","Right"],null);
        const center=b4Pick(assembly,["centerComponents","CenterComponents","center","Center"],null);
        if(Array.isArray(left)) left.forEach(x=>result.push({...x,__b4Side:"Left"}));
        if(Array.isArray(right)) right.forEach(x=>result.push({...x,__b4Side:"Right"}));
        if(Array.isArray(center)) center.forEach(x=>result.push({...x,__b4Side:"Center"}));
        return result;
    };

    const b4ComponentSide = component => {
        const raw=b4TextKey(b4Pick(component,["__b4Side","side","Side","BenBoTri","benBoTri","Phia","phia","position","Position"],""));
        if(raw.includes("left") || raw.includes("trai")) return "Left";
        if(raw.includes("right") || raw.includes("phai")) return "Right";
        if(raw.includes("center") || raw.includes("tim") || raw.includes("centre")) return "Center";
        const off=b4Pick(component,["centerOffset","CenterOffset","offset","Offset","OffsetTim","offsetTim","TamLaneOffset","tamLaneOffset"],null);
        if(off !== null){ const n=b4Number(off); if(n<0) return "Left"; if(n>0) return "Right"; }
        return "Center";
    };
    const b4ComponentType = component => String(b4Pick(component,["type","Type","LoaiThanhPhan","loaiThanhPhan","componentType","ComponentType","Loai","loai","name","Name"],""));
    const b4ComponentWidth = component => b4Number(b4Pick(component,["width","Width","BeRong","beRong","BeRongThanhPhan","beRongThanhPhan"],0));
    const b4ComponentOccupies = component => {
        const raw=b4Pick(component,["occupiesWidth","OccupiesWidth","IsChiemDienTich","isChiemDienTich","chiemBeRong","ChiemBeRong"],undefined);
        if(raw !== undefined) return !(raw===false || raw===0 || String(raw).toLowerCase()==="false");
        const key=b4TextKey(b4ComponentType(component));
        return !(key.includes("vach son") || key.includes("vachson") || key.includes("marking"));
    };
    const b4IsLane = component => {
        const key=b4TextKey(b4ComponentType(component));
        return key === "lanxe" || key.includes("lan xe") || key === "lane" || key.includes("traffic lane");
    };
    const b4IsMedian = component => {
        const key=b4TextKey(b4ComponentType(component));
        return key.includes("dai phan cach") || key.includes("daiphancach") || key.includes("median");
    };
    const b4ExplicitOffset = component => {
        const raw=b4Pick(component,["centerOffset","CenterOffset","laneCenterOffset","LaneCenterOffset","OffsetTim","offsetTim","TamLaneOffset","tamLaneOffset","offset","Offset"],null);
        if(raw === null || raw === undefined || raw === "") return null;
        const n=b4Number(raw); return Number.isFinite(n) ? n : null;
    };

    const b4BuildSideLanes = (components, side, centerHalfWidth) => {
        const sideComponents=components.filter(c=>b4ComponentSide(c)===side).sort((a,b)=>b4Number(b4Pick(a,["order","Order"],0))-b4Number(b4Pick(b,["order","Order"],0)));
        const sign=side==="Left"?-1:1;
        let physical=Math.max(0,centerHalfWidth), local=0, origin=physical, groupIndex=1, lanesInGroup=0;
        const lanes=[];
        sideComponents.forEach((component,sourceIndex)=>{
            const width=Math.max(0,b4ComponentWidth(component));
            const occupies=b4ComponentOccupies(component);
            if(b4IsMedian(component)){
                if(occupies) physical+=width;
                if(lanesInGroup>0) groupIndex++;
                origin=physical; local=0; lanesInGroup=0;
                return;
            }
            if(b4IsLane(component)&&width>0){
                const laneIndex=lanesInGroup+1;
                const hostLocal=b4Pick(component,["localCenterOffset","LocalCenterOffset"],null);
                const hostOrigin=b4Pick(component,["carriagewayOriginOffset","CarriagewayOriginOffset"],null);
                const hostPhysical=b4Pick(component,["physicalCenterOffset","PhysicalCenterOffset"],null);
                const localCenter=hostLocal!==null&&hostLocal!==undefined&&hostLocal!==""?b4Number(hostLocal):sign*(local+width/2);
                const originOffset=hostOrigin!==null&&hostOrigin!==undefined&&hostOrigin!==""?b4Number(hostOrigin):sign*origin;
                const physicalCenter=hostPhysical!==null&&hostPhysical!==undefined&&hostPhysical!==""?b4Number(hostPhysical):sign*(physical+width/2);
                const sourceName=String(b4Pick(component,["laneName","LaneName","Ten","ten","name","Name"],"")).trim();
                lanes.push({
                    id:groupIndex===1?`${side==="Left"?'L':'R'}${laneIndex}`:`${side==="Left"?'L':'R'}G${groupIndex}_${laneIndex}`,
                    sourceIndex,sourceSide:side,width,laneIndex,
                    carriagewayGroupId:`${side==="Left"?'L':'R'}${groupIndex===1?'_MAIN':`_GROUP_${groupIndex}`}`,
                    isMainCarriageway:groupIndex===1,
                    localCenterOffset:localCenter,
                    carriagewayOriginOffset:originOffset,
                    physicalCenterOffset:physicalCenter,
                    centerOffset:physicalCenter,
                    offset:`Local ${Math.abs(localCenter).toFixed(2)} m · CAD ${Math.abs(physicalCenter).toFixed(2)} m`,
                    raw:component,sourceName
                });
                physical+=width; local+=width; lanesInGroup++;
                return;
            }
            if(!occupies||width<=0)return;
            physical+=width;
            if(lanesInGroup===0){origin=physical;local=0;}else local+=width;
        });
        return lanes.filter(x=>x.isMainCarriageway);
    };

    const b4MedianDescription = components => {
        const medians=components.filter(b4IsMedian);
        if(!medians.length) return "Không có";
        const total=medians.reduce((sum,c)=>sum+b4ComponentWidth(c),0);
        return total>0 ? `Dải phân cách · ${total.toFixed(2)} m` : "Dải phân cách";
    };

    const b4MergeLaneRule = (lane, saved, index) => {
        if(!Array.isArray(saved)) return lane;
        const match=saved.find(x=>String(x.id||x.laneId||"")===String(lane.id)) || saved[index];
        if(!match) return lane;
        return {...lane, marking76:match.marking76 ?? match.enable76 ?? lane.marking76, marking93:match.marking93 ?? match.type93 ?? lane.marking93};
    };

    const b4NormalizeAssemblyProfile = assembly => {
        const components=b4GetAssemblyComponents(assembly);
        const centerMedianWidth=components
            .filter(c=>b4ComponentSide(c)==="Center" && b4IsMedian(c) && b4ComponentOccupies(c))
            .reduce((sum,c)=>sum+b4ComponentWidth(c),0);
        const centerHalfWidth=centerMedianWidth/2;
        let leftLanes=b4BuildSideLanes(components,"Left",centerHalfWidth);
        let rightLanes=b4BuildSideLanes(components,"Right",centerHalfWidth);

        // Nếu host đã gửi lane group chuẩn hóa thì vẫn ưu tiên dữ liệu đó.
        const hostInbound=Array.isArray(assembly.inbound) ? assembly.inbound : null;
        const hostOutbound=Array.isArray(assembly.outbound) ? assembly.outbound : null;
        let inbound=hostInbound || leftLanes;
        let outbound=hostOutbound || rightLanes;

        // MCN một chiều: cùng hình học lane có thể là IN ở một đầu và OUT ở đầu còn lại.
        if(!hostInbound && !hostOutbound){
            if(inbound.length && !outbound.length) outbound=inbound.map(x=>({...x,id:String(x.id).replace("Left_","OneWay_OUT_")}));
            else if(outbound.length && !inbound.length) inbound=outbound.map(x=>({...x,id:String(x.id).replace("Right_","OneWay_IN_")}));
        }

        const saved=b4Pick(assembly,["placementProfile","PlacementProfile","tab4Profile","Tab4Profile"],{}) || {};
        inbound=inbound.map((lane,i)=>b4MergeLaneRule(lane,saved.inbound,i));
        outbound=outbound.map((lane,i)=>b4MergeLaneRule(lane,saved.outbound,i));

        return {
            id:b4AssemblyId(assembly),
            name:b4AssemblyName(assembly),
            median:b4Pick(assembly,["median","Median"],b4MedianDescription(components)),
            appliedRoads:b4Number(b4Pick(assembly,["appliedRoads","AppliedRoads","roadCount","RoadCount"],0)),
            inbound,
            outbound,
            raw:assembly,
            componentCount:components.length,
            laneCount:leftLanes.length+rightLanes.length || inbound.length+outbound.length,
            sourceGroupA:leftLanes.length,
            sourceGroupB:rightLanes.length,
            preview:!!assembly.preview
        };
    };

    // Chỉ để kiểm tra standalone. Khi C# gửi MCN Tab 2 thật, các mẫu này được thay thế hoàn toàn.
    const b4PreviewProfiles = [
        {
            id:"__preview_6lane__", name:"MINH HỌA · MCN 6 LÀN + DPC", preview:true,
            components:[
                {side:"Center",type:"DaiPhanCach",width:3.0,IsChiemDienTich:true},
                {side:"Left",type:"LanXe",width:3.5,IsChiemDienTich:true},
                {side:"Left",type:"LanXe",width:3.5,IsChiemDienTich:true},
                {side:"Left",type:"LanXe",width:3.5,IsChiemDienTich:true},
                {side:"Right",type:"LanXe",width:3.5,IsChiemDienTich:true},
                {side:"Right",type:"LanXe",width:3.5,IsChiemDienTich:true},
                {side:"Right",type:"LanXe",width:3.5,IsChiemDienTich:true}
            ]
        },
        {
            id:"__preview_4lane__", name:"MINH HỌA · MCN 4 LÀN", preview:true,
            components:[
                {side:"Left",type:"LanXe",width:3.5,IsChiemDienTich:true},
                {side:"Left",type:"LanXe",width:3.5,IsChiemDienTich:true},
                {side:"Right",type:"LanXe",width:3.5,IsChiemDienTich:true},
                {side:"Right",type:"LanXe",width:3.5,IsChiemDienTich:true}
            ]
        },
        {
            id:"__preview_2lane__", name:"MINH HỌA · MCN 2 LÀN", preview:true,
            components:[
                {side:"Left",type:"LanXe",width:3.5,IsChiemDienTich:true},
                {side:"Right",type:"LanXe",width:3.5,IsChiemDienTich:true}
            ]
        }
    ];
    let b4Profiles = [];

    const b4OptionHtml = selected => b4Marking93Options.map(([value,label]) => `<option value="${value}"${value===selected?' selected':''}>${label}</option>`).join("");

    const renderB4LaneCards = (targetId, lanes, direction) => {
        const host = byId(targetId);
        if(!host) return;
        if(!lanes || !lanes.length){
            host.innerHTML = '<div class="b4-lane-empty">MCN này chưa có lane thuộc nhóm ' + (direction === "in" ? "INBOUND" : "OUTBOUND") + '.</div>';
            return;
        }
        host.innerHTML = lanes.map((lane,index) => {
            const default93 = lane.marking93 || "none";
            const default76 = lane.marking76 ?? false;
            const laneName = `LÀN ${index+1}`;
            const sourceLabel = String(lane.sourceName || lane.name || "").trim();
            const geometryBase = lane.offset || [lane.sourceSide, Number.isFinite(Number(lane.localCenterOffset))?`Local ${Math.abs(Number(lane.localCenterOffset)).toFixed(2)} m`:"", Number.isFinite(Number(lane.physicalCenterOffset))?`CAD ${Math.abs(Number(lane.physicalCenterOffset)).toFixed(2)} m`:"", Number.isFinite(lane.width)?`Rộng ${lane.width.toFixed(2)} m`:""].filter(Boolean).join(" · ") || "Hình học từ MCN";
            const geometry = sourceLabel && b4TextKey(sourceLabel) !== b4TextKey(laneName) ? `${geometryBase} · ${sourceLabel}` : geometryBase;
            return `<div class="b4-lane-card" data-direction="${direction}" data-lane-index="${index}" data-lane-id="${lane.id||index}" data-source-side="${lane.sourceSide||''}" data-carriageway-group="${lane.carriagewayGroupId||''}" data-local-offset="${Number(lane.localCenterOffset||0)}" data-origin-offset="${Number(lane.carriagewayOriginOffset||0)}" data-physical-offset="${Number(lane.physicalCenterOffset??lane.centerOffset??0)}">
                <div class="b4-lane-id"><strong>${laneName}</strong><span title="${geometry}">${geometry}</span></div>
                <div class="b4-lane-field"><label>Vạch 7.6</label><select class="b4-lane-76"><option value="0"${!default76?' selected':''}>KHÔNG BỐ TRÍ</option><option value="1"${default76?' selected':''}>BỐ TRÍ 7.6</option></select></div>
                <div class="b4-lane-field"><label>Vạch 9.3</label><select class="b4-lane-93">${b4OptionHtml(default93)}</select></div>
            </div>`;
        }).join("");
    };

    const renderB4Profile = id => {
        const profile = b4Profiles.find(x => String(x.id) === String(id));
        if(!profile){
            byId("b4ProfileAssemblyName") && (byId("b4ProfileAssemblyName").textContent = "—");
            byId("b4ProfileLaneCount") && (byId("b4ProfileLaneCount").textContent = "—");
            byId("b4ProfileGroupA") && (byId("b4ProfileGroupA").textContent = "—");
            byId("b4ProfileGroupB") && (byId("b4ProfileGroupB").textContent = "—");
            byId("b4ProfileMedian") && (byId("b4ProfileMedian").textContent = "—");
            byId("b4InboundProfileCount") && (byId("b4InboundProfileCount").textContent = "0 làn");
            byId("b4OutboundProfileCount") && (byId("b4OutboundProfileCount").textContent = "0 làn");
            byId("b4InboundLaneProfile") && (byId("b4InboundLaneProfile").innerHTML = '<div class="b4-lane-empty">Chọn một MCN để khai báo cách bố trí từng làn vào nút.</div>');
            byId("b4OutboundLaneProfile") && (byId("b4OutboundLaneProfile").innerHTML = '<div class="b4-lane-empty">Chọn một MCN để khai báo cách bố trí từng làn thoát nút.</div>');
            byId("b4CurrentProfileName") && (byId("b4CurrentProfileName").textContent = "Chưa có");
            byId("b4ProfileAppliedRoads") && (byId("b4ProfileAppliedRoads").textContent = "0 tuyến / nhánh");
            const status=byId("b4ProfileStatus"); if(status){status.textContent="Chưa chọn MCN";status.className="status-pill status-wait";}
            return;
        }
        const inbound = profile.inbound || [];
        const outbound = profile.outbound || [];
        byId("b4ProfileAssemblyName") && (byId("b4ProfileAssemblyName").textContent = profile.name || "—");
        byId("b4ProfileLaneCount") && (byId("b4ProfileLaneCount").textContent = String(profile.laneCount ?? (inbound.length + outbound.length)));
        byId("b4ProfileGroupA") && (byId("b4ProfileGroupA").textContent = `${profile.sourceGroupA ?? inbound.length} làn`);
        byId("b4ProfileGroupB") && (byId("b4ProfileGroupB").textContent = `${profile.sourceGroupB ?? outbound.length} làn`);
        byId("b4ProfileMedian") && (byId("b4ProfileMedian").textContent = profile.median || "Không có");
        byId("b4InboundProfileCount") && (byId("b4InboundProfileCount").textContent = `${inbound.length} làn`);
        byId("b4OutboundProfileCount") && (byId("b4OutboundProfileCount").textContent = `${outbound.length} làn`);
        byId("b4CurrentProfileName") && (byId("b4CurrentProfileName").textContent = profile.name || "—");
        byId("b4ProfileAppliedRoads") && (byId("b4ProfileAppliedRoads").textContent = `${profile.appliedRoads || 0} tuyến / nhánh`);
        renderB4LaneCards("b4InboundLaneProfile", inbound, "in");
        renderB4LaneCards("b4OutboundLaneProfile", outbound, "out");
        const status=byId("b4ProfileStatus"); if(status){status.textContent=profile.preview?"Dữ liệu minh họa":"MCN từ Tab 2";status.className="status-pill status-ready";}
    };

    // API chính cho C#/WebView2: có thể truyền thẳng danh sách MCN của Tab 2.
    // Mỗi MCN chỉ cần id/name + components (hoặc ThanhPhan). Tab 4 tự sinh lane từ LoaiThanhPhan=LanXe.
    ARM.tab4 = ARM.tab4 || {};
    ARM.tab4.previewProfiles = b4PreviewProfiles;

    window.setTab4AssemblyProfiles = function(assemblies){
        const raw = Array.isArray(assemblies) ? assemblies : [];
        b4Profiles = raw.map(b4NormalizeAssemblyProfile).filter(p=>p.id || p.name);
        const select = byId("b4AssemblyProfileSelect");
        if(!select) return;
        const previous=select.value;
        select.innerHTML = '<option value="">Chọn mặt cắt từ Tab 2...</option>' + b4Profiles.map(p => {
            const laneText = `${p.sourceGroupA ?? 0}+${p.sourceGroupB ?? 0} làn`;
            return `<option value="${String(p.id)}">${p.name || p.id} · ${laneText}</option>`;
        }).join("");
        byId("b4AssemblyCount") && (byId("b4AssemblyCount").textContent = String(b4Profiles.length));
        byId("b4SourceAssemblyCount") && (byId("b4SourceAssemblyCount").textContent = String(b4Profiles.length));
        const next = b4Profiles.some(p=>String(p.id)===String(previous)) ? previous : "";
        select.value=next;
        renderB4Profile(next);
    };

    // Alias rõ nghĩa hơn nếu host muốn gửi đúng dữ liệu Tab 2 thay vì "profiles".
    window.setTab4AssembliesFromTab2 = window.setTab4AssemblyProfiles;
    window.buildTab4ProfileFromAssembly = b4NormalizeAssemblyProfile;
    let b4LastProfileId="";
    const setB4Dirty = dirty => { const el=byId("b4ProfileDirty"); if(el) el.hidden=!dirty; if(dirty) el.dataset.dirty="true"; else if(el) delete el.dataset.dirty; };
    ARM.tab4 = ARM.tab4 || {};
    ARM.tab4.setDirty = setB4Dirty;
    window.changeTab4AssemblyProfile = function(id){
        const dirty=byId("b4ProfileDirty")?.dataset.dirty==="true";
        if(dirty && b4LastProfileId && String(id)!==String(b4LastProfileId)){
            const ok=window.confirm("Cấu hình MCN hiện tại chưa lưu. Bỏ thay đổi và chuyển sang MCN khác?");
            if(!ok){ const sel=byId("b4AssemblyProfileSelect"); if(sel) sel.value=b4LastProfileId; return; }
        }
        setB4Dirty(false); b4LastProfileId=String(id||""); renderB4Profile(id);
    };
        window.reloadTab4AssemblyProfiles = function(){
        if(typeof window.requestTab4AssembliesFromHost === "function") return window.requestTab4AssembliesFromHost();
        if(typeof window.requestTab4AssemblyProfilesFromHost === "function") return window.requestTab4AssemblyProfilesFromHost();
        if(!b4Profiles.length || b4Profiles.every(p=>p.preview)) window.setTab4AssemblyProfiles(b4PreviewProfiles);
    };
    window.saveTab4AssemblyProfile = window.saveTab4AssemblyProfile || previewOnly;
    window.resetTab4AssemblyProfile = function(){ renderB4Profile(byId("b4AssemblyProfileSelect")?.value || ""); setB4Dirty(false); };

})(window.ARM);
