/* AutoRoadMarking UI 6.1 -> C#/WebView2 bridge.
 * UI state is intentionally transient. Engineering state is persisted by CadHost in DWG.
 */
const $ = id => document.getElementById(id);
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const num = (id, fallback=0) => { const n=Number($(id)?.value); return Number.isFinite(n)?n:fallback; };
const txt = (id, fallback='') => ($(id)?.value ?? fallback).toString().trim();
const checked = id => !!$(id)?.checked;
const setText = (id, value) => { const e=$(id); if(e)e.textContent=String(value ?? ''); };
const setValue = (id, value) => { const e=$(id); if(e)e.value=value ?? ''; };
const arr = v => Array.isArray(v) ? v : [];
const ci = (obj, ...names) => {
  if(!obj || typeof obj!=='object') return undefined;
  const keys=Object.keys(obj);
  for(const name of names){ const k=keys.find(x=>x.toLowerCase()===String(name).toLowerCase()); if(k!==undefined)return obj[k]; }
  return undefined;
};
const prop=(obj,name,fallback='')=>{const v=ci(obj,name);return v===undefined||v===null?fallback:v;};
const fmt=n=>Number.isFinite(Number(n))?Number(n).toFixed(2):'0.00';
const roadKeyOf=r=>String(prop(r,'AxisKey',prop(r,'axisKey',prop(r,'RoadKey',prop(r,'roadKey','')))));
const roadNameOf=r=>String(prop(r,'RoadName',prop(r,'roadName','')));
const idOf=x=>String(prop(x,'Id',prop(x,'id','')));

const arm = {
  host: !!window.chrome?.webview,
  selectedAxis: null,
  roadAxes: [], roadCatalog: [], templates: [], crossSections: [], currentParts: [],
  selectedTemplateIds: new Set(), selectedCrossSectionIds: new Set(), activeCrossSectionIds: new Set(),
  comparison: [], proposals: [], quantityRows: [],
  supplementary: { roads:[], templates:[], groups:[], blockRules:[], boundary1:'', boundary2:'', startStation:0, endStation:0, anchorStation:0, selectedHandles:[], manualHandles:[] },
  pendingPick: '',
  tab2EditingId: '',
  pendingCrossSectionImport: [],
  quantityOwnerType: 'all',
  lastResultAction: '',
  pendingDrawSubAction: '',
  pipelineStatus: {timCount:0,edgeCount:0,polygonCount:0}
};

function toast(message,type='info'){ if(message) window.showArmToast?.(message,type); }
function setCadStatus(ok, text){
  ['uiCadStatus','t0CadStatus','t3DrawingUnitBadge','b4CadStatus','s5CadStatus','q6CadStatus'].forEach(id=>{
    const e=$(id); if(!e)return; e.textContent=text || (ok?'CAD sẵn sàng':'Chờ CAD');
    e.className=`status-pill ${ok?'status-ready':'status-wait'}`;
  });
}
function post(action,payload={}){
  if(!arm.host){ toast('Đang chạy preview ngoài Civil 3D; không có C# host.','warning'); return false; }
  try{ window.chrome.webview.postMessage({action,payload}); return true; }
  catch(err){ toast(`Không gửi được ${action}: ${err.message}`,'error'); return false; }
}
function markCadPick(title,detail){ window.setCadInteractionState?.({active:true,title,detail}); }
function clearCadPick(){ window.setCadInteractionState?.({active:false}); }
function setStatus(id,text,kind='ready'){
  const e=$(id); if(!e)return; e.textContent=text; e.className=`status-pill status-${kind}`;
}

function backendData(message){ return ci(message,'Data','data') ?? {}; }
function backendSuccess(message){ return ci(message,'Success','success') !== false; }
function backendAction(message){ return String(ci(message,'Action','action') ?? ''); }
function backendMessage(message){ return String(ci(message,'Message','message') ?? ''); }

if(arm.host){
  window.chrome.webview.addEventListener('message', ev => onHostMessage(ev.data));
  post('Ping',{});
} else setCadStatus(false,'Preview');

function onHostMessage(message){
  if(typeof message==='string'){ try{message=JSON.parse(message);}catch{return;} }
  if(!message || typeof message!=='object')return;
  const action=backendAction(message), data=backendData(message), success=backendSuccess(message), msg=backendMessage(message);
  if(ci(data,'queued')===true) return; // ACK from router; real CAD result follows.
  arm.lastResultAction=action;
  clearCadPick();
  if(action==='LoadDefaultCrossSectionLibrary'){
    const button=$('btnLoadCrossSectionLibrary');
    if(button)button.disabled=false;
  }
  if(action==='Ping'){
    setCadStatus(true,'CAD sẵn sàng');
    initialLoad();
    return;
  }
  if(!success){
    if(action==='DrawMarkingsCAD' && arm.pendingDrawSubAction==='Step3_VeVachMep'){
      setStatus('stepStatus3','Lỗi','error');
      arm.pendingDrawSubAction='';
    }
    if(action==='LoadDefaultCrossSectionLibrary' && /chưa được triển khai|not implemented/i.test(msg||'')){
      toast('DLL CadHost đang chạy là bản cũ. Hãy Clean/Rebuild rồi NETLOAD lại DLL mới để dùng NẠP THƯ VIỆN.','error');
      setCadStatus(true,'CAD · cần nạp DLL mới');
      return;
    }
    if(action==='ImportCrossSections' && arm.pendingCrossSectionImport.length && /chưa được triển khai|not implemented/i.test(msg||'')){
      const fallback=legacyCrossSectionPayload(arm.pendingCrossSectionImport);
      toast('CadHost hiện tại chưa có ImportCrossSections; chuyển sang bộ nạp tương thích theo lô.','warning');
      post('SyncLegacyCrossSections',fallback);
      return;
    }
    toast(msg || `Lỗi ${action}`,'error'); setCadStatus(true,'CAD · lỗi thao tác'); return;
  }
  if(msg) toast(msg,'success');
  dispatch(action,data);
}

function initialLoad(){
  ['ReadCadAxisIdentitiesCAD','ReadMarkingTemplates','ReadCrossSections','RefreshPipelineStatus','ReadComparisonResults','ReadSymbolPlacementWorkspace','ReadSupplementaryWorkspace'].forEach(a=>post(a,{}));
  setCadStatus(true,'CAD sẵn sàng');
}

function dispatch(action,data){
  switch(action){
    case 'ReadCadAxisIdentitiesCAD': arm.roadAxes=arr(ci(data,'roadAxes')); renderRoadAxes(); break;
    case 'SelectCadAxisForNaming': receiveAxisSelection(data); break;
    case 'AssignCadAxisIdentity': arm.selectedAxis=null; post('ReadCadAxisIdentitiesCAD',{}); post('ReadSupplementaryWorkspace',{}); break;
    case 'RemoveCadAxisIdentity': post('ReadCadAxisIdentitiesCAD',{}); post('ReadSupplementaryWorkspace',{}); break;
    
    // Đã xóa lệnh tự nạp CSV thư viện (làm trắng danh sách, chờ bạn nhập thủ công)
    case 'ReadMarkingTemplates': arm.templates=arr(ci(data,'templates')); renderTemplates(); syncTemplatesEverywhere(); break;
    
    case 'Tab1_UpdateLayerManagementSet': arm.templates=arr(ci(data,'templates')).length?arr(ci(data,'templates')):arm.templates; renderTemplates(); syncTemplatesEverywhere(); window.setTab1ManagementSetState?.(data); break;
    case 'Tab1_ApplySharedLayerSet': {
      arm.templates=arr(ci(data,'templates')).length?arr(ci(data,'templates')):arm.templates;
      renderTemplates(); syncTemplatesEverywhere();
      const shared=arr(ci(data,'layers')).map(toUiLayerRow);
      window.onTab1SharedLayerSetApplied?.({...data,layers:shared});
      window.armBroadcastSharedLayerSet?.(arm.templates.map(toUiLayerRow),{source:'host',appliedTemplateIds:shared.map(x=>x.TemplateId)});
      break;
    }
    case 'SaveMarkingTemplate': post('ReadMarkingTemplates',{}); resetTemplateForm(false); break;
    case 'ImportMarkingTemplates': arm.templates=arr(ci(data,'templates')); renderTemplates(); syncTemplatesEverywhere(); break;
    case 'DeleteMarkingTemplates': post('ReadMarkingTemplates',{}); break;
    case 'SyncMarkingTemplateCAD': break;
    case 'ReadCrossSections': {
      arm.crossSections=arr(ci(data,'crossSections'));
      arm.activeCrossSectionIds=new Set(arr(ci(data,'activeCrossSectionIds')).map(String));
      if(arm.activeCrossSectionIds.size) arm.selectedCrossSectionIds=new Set(arm.activeCrossSectionIds);
      renderCrossSections(); syncCrossSectionsEverywhere();
      break;
    }
    case 'SaveCrossSection': post('ReadCrossSections',{}); resetCrossSectionEditor(false); break;
    case 'LoadDefaultCrossSectionLibrary':
    case 'ImportCrossSections': {
      arm.pendingCrossSectionImport=[];
      arm.crossSections=arr(ci(data,'crossSections'));
      const importedIds=arr(ci(data,'importedIds')).map(String);
      if(importedIds.length) arm.selectedCrossSectionIds=new Set(importedIds);
      renderCrossSections(); syncCrossSectionsEverywhere();
      break;
    }
    case 'SyncLegacyCrossSections': arm.pendingCrossSectionImport=[]; post('ReadCrossSections',{}); break;
    case 'DeleteCrossSections': post('ReadCrossSections',{}); break;
    case 'SyncSelectedCrossSections': {
      arm.crossSections=arr(ci(data,'crossSections')).length?arr(ci(data,'crossSections')):arm.crossSections;
      arm.activeCrossSectionIds=new Set(arr(ci(data,'activeCrossSectionIds')).map(String));
      arm.selectedCrossSectionIds=new Set(arm.activeCrossSectionIds);
      renderCrossSections(); syncCrossSectionsEverywhere();
      post('RefreshPipelineStatus',{}); post('ReadComparisonResults',{}); post('ReadSymbolPlacementWorkspace',{});
      break;
    }
    case 'SelectTimCAD': case 'SelectMepCAD': case 'SelectMepCauKienCAD': post('RefreshPipelineStatus',{}); break;
    case 'RefreshPipelineStatus':
    case 'RefreshPipelineFromCad': {
      renderPipelineStatus(data);
      if(ci(data,'refreshedFromCad')===true || action==='RefreshPipelineFromCad'){
        arm.comparison=arr(ci(data,'results'));
        renderComparison();
        post('ReadMarkingTemplates',{});
        post('ReadCrossSections',{});
        setCadStatus(true,'CAD · dữ liệu mới');
      }
      break;
    }
    case 'ResetPipelineSession': {
      resetTab3UiSession(data);
      break;
    }
    case 'ReadComparisonResults': case 'AutoMatchCAD': arm.comparison=arr(ci(data,'results')); renderComparison(); post('RefreshPipelineStatus',{}); break;
    case 'DrawMarkingsCAD': updateDrawResult(data); arm.pendingDrawSubAction=''; post('RefreshPipelineStatus',{}); break;
    case 'GenerateBatchApproachLinesCAD': setStatus('stepStatus5','Đã sinh','success'); post('RefreshPipelineStatus',{}); break;
    case 'SelectSymbolLibraryFolder': if(prop(data,'canceled',false)) break; setValue('b4LibraryPath',prop(data,'path','')); post('ScanSymbolBlockLibrary',{libraryPath:prop(data,'path','')}); break;
    case 'ScanSymbolBlockLibrary': renderBlockLibrary(data); break;
    case 'ReadSymbolPlacementWorkspace': renderSymbolWorkspace(data); break;
    case 'SaveSymbolProfile': post('ReadSymbolPlacementWorkspace',{}); break;
    case 'AnalyzeSymbolBlockPlacement': arm.proposals=arr(ci(data,'proposals')); renderProposals(); { const w=arr(ci(data,'warnings')); if(w.length) toast(w.slice(0,4).join(' · ')+(w.length>4?` · +${w.length-4} cảnh báo`:''),'warning'); } break;
    case 'GenerateSymbolBlocksCAD': post('ReadSymbolPlacementWorkspace',{}); window.setQuantitySnapshotState?.({dirty:true}); break;
    case 'SaveSymbolPlacementDefaults': applyBlockRulesFromHost(ci(data,'rules')); break;
    case 'ReadSupplementaryWorkspace': receiveSupplementaryWorkspace(data); break;
    case 'SelectSupplementaryBoundary': receiveSupplementaryBoundary(data); break;
    case 'SelectSupplementaryEntities': receiveSupplementaryEntities(data,false); break;
    case 'SelectSupplementaryBlocks': receiveSupplementaryEntities(data,true); break;
    case 'SelectSupplementaryStation': receiveSupplementaryStation(data); break;
    case 'DrawSupplementaryManualPolyline': receiveManualPolyline(data); break;
    case 'PreviewSpeedHump': receiveSpeedPreview(data); break;
    case 'GenerateSpeedHump': case 'RegisterSupplementaryEntities': case 'SyncSupplementaryBlocks': case 'SetManagedGroupLock': case 'RemoveManagedGroup': if(action==='RegisterSupplementaryEntities'){arm.supplementary.manualHandles=[];window.resetTab5ManualSelection?.();} post('ReadSupplementaryWorkspace',{}); window.setQuantitySnapshotState?.({dirty:true}); break;
    case 'ScanSupplementaryBlocks': receiveScannedBlocks(data); break;
    case 'SaveSupplementaryBlockRule': arm.supplementary.blockRules=arr(ci(data,'rules')); window.setTab5BlockRules?.(arm.supplementary.blockRules); break;
    case 'ReadMarkingQuantitiesCAD': receiveQuantities(data); break;
    case 'ExportMarkingQuantitiesExcel': toast(`Đã xuất: ${prop(data,'path','')}`,'success'); break;
  }
}

// -----------------------------------------------------------------------------
// TAB 0 · Road Identity
// -----------------------------------------------------------------------------
function receiveAxisSelection(data){
  arm.selectedAxis=data;
  const h=prop(data,'handle',prop(data,'objectHandle','—')), layer=prop(data,'currentLayer',prop(data,'layer','—')), type=prop(data,'entityType',prop(data,'type','Polyline')), length=Number(prop(data,'length',0));
  $('t0SelectedCard')?.setAttribute('data-empty','false');
  setText('t0SelType',type); setText('t0SelHandle',h); setText('t0SelLayer',layer); setText('t0SelLength',`${fmt(length)} m`);
  $('t0AssignBtn') && ($('t0AssignBtn').disabled=false);
  previewCadAxisName();
}
function renderRoadAxes(){
  const q=txt('t0Search').toLowerCase();
  const rows=arm.roadAxes.filter(r=>!q || JSON.stringify(r).toLowerCase().includes(q));
  const body=$('t0AxisBody'); if(!body)return;
  body.innerHTML=rows.length?rows.map((r,i)=>{
    const id=prop(r,'RecordId'),name=roadNameOf(r),h=prop(r,'Handle'),layer=prop(r,'Layer'),type=prop(r,'EntityType'),length=Number(prop(r,'Length',0));
    return `<tr><td class="t0-no">${i+1}</td><td title="${esc(name)}"><strong>${esc(name)}</strong><small>${esc(roadKeyOf(r))}</small></td><td>${esc(type)}</td><td>${esc(h)}</td><td>${esc(layer)}</td><td class="t0-num">${fmt(length)} m</td><td><span class="status-pill status-success">ARM</span></td><td><div class="t0-actions"><button class="btn-outline" onclick="window.zoomCadAxisIdentity('${esc(id)}')">ZOOM</button><button class="btn-outline" onclick="window.editCadAxisIdentity('${esc(id)}')">SỬA</button><button class="btn-danger" onclick="window.removeCadAxisIdentity('${esc(id)}')">GỠ</button></div></td></tr>`;
  }).join(''):'<tr class="t0-empty-row"><td colspan="8">Chưa có TIM được định danh trong DWG.</td></tr>';
  const total=arm.roadAxes.reduce((s,r)=>s+Number(prop(r,'Length',0)),0);
  setText('t0NamedCount',arm.roadAxes.length); setText('t0NamedLength',`${fmt(total)} m`); setText('t0InvalidCount','0'); setText('t0VisibleCount',`${rows.length} tuyến`); setText('t0ListMeta',`${arm.roadAxes.length} Road Identity`);
}
window.refreshCadAxisIdentities=()=>post('ReadCadAxisIdentitiesCAD',{});
window.selectCadAxisForNaming=()=>{markCadPick('CHỌN TIM TUYẾN','Chọn một Polyline tim tuyến trên CAD'); post('SelectCadAxisForNaming',{});};
window.previewCadAxisName=previewCadAxisName;
function previewCadAxisName(){ const n=txt('t0RoadName'); setText('t0LayerPreview',n||'—'); }
window.clearCadAxisSelection=()=>{arm.selectedAxis=null;$('t0SelectedCard')?.setAttribute('data-empty','true');$('t0AssignBtn')&&($('t0AssignBtn').disabled=true);setValue('t0RoadName','');previewCadAxisName();};
window.assignCadAxisName=()=>{
  if(!arm.selectedAxis)return toast('Chưa chọn TIM.','warning');
  const roadName=txt('t0RoadName'); if(!roadName)return toast('Nhập tên tuyến.','warning');
  post('AssignCadAxisIdentity',{objectHandle:prop(arm.selectedAxis,'handle',prop(arm.selectedAxis,'objectHandle','')),roadName,roadKey:normalizeKey(roadName),targetLayer:roadName});
};
window.filterCadAxisIdentities=renderRoadAxes;
window.zoomCadAxisIdentity=id=>post('ZoomCadAxisIdentity',{recordId:id});
window.editCadAxisIdentity=id=>{const r=arm.roadAxes.find(x=>String(prop(x,'RecordId'))===String(id));if(!r)return;arm.selectedAxis={handle:prop(r,'Handle'),layer:prop(r,'Layer'),entityType:prop(r,'EntityType'),length:prop(r,'Length')};receiveAxisSelection(arm.selectedAxis);setValue('t0RoadName',roadNameOf(r));previewCadAxisName();};
window.removeCadAxisIdentity=id=>{if(confirm('Gỡ Road Identity và trả lại layer gốc?'))post('RemoveCadAxisIdentity',{recordId:id,restoreOriginalLayer:true});};
function normalizeKey(s){return String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toUpperCase().replace(/[^A-Z0-9]+/g,'_').replace(/^_+|_+$/g,'');}

// -----------------------------------------------------------------------------
// TAB 1 · Marking templates
// -----------------------------------------------------------------------------
function cleanLayerPart(value){return String(value||'').trim().replace(/^\.+|\.+$/g,'');}
function currentTemplate(){
  const code=cleanLayerPart(txt('t1_LayerSuffix'));
  const stt=cleanLayerPart(txt('t1_PrefixSTT','_1.'));
  const fixed=cleanLayerPart(txt('t1_PrefixFixed','VS_MARKING.'));
  const layer=[stt||'_1',fixed||'VS_MARKING',code].filter(Boolean).join('.');
  const pattern=txt('t1_KieuNet','CUSTOM_REAL').toUpperCase();
  const isStandardPattern=['DASHED','DASHEDX2','HIDDEN'].includes(pattern);
  const isCustom=pattern==='CUSTOM_REAL';
  let custom={}; try{custom=JSON.parse(txt('t1_CustomProperties','{}')||'{}')}catch{}
  return {
    id:$('btnSubmitLayer')?.dataset.editId||'', code, name:code, description:txt('t1_MoTa'),
    layer, geometry:'line', width:num('t1_Width'), linetypeScale:num('t1_Scale',1), pattern,
    dashLength:isStandardPattern?num('t1_DashLength'):0,
    gapLength:isStandardPattern?num('t1_GapLength'):0,
    customDash1:isCustom?num('t1_CustomDash1'):0, customGap1:isCustom?num('t1_CustomGap1'):0,
    customDash2:isCustom?num('t1_CustomDash2'):0, customGap2:isCustom?num('t1_CustomGap2'):0,
    customPhase:isCustom?num('t1_CustomPhase'):0,
    color:txt('t1_ColorHex','#ffffff'), rgb:txt('t1_ColorRGB','255,255,255'),
    reference:txt('t1_StandardRef'), standardClause:txt('t1_StandardClause'),
    customProperties:custom, verified:true
  };
}
function customPropObject(t){
  const raw=prop(t,'CustomProperties',{});
  if(raw && typeof raw==='object' && !Array.isArray(raw)) return raw;
  if(typeof raw==='string'){try{return JSON.parse(raw)}catch{return {}}}
  return {};
}
function customPropValue(t,key,fallback=''){
  const obj=customPropObject(t), keys=Object.keys(obj||{}), k=keys.find(x=>x.toLowerCase()===String(key).toLowerCase());
  const v=k===undefined?undefined:obj[k];
  if(v===undefined||v===null)return fallback;
  if(typeof v==='string')return v;
  return typeof v==='object'?JSON.stringify(v):String(v);
}
function toUiLayerRow(t){
  const code=String(prop(t,'Code',prop(t,'MarkingCode','')));
  const geometryType=customPropValue(t,'geometryType','');
  const presentationRule=customPropValue(t,'presentationRule','');
  const quantity=String(prop(t,'QuantityMethod',code==='7.3'||code.toUpperCase()==='GGT'?'GENERATED':'LENGTH'));
  return {...t,
    TemplateId:idOf(t), Id:idOf(t), Code:code, MarkingCode:code,
    Layer:String(prop(t,'Layer','')), Description:String(prop(t,'Description',prop(t,'Name',''))),
    Pattern:String(prop(t,'Pattern','CUSTOM_REAL')), Width:Number(prop(t,'Width',0)),
    Scale:Number(prop(t,'LinetypeScale',1)), RGB:String(prop(t,'Rgb',prop(t,'RGB',''))),
    Color:String(prop(t,'Color','#ffffff')),
    CustomDash1:Number(prop(t,'CustomDash1',0)), CustomGap1:Number(prop(t,'CustomGap1',0)),
    CustomDash2:Number(prop(t,'CustomDash2',0)), CustomGap2:Number(prop(t,'CustomGap2',0)),
    CustomPhase:Number(prop(t,'CustomPhase',0)),
    ManagementState:String(prop(t,'ManagementState','DIRTY')),
    QuantityMethod:quantity, LayerHandle:String(prop(t,'LayerHandle','')),
    GeometryType:geometryType, PresentationRule:presentationRule
  };
}

// FIX: Chỉ gọi armSetTab1LayerRows của tab1.js để UI tự vẽ HTML xịn (có Preview/Nút bấm)
function renderTemplates(){
  const rows=arm.templates.map(toUiLayerRow);
  if(typeof window.armSetTab1LayerRows==='function'){
    window.armSetTab1LayerRows(rows,{source:'host'});
  }
  setText('lblTotalLayers',`${arm.templates.length} layer`);
}

window.toggleCustomLineTypeFields=()=>{
  const pattern=txt('t1_KieuNet').toUpperCase();
  const custom=$('t1CustomRealFields'), standard=$('t1StandardPatternFields');
  const isCustom=pattern==='CUSTOM_REAL', isStandard=['DASHED','DASHEDX2','HIDDEN'].includes(pattern);
  if(custom)custom.hidden=!isCustom; if(standard)standard.hidden=!isStandard;
  if($('t1_DashLength'))$('t1_DashLength').disabled=!isStandard;
  if($('t1_GapLength'))$('t1_GapLength').disabled=!isStandard;
};
window.syncColorToRGB=()=>{const h=txt('t1_ColorHex','#ffffff').replace('#','');if(/^[0-9a-fA-F]{6}$/.test(h)){setValue('t1_ColorRGB',`${parseInt(h.slice(0,2),16)},${parseInt(h.slice(2,4),16)},${parseInt(h.slice(4,6),16)}`);window.vePreviewLayerTab1?.();}};
window.syncRGBToHex=()=>{const p=txt('t1_ColorRGB').split(',').map(x=>Math.max(0,Math.min(255,Number(x.trim())||0)));if(p.length===3){setValue('t1_ColorHex','#'+p.map(x=>Math.round(x).toString(16).padStart(2,'0')).join(''));window.vePreviewLayerTab1?.();}};
window.updateCustomRealCycle=()=>{const cycle=num('t1_CustomDash1')+num('t1_CustomGap1')+num('t1_CustomDash2')+num('t1_CustomGap2');setValue('t1_CustomCycle',fmt(cycle));setText('t1CustomPaintRatio',`Tỷ lệ sơn tự tính theo chu kỳ: ${Math.round(computePaintRatio(currentTemplate())*100)}%`);window.vePreviewLayerTab1?.();};
window.vePreviewLayerTab1=()=>{
  const t=currentTemplate(), c=$('previewSingleLayer'); if(!c?.getContext)return;
  const ctx=c.getContext('2d'), w=c.clientWidth||320, h=c.clientHeight||32; c.width=w;c.height=h;ctx.clearRect(0,0,w,h);
  const rgb=String(t.rgb||'255,255,255').split(',').map(v=>Math.max(0,Math.min(255,Number(v)||0)));
  ctx.strokeStyle=`rgb(${rgb[0]},${rgb[1]},${rgb[2]})`;ctx.lineWidth=Math.max(2,Math.min(8,Number(t.width||0.15)*16));ctx.lineCap='butt';
  const y=Math.round(h/2), left=8, right=w-8;
  if(t.pattern==='CUSTOM_REAL' && Number(t.customGap1)>0){
    const unit=8,d=Math.max(2,Number(t.customDash1||1)*unit),g=Math.max(2,Number(t.customGap1||1)*unit);ctx.setLineDash([d,g]);ctx.lineDashOffset=-Math.max(0,Number(t.customPhase||0))*unit;
  }else ctx.setLineDash([]);
  ctx.beginPath();ctx.moveTo(left,y);ctx.lineTo(right,y);ctx.stroke();ctx.setLineDash([]);
};
window.themLayerThuCong=()=>{
  const t=currentTemplate();
  if(!t.code)return toast('Nhập hậu tố/mã vạch.','warning');
  if(typeof window.validateTab1CustomProperties==='function' && !window.validateTab1CustomProperties(true))return false;
  if(['DASHED','DASHEDX2','HIDDEN'].includes(t.pattern) && !(t.dashLength>0))return toast('Chiều dài nét phải > 0 cho kiểu nét đứt.','warning');
  if(t.pattern==='CUSTOM_REAL' && !(t.customDash1+t.customDash2>0))return toast('CUSTOM_REAL phải có ít nhất một đoạn sơn > 0 m.','warning');
  post('SaveMarkingTemplate',{template:t});
};
window.resetFormTab1=()=>resetTemplateForm(true);
function resetTemplateForm(clear=true){
  if(clear){
    ['t1_LayerSuffix','t1_MoTa','t1_CustomProperties'].forEach(id=>setValue(id,id==='t1_CustomProperties'?'{}':''));
    const host=$('t1PropertyRows'); if(host)host.innerHTML='';
  }
  if($('btnSubmitLayer')){delete $('btnSubmitLayer').dataset.editId;$('btnSubmitLayer').textContent='+ THÊM LAYER VÀO THƯ VIỆN';}
  if($('btnCancelLayerEdit'))$('btnCancelLayerEdit').hidden=true;
  window.toggleCustomLineTypeFields?.(); window.vePreviewLayerTab1?.();
}
window.armToggleLayer=(id,on)=>{on?arm.selectedTemplateIds.add(id):arm.selectedTemplateIds.delete(id);};
window.toggleSelectAllLayers=box=>{document.querySelectorAll('#layerBody .t1-row-check').forEach(x=>x.checked=!!box?.checked);};
window.updateTab1ManagementSet=()=>post('Tab1_UpdateLayerManagementSet',{layers:arm.templates.map(toUiLayerRow)});
window.apDungBoLayerDaChon=()=>{
  const request=window.getTab1SharedLayerDeploymentRequest?.()||{layers:window.armGetSelectedTab1LayerRows?.()||[]};
  if(!arr(request.layers).length)return toast('Chọn ít nhất một Layer để áp dụng và đồng bộ.','warning');
  window.setTab1SharedLayerStatus?.({state:'running'});
  return post('Tab1_ApplySharedLayerSet',request);
};
window.dongBoLayerDaChon=window.apDungBoLayerDaChon;
window.xoaLayersDaChon=()=>{
  const selected=window.armGetSelectedTab1LayerRows?.()||[];
  const ids=selected.map(x=>String(x.TemplateId||x.Id||x.id||'')).filter(Boolean);
  if(ids.length&&confirm(`Xóa ${ids.length} template khỏi thư viện dự án?`))post('DeleteMarkingTemplates',{ids});
};
window.armEditLayer=id=>{
  const t=arm.templates.find(x=>idOf(x)===id); if(!t)return;
  const layer=String(prop(t,'Layer')); const parts=layer.split('.');
  setValue('t1_LayerSuffix',parts.length>=3?parts.slice(2).join('.'):prop(t,'Code')); setValue('t1_MoTa',prop(t,'Description'));
  setValue('t1_KieuNet',prop(t,'Pattern')); setValue('t1_Width',prop(t,'Width')); setValue('t1_Scale',prop(t,'LinetypeScale',1));
  setValue('t1_ColorHex',prop(t,'Color')); setValue('t1_ColorRGB',prop(t,'Rgb'));
  setValue('t1_DashLength',prop(t,'DashLength')); setValue('t1_GapLength',prop(t,'GapLength'));
  setValue('t1_CustomDash1',prop(t,'CustomDash1')); setValue('t1_CustomGap1',prop(t,'CustomGap1'));
  setValue('t1_CustomDash2',prop(t,'CustomDash2')); setValue('t1_CustomGap2',prop(t,'CustomGap2')); setValue('t1_CustomPhase',prop(t,'CustomPhase'));
  setValue('t1_StandardRef',prop(t,'Reference')); setValue('t1_StandardClause',prop(t,'StandardClause'));
  const custom=prop(t,'CustomProperties',{}); setValue('t1_CustomProperties',JSON.stringify(custom));
  const host=$('t1PropertyRows'); if(host){host.innerHTML='';Object.entries(custom||{}).forEach(([k,v])=>window.addTab1CustomProperty?.(k,v));}
  if($('btnSubmitLayer')){$('btnSubmitLayer').dataset.editId=id;$('btnSubmitLayer').textContent='LƯU THAY ĐỔI LAYER';}
  if($('btnCancelLayerEdit'))$('btnCancelLayerEdit').hidden=false;
  window.toggleCustomLineTypeFields?.(); window.updateCustomRealCycle?.(); window.vePreviewLayerTab1?.();
};
function computePaintRatio(t){ if(t.pattern==='CONTINUOUS'||t.pattern==='SOLID')return 1; if(t.pattern==='CUSTOM_REAL'){const paint=Math.max(0,t.customDash1)+Math.max(0,t.customDash2),cycle=paint+Math.max(0,t.customGap1)+Math.max(0,t.customGap2);return cycle?paint/cycle:1;} const c=Math.max(0,t.dashLength)+Math.max(0,t.gapLength);return c?Math.max(0,t.dashLength)/c:1; }
window.xuatCSV=()=>{
  const header=['Code','Description','Layer','Pattern','Width','Scale','Color','RGB','CustomDash1','CustomGap1','CustomDash2','CustomGap2','CustomPhase','Reference','StandardClause','CustomProperties'];
  const lines=[header.join(',')].concat(arm.templates.map(t=>[
    prop(t,'Code'),prop(t,'Description'),prop(t,'Layer'),prop(t,'Pattern','CUSTOM_REAL'),prop(t,'Width'),prop(t,'LinetypeScale',1),prop(t,'Color'),prop(t,'Rgb'),
    prop(t,'CustomDash1',0),prop(t,'CustomGap1',0),prop(t,'CustomDash2',0),prop(t,'CustomGap2',0),prop(t,'CustomPhase',0),prop(t,'Reference'),prop(t,'StandardClause'),JSON.stringify(customPropObject(t))
  ].map(csv).join(',')));
  downloadText('ARM_MarkingTemplates_QCVN41.csv',lines.join('\r\n'),'text/csv;charset=utf-8');
};
function csvRowsToTemplates(rows){
  if(!Array.isArray(rows)||rows.length<2)return {templates:[],skipped:0};
  const headers=rows[0].map(x=>String(x||'').trim().replace(/^\uFEFF/,''));
  const idx=name=>headers.findIndex(h=>h.toLowerCase()===name.toLowerCase());
  const val=(row,name,fallback='')=>{const i=idx(name);return i>=0?(row[i]??fallback):fallback;};
  const templates=[];let skipped=0;
  for(const row of rows.slice(1)){
    if(!row.some(x=>String(x||'').trim()))continue;
    const code=String(val(row,'Code')).trim(),layer=String(val(row,'Layer')).trim();
    if(!code||!layer){skipped++;continue;}
    let custom={};const raw=String(val(row,'CustomProperties','')).trim();if(raw){try{custom=JSON.parse(raw)}catch{custom={note:raw}}}
    const templateId=String(val(row,'TemplateId',val(row,'Id',''))||'').trim();
    templates.push({id:templateId,code,name:code,description:val(row,'Description'),layer,pattern:String(val(row,'Pattern','CUSTOM_REAL')||'CUSTOM_REAL').toUpperCase(),
      width:Number(val(row,'Width'))||0,linetypeScale:Number(val(row,'Scale'))||1,color:val(row,'Color','#ffffff'),rgb:val(row,'RGB','255,255,255'),
      dashLength:Number(val(row,'DashLength',val(row,'Dash','0')))||0,gapLength:Number(val(row,'GapLength',val(row,'Gap','0')))||0,customDash1:Number(val(row,'CustomDash1'))||0,customGap1:Number(val(row,'CustomGap1'))||0,
      customDash2:Number(val(row,'CustomDash2'))||0,customGap2:Number(val(row,'CustomGap2'))||0,customPhase:Number(val(row,'CustomPhase'))||0,
      reference:val(row,'Reference'),standardClause:val(row,'StandardClause'),customProperties:custom,verified:true});
  }
  return {templates,skipped};
}
async function importTemplateCsvText(text,sourceLabel='CSV'){
  const parsed=csvRowsToTemplates(parseCsvText(text));
  if(!parsed.templates.length){toast(`${sourceLabel} không có template hợp lệ.`,'warning');return false;}
  const sent=post('ImportMarkingTemplates',{templates:parsed.templates});
  if(sent) toast(`Đang nạp ${parsed.templates.length} template từ ${sourceLabel}${parsed.skipped?` · bỏ qua ${parsed.skipped} dòng thiếu Code/Layer`:''}.`,'info');
  return sent;
}
window.nhapCSV=async arg=>{
  const input=arg?.target||arg,file=input?.files?.[0];if(!file)return false;
  try{return await importTemplateCsvText(await file.text(),file.name||'CSV');}
  catch(err){toast(`Không đọc được CSV: ${err?.message||err}`,'error');return false;}
  finally{if(input)input.value='';}
};
window.napThuVienCsvMacDinh=async()=>{
  const button=$('btnLoadBundledCsv'); if(button)button.disabled=true;
  try{
    const response=await fetch('./data/templates/ARM_MarkingTemplates_QCVN41_CUSTOM_COMMON_v6_FINAL.csv',{cache:'no-store'});
    if(!response.ok)throw new Error(`HTTP ${response.status}`);
    return await importTemplateCsvText(await response.text(),'CSV thư viện đi kèm');
  }catch(err){
    toast(`Không nạp được CSV thư viện đi kèm: ${err?.message||err}`,'error');
    return false;
  }finally{if(button)button.disabled=false;}
};

function parseCsvText(text){
  const rows=[];let row=[],cell='',q=false;
  const src=String(text||'').replace(/^\uFEFF/,'');
  for(let i=0;i<src.length;i++){
    const c=src[i];
    if(c==='"'){if(q&&src[i+1]==='"'){cell+='"';i++;}else q=!q;}
    else if(c===','&&!q){row.push(cell);cell='';}
    else if((c==='\n'||c==='\r')&&!q){if(c==='\r'&&src[i+1]==='\n')i++;row.push(cell);rows.push(row);row=[];cell='';}
    else cell+=c;
  }
  if(cell.length||row.length){row.push(cell);rows.push(row);} return rows;
}

// -----------------------------------------------------------------------------
// TAB 2 · Cross sections
// -----------------------------------------------------------------------------
window.syncTab3EdgeWidthFromTemplate=(force=false)=>{
  const select=$('t3_edgeLayer'), input=$('t3_edgeMarkingWidth');
  if(!select||!input)return;
  const templateId=String(select.value||'');
  const t=arm.templates.find(x=>idOf(x)===templateId)
    || arm.templates.find(x=>String(prop(x,'Code','')).toUpperCase()===String(select.selectedOptions?.[0]?.dataset?.code||'').toUpperCase());
  const raw=Number(prop(t||{},'Width',0));
  if(!(raw>0))return;
  const limited=Math.max(0.05,Math.min(0.30,raw));
  if(force||!(Number(input.value)>0))input.value=limited.toFixed(2);
};

function syncTemplateSelects(){
  const rows=arm.templates.map(toUiLayerRow);
  const options='<option value="">Chọn template vạch...</option>'+rows.map(t=>`<option value="${esc(t.TemplateId||t.Id||'')}">${esc(t.Code)} · ${esc(t.Layer)}</option>`).join('');
  const k=$('KieuVach');
  if(k){
    const old=k.value;
    k.innerHTML=options;
    if(old && [...k.options].some(o=>o.value===old))k.value=old;
  }

  // Tab 3 có bộ lọc nghiệp vụ riêng trong tab1.js. Gọi đúng một nguồn để tránh
  // bridge ghi TemplateId rồi tab1.js ghi đè bằng LayerName (lỗi contract cũ).
  if(typeof window.setTab3SharedLayerTemplates==='function'){
    window.setTab3SharedLayerTemplates(rows);
    setTimeout(()=>window.syncTab3EdgeWidthFromTemplate?.(true),0);
    return;
  }

  // Fallback khi tab1.js chưa được nạp: vẫn dùng TemplateId, tuyệt đối không dùng LayerName làm value.
  ['t3_edgeLayer','t3_stopLineLayer','t3_pedLineLayer'].forEach(id=>{
    const e=$(id);if(!e)return;
    const old=e.value;
    e.innerHTML='<option value="">Chọn template từ Tab 1...</option>'+rows.map(t=>`<option value="${esc(t.TemplateId||t.Id||'')}">${esc(t.Code)} · ${esc(t.Layer)}</option>`).join('');
    if(old && [...e.options].some(o=>o.value===old))e.value=old;
  });
}
function tab2RoleLabel(role){return ({TimTuyen:'Tim tuyến',Centerline:'Tim tuyến',LanXe:'Làn xe',Lane:'Làn xe',VachSon:'Vạch sơn',Marking:'Vạch sơn',DaiPhanCach:'Dải phân cách',Median:'Dải phân cách',ViaHe:'Vỉa hè / mép',Sidewalk:'Vỉa hè / mép'})[role]||role||'Cấu kiện';}
function tab2SideLabel(side){return ({Left:'LEFT',Right:'RIGHT',Center:'TIM'})[side]||String(side||'').toUpperCase();}
function tab2SideClass(side){return String(side).toLowerCase()==='left'?'is-left':String(side).toLowerCase()==='right'?'is-right':'is-center';}
function setTab2EditingState(editing){
  const hint=$('t2EditModeHint');
  if(hint){hint.classList.toggle('is-editing',!!editing);const title=hint.querySelector('span');const sub=hint.querySelector('small');if(title)title.textContent=editing?'Đang chỉnh sửa mặt cắt':'Chế độ tạo mới';if(sub)sub.textContent=editing?'Bề rộng trong cột Rộng có thể sửa trực tiếp; preview cập nhật ngay.':'Bề rộng cấu kiện có thể nhập ở form hoặc sửa trực tiếp trong bảng.';}
  if($('btnHuySua'))$('btnHuySua').style.display=editing?'block':'none';
  if($('btnSaveMatCat'))$('btnSaveMatCat').textContent=editing?'CẬP NHẬT MẶT CẮT':'LƯU MẶT CẮT';
}
window.onLoaiChange=()=>{
  const type=txt('LoaiThanhPhan'),isCenter=type==='TimTuyen',isMark=type==='VachSon';
  const side=$('BenBoTri'),width=$('BeRongThanhPhan'),occupies=$('IsChiemDienTich');
  if($('divKieuVach'))$('divKieuVach').style.display=isMark?'block':'none';
  if($('divBeRong'))$('divBeRong').style.display='block';
  if(side){side.disabled=isCenter;if(isCenter)side.value='Center';else if(side.value==='Center')side.value='Right';}
  if(width){width.disabled=isCenter;if(isCenter)width.value='0';else if(!(Number(width.value)>0))width.value=isMark?'0':'3.50';}
  if(occupies){occupies.disabled=isCenter||isMark;if(isCenter||isMark)occupies.checked=false;else if(!occupies.checked)occupies.checked=true;}
  if(isMark)window.onTab2MarkingTemplateChange?.(true);
};
window.onTab2MarkingTemplateChange=(force=true)=>{
  const id=txt('KieuVach'),t=arm.templates.find(x=>idOf(x)===id);if(!t)return;
  const width=$('BeRongThanhPhan'),templateWidth=Number(prop(t,'Width',0));
  if(width&&templateWidth>0&&(force||!(Number(width.value)>0)))width.value=String(templateWidth);
};
window.lamMoiLayerTab2=()=>{post('ReadMarkingTemplates',{});};
window.themThanhPhan=()=>{
  const type=txt('LoaiThanhPhan'),side=type==='TimTuyen'?'Center':txt('BenBoTri'),width=num('BeRongThanhPhan'),template=txt('KieuVach');
  if(type!=='TimTuyen'&&type!=='VachSon'&&width<=0)return toast('Bề rộng cấu kiện phải > 0.','warning');
  if(type==='VachSon'&&!template)return toast('Hãy chọn Layer/template vạch sơn từ thư viện Tab 1.','warning');
  arm.currentParts.push({id:`P${String(arm.currentParts.length+1).padStart(3,'0')}`,order:arm.currentParts.length+1,side,role:type,name:tab2RoleLabel(type),width:isFinite(width)?Math.max(0,width):0,occupiesWidth:checked('IsChiemDienTich')&&type!=='VachSon'&&type!=='TimTuyen',template});
  renderCurrentParts();
};
function updateCrossSectionMetrics(){
  const total=arm.currentParts.filter(p=>p.occupiesWidth).reduce((sum,p)=>sum+Math.max(0,Number(p.width||0)),0);
  setText('liveWidthTxt',`Tổng bề rộng hình học (2 bên): ${fmt(total)} m`);
  drawCrossSectionPreview();
}
function renderCurrentParts(){
  const b=$('thanhPhanBody');
  if(b)b.innerHTML=arm.currentParts.length?arm.currentParts.map((p,i)=>{
    const center=String(p.role)==='TimTuyen'||String(p.role)==='Centerline';
    const sub=p.template?`<span class="t2-part-sub">${esc(p.template)}</span>`:'';
    return `<tr><td>${i+1}</td><td><span class="t2-side-badge ${tab2SideClass(p.side)}">${esc(tab2SideLabel(p.side))}</span></td><td><span class="t2-part-main">${esc(tab2RoleLabel(p.role))}</span>${sub}</td><td class="t2-width-cell"><input class="t2-width-editor" type="number" min="0" step="0.01" value="${Number(p.width||0)}" ${center?'disabled':''} oninput="window.armSetPartWidth(${i},this.value)" aria-label="Bề rộng cấu kiện ${i+1}"></td><td><button class="t2-table-action move" onclick="window.armMovePart(${i})" ${center?'disabled':''}>ĐỔI</button></td><td><button class="t2-table-action delete" onclick="window.armRemovePart(${i})">XÓA</button></td></tr>`;
  }).join(''):'<tr><td colspan="6" style="text-align:center">Chưa có cấu kiện.</td></tr>';
  updateCrossSectionMetrics();
}
window.armSetPartWidth=(i,value)=>{const p=arm.currentParts[i];if(!p)return;const n=Number(value);if(Number.isFinite(n))p.width=Math.max(0,n);updateCrossSectionMetrics();};
window.armRemovePart=i=>{arm.currentParts.splice(i,1);arm.currentParts.forEach((p,n)=>{p.order=n+1;});renderCurrentParts();};
window.armMovePart=i=>{const p=arm.currentParts[i];if(!p)return;p.side=p.side==='Left'?'Right':p.side==='Right'?'Left':p.side;renderCurrentParts();};
window.doiXungQuaTim=()=>{
  const source=arm.currentParts.filter(p=>p.side==='Left'||p.side==='Right');
  const base=arm.currentParts.length;
  const mirrored=source.map((p,index)=>({...p,id:`P${String(base+index+1).padStart(3,'0')}`,order:base+index+1,side:p.side==='Left'?'Right':'Left'}));
  arm.currentParts=arm.currentParts.concat(mirrored);renderCurrentParts();
};
window.luuMatCatVaoDanhSach=()=>{const name=txt('TenMatCat');if(!name)return toast('Nhập tên mặt cắt.','warning');if(!arm.currentParts.length)return toast('Mặt cắt chưa có cấu kiện.','warning'); const id=arm.tab2EditingId||normalizeKey(name)||name; post('SaveCrossSection',{crossSection:{id,name,components:arm.currentParts}});};
window.huyCheDoSua=()=>resetCrossSectionEditor(true);
function resetCrossSectionEditor(clear=true){arm.tab2EditingId='';arm.currentParts=[];if(clear)setValue('TenMatCat','');setTab2EditingState(false);renderCurrentParts();}
function buildUniqueCrossSectionName(baseName){
  const raw=String(baseName||'Mặt cắt').trim()||'Mặt cắt';
  const existing=new Set(arm.crossSections.map(x=>String(prop(x,'Name',idOf(x))).trim().toLowerCase()));
  if(!existing.has(raw.toLowerCase()))return raw;
  for(let i=2;i<=999;i++){
    const candidate=`${raw} (${i})`;
    if(!existing.has(candidate.toLowerCase()))return candidate;
  }
  return `${raw} (${Date.now()})`;
}
function buildUniqueCrossSectionId(name){
  const taken=new Set(arm.crossSections.map(x=>String(idOf(x)).toLowerCase()));
  let base=normalizeKey(name)||`mcn_${Date.now()}`;
  if(!taken.has(base.toLowerCase()))return base;
  for(let i=2;i<=999;i++){
    const candidate=`${base}_${i}`;
    if(!taken.has(candidate.toLowerCase()))return candidate;
  }
  return `${base}_${Date.now()}`;
}
function renderCrossSections(){
  const q=txt('timKiemMatCat').toLowerCase(),rows=arm.crossSections.filter(x=>!q||JSON.stringify(x).toLowerCase().includes(q)),h=$('danhSachMatCatDaTao');
  if(h)h.innerHTML=rows.length?rows.map(x=>{
    const id=idOf(x),comps=arr(ci(x,'Components'));
    const total=fmt(prop(x,'TotalSectionWidth',comps.reduce((sum,p)=>sum+(prop(p,'OccupiesWidth',true)?Number(prop(p,'Width',0)):0),0)));
    return `<div class="card-matcat"><div class="card-matcat-main"><strong>${esc(prop(x,'Name',id))}</strong><div class="helper">${comps.length} cấu kiện · W=${total} m</div></div><div class="card-matcat-actions"><label class="t2-card-check"><input type="checkbox" ${arm.selectedCrossSectionIds.has(id)?'checked':''} onchange="window.armToggleMcn('${esc(id)}',this.checked)"><span>Chọn</span></label><button class="btn-outline t2-card-btn" onclick="window.armEditMcn('${esc(id)}')">SỬA</button><button class="btn-outline t2-card-btn" onclick="window.armDuplicateMcn('${esc(id)}')">NHÂN BẢN</button><button class="btn-danger t2-card-btn" onclick="window.armDeleteSingleMcn('${esc(id)}')">XÓA</button></div></div>`;
  }).join(''):'<div class="note-box">Chưa có mặt cắt. Có thể tạo mới hoặc nạp thư viện JSON.</div>';
  setText('lblTotalAssemblies',arm.crossSections.length);
  const all=$('cbxSelectAllAssemblies');if(all){all.checked=arm.crossSections.length>0&&arm.crossSections.every(x=>arm.selectedCrossSectionIds.has(idOf(x)));all.indeterminate=arm.selectedCrossSectionIds.size>0&&!all.checked;}
}
window.renderDanhSachDaLuu=renderCrossSections;
window.armToggleMcn=(id,on)=>{on?arm.selectedCrossSectionIds.add(id):arm.selectedCrossSectionIds.delete(id);renderCrossSections();};
window.toggleSelectAllAssemblies=box=>{arm.selectedCrossSectionIds.clear();if(box.checked)arm.crossSections.forEach(x=>arm.selectedCrossSectionIds.add(idOf(x)));renderCrossSections();};
window.armEditMcn=id=>{const x=arm.crossSections.find(v=>idOf(v)===id);if(!x)return;arm.tab2EditingId=id;setValue('TenMatCat',prop(x,'Name',id));arm.currentParts=arr(ci(x,'Components')).map((p,index)=>({id:prop(p,'Id',`P${String(index+1).padStart(3,'0')}`),order:Number(prop(p,'Order',index+1)),side:prop(p,'Side'),role:roleToUi(prop(p,'Role')),name:prop(p,'Name'),width:Number(prop(p,'Width',0)),occupiesWidth:prop(p,'OccupiesWidth',true),template:prop(p,'Template')}));setTab2EditingState(true);renderCurrentParts();};
window.armDuplicateMcn=id=>{
  const src=arm.crossSections.find(v=>idOf(v)===id);if(!src)return;
  const duplicated=JSON.parse(JSON.stringify(src));
  const sourceName=String(prop(src,'Name',id)||id);
  const newName=buildUniqueCrossSectionName(`${sourceName} - Bản sao`);
  duplicated.id=buildUniqueCrossSectionId(newName);
  duplicated.Id=duplicated.id;
  duplicated.name=newName;
  duplicated.Name=newName;
  const parts=arr(ci(duplicated,'Components','components')).map((p,index)=>({
    ...p,
    id:prop(p,'Id',prop(p,'id',`P${String(index+1).padStart(3,'0')}`)),
    Id:prop(p,'Id',prop(p,'id',`P${String(index+1).padStart(3,'0')}`)),
    order:Number(prop(p,'Order',prop(p,'order',index+1)))||index+1,
    Order:Number(prop(p,'Order',prop(p,'order',index+1)))||index+1
  }));
  duplicated.components=parts;
  duplicated.Components=parts;
  post('SaveCrossSection',{crossSection:duplicated});
  toast(`Đã tạo bản sao: ${newName}`,'success');
};
window.armDeleteSingleMcn=id=>{
  const x=arm.crossSections.find(v=>idOf(v)===id);if(!x)return;
  const name=String(prop(x,'Name',id)||id);
  if(!confirm(`Xóa mặt cắt "${name}"?`))return;
  arm.selectedCrossSectionIds.delete(id);
  post('DeleteCrossSections',{ids:[id]});
};
window.dongBoAssemblyDaChon=()=>{const ids=[...arm.selectedCrossSectionIds];if(!ids.length)return toast('Hãy chọn ít nhất một mặt cắt để đồng bộ.','warning');post('SyncSelectedCrossSections',{ids});};
window.xoaAssemblyDaChon=()=>{const ids=[...arm.selectedCrossSectionIds];if(ids.length&&confirm(`Xóa ${ids.length} mặt cắt?`)){post('DeleteCrossSections',{ids});arm.selectedCrossSectionIds.clear();}};
window.xuatJSONDaChon=()=>{const ids=arm.selectedCrossSectionIds.size?arm.selectedCrossSectionIds:new Set(arm.crossSections.map(idOf));downloadText('ARM_CrossSections.json',JSON.stringify({version:1,crossSections:arm.crossSections.filter(x=>ids.has(idOf(x)))},null,2),'application/json');};
function crossSectionsFromJson(data){
  if(Array.isArray(data))return data;
  if(!data||typeof data!=='object')return [];
  const nested=ci(data,'crossSections','assemblies','sections','library');
  if(Array.isArray(nested))return nested;
  return (ci(data,'id','Id','name','Name')!==undefined)?[data]:[];
}
function legacyCrossSectionPayload(items){
  return {
    Layers:[],
    Assemblies:items.map((x,index)=>({
      TenMatCat:String(prop(x,'Name',prop(x,'Id',`MCN_${index+1}`))),
      ThanhPhan:arr(ci(x,'Components','components')).map(p=>({
        Loai:roleToUi(String(prop(p,'Role',prop(p,'Loai','Component')))),
        Side:String(prop(p,'Side','Center')),
        IsChiemDienTich:!!prop(p,'OccupiesWidth',prop(p,'IsChiemDienTich',true)),
        BeRong:Number(prop(p,'Width',prop(p,'BeRong',0)))||0,
        Kieu:String(prop(p,'Template',prop(p,'Kieu','')))
      }))
    })),
    Mode:'UPSERT'
  };
}
function importCrossSectionLibrary(data,sourceLabel='JSON'){
  const items=crossSectionsFromJson(data).filter(x=>x&&typeof x==='object');
  if(!items.length){toast(`${sourceLabel} không có danh sách mặt cắt hợp lệ.`,'warning');return false;}
  arm.pendingCrossSectionImport=items;
  const sent=post('ImportCrossSections',{crossSections:items,mode:'UPSERT',source:sourceLabel});
  if(!sent)arm.pendingCrossSectionImport=[];
  if(sent)toast(`Đang nạp ${items.length} mặt cắt từ ${sourceLabel}.`,'info');
  return sent;
}
window.nhapJSON=async arg=>{
  const input=arg?.target||arg,file=input?.files?.[0];if(!file)return false;
  try{return importCrossSectionLibrary(JSON.parse(await file.text()),file.name||'JSON');}
  catch(err){toast(`Không đọc được JSON mặt cắt: ${err?.message||err}`,'error');return false;}
  finally{if(input)input.value='';}
};
window.napThuVienMatCatMacDinh=()=>{
  const button=$('btnLoadCrossSectionLibrary');
  if(button)button.disabled=true;
  const sent=post('LoadDefaultCrossSectionLibrary',{});
  if(!sent && button)button.disabled=false;
  return sent;
};
function roleToUi(r){return ({Centerline:'TimTuyen',Lane:'LanXe',Marking:'VachSon',Median:'DaiPhanCach',Sidewalk:'ViaHe'})[r]||r;}
function drawCrossSectionPreview(){
  const c=$('crossSectionCanvas');if(!c?.getContext)return;
  const rect=c.getBoundingClientRect(),w=Math.max(320,Math.round(rect.width||c.clientWidth||700)),h=Math.max(220,Math.round(rect.height||c.clientHeight||300)),dpr=Math.max(1,Math.min(2,window.devicePixelRatio||1));
  const backingW=Math.round(w*dpr),backingH=Math.round(h*dpr);if(c.width!==backingW)c.width=backingW;if(c.height!==backingH)c.height=backingH;
  const ctx=c.getContext('2d');ctx.setTransform(dpr,0,0,dpr,0,0);ctx.clearRect(0,0,w,h);
  const physical=arm.currentParts.filter(p=>p.occupiesWidth&&Number(p.width)>0),leftTotal=physical.filter(p=>String(p.side).toLowerCase()==='left').reduce((sum,p)=>sum+Number(p.width||0),0),rightTotal=physical.filter(p=>String(p.side).toLowerCase()==='right').reduce((sum,p)=>sum+Number(p.width||0),0),centerTotal=physical.filter(p=>String(p.side).toLowerCase()==='center').reduce((sum,p)=>sum+Number(p.width||0),0),centerHalf=centerTotal/2;
  const centerX=w/2,pad=Math.max(28,Math.min(48,w*.055)),halfUsable=Math.max(80,(w-2*pad)/2),span=Math.max(1,centerHalf+leftTotal,centerHalf+rightTotal),scale=halfUsable/span,top=Math.max(66,Math.round(h*.23)),sectionH=Math.max(88,Math.min(125,Math.round(h*.38)));
  ctx.font='11px Segoe UI';ctx.textBaseline='middle';ctx.strokeStyle='#78a0c4';ctx.lineWidth=1;ctx.setLineDash([5,4]);ctx.beginPath();ctx.moveTo(centerX,24);ctx.lineTo(centerX,h-22);ctx.stroke();ctx.setLineDash([]);ctx.fillStyle='#b8cbe0';ctx.textAlign='center';ctx.fillText('TIM',centerX,40);
  let left=centerHalf,right=centerHalf,centerCursor=-centerHalf;
  for(const p of arm.currentParts){
    const side=String(p.side||'').toLowerCase(),role=String(p.role||'');
    if(p.occupiesWidth){
      const widthM=Math.max(0,Number(p.width||0)),pw=widthM*scale;if(pw<=0)continue;let x;
      if(side==='left'){x=centerX-(left+widthM)*scale;left+=widthM;}
      else if(side==='right'){x=centerX+right*scale;right+=widthM;}
      else{x=centerX+centerCursor*scale;centerCursor+=widthM;}
      ctx.fillStyle='rgba(74,163,255,.10)';ctx.fillRect(x,top,pw,sectionH);ctx.strokeStyle='#557695';ctx.strokeRect(x,top,pw,sectionH);
      ctx.save();ctx.beginPath();ctx.rect(x+2,top+2,Math.max(0,pw-4),Math.max(0,sectionH-4));ctx.clip();ctx.fillStyle='#dce7f3';ctx.textAlign='center';ctx.fillText(`${tab2RoleLabel(role)} · ${fmt(widthM)} m`,x+pw/2,top+sectionH/2);ctx.restore();
      continue;
    }
    if(role!=='VachSon'&&role!=='Marking')continue;
    const t=arm.templates.find(x=>idOf(x)===String(p.template))||arm.templates.find(x=>String(prop(x,'Code'))===String(p.template))||{},baseOffset=side==='left'?-left:side==='right'?right:0,baseX=centerX+baseOffset*scale,code=String(prop(t,'Code',p.template||'')),rgb=String(prop(t,'Rgb','255,255,255')).split(',').map(v=>Math.max(0,Math.min(255,Number(v)||0)));
    ctx.strokeStyle=`rgb(${rgb[0]},${rgb[1]},${rgb[2]})`;ctx.lineWidth=Math.max(2,Math.min(6,Number(prop(t,'Width',0.15))*16));
    const rule=customPropValue(t,'presentationRule',''),isDouble=code==='1.3'||rule==='DUPLICATE_ON_CROSS_SECTION',stripe=Number(prop(t,'Width',0.15))||0.15,inner=Number(customPropValue(t,'standardInnerGap_m',customPropValue(t,'standardInnerGapMin_m','0.15')))||0.15,delta=(stripe+inner)*scale/2;
    const xs=isDouble?[baseX-delta,baseX+delta]:[baseX];for(const mx of xs){ctx.beginPath();ctx.moveTo(mx,top-8);ctx.lineTo(mx,top+sectionH+8);ctx.stroke();}
    ctx.fillStyle='#f7f9fc';ctx.textAlign=baseX<centerX?'right':'left';ctx.fillText(code||'Vạch',baseX+(baseX<centerX?-5:5),top+sectionH+18);
  }
  ctx.fillStyle='#7590aa';ctx.textAlign='left';ctx.font='10px Segoe UI';ctx.fillText(`LEFT ${fmt(leftTotal+centerHalf)} m`,pad,h-14);ctx.textAlign='right';ctx.fillText(`RIGHT ${fmt(rightTotal+centerHalf)} m`,w-pad,h-14);
  if(!arm.currentParts.length){ctx.fillStyle='#6f859b';ctx.textAlign='center';ctx.font='12px Segoe UI';ctx.fillText('Thêm cấu kiện để xem trước mặt cắt',centerX,top+sectionH/2);}
}
window.redrawTab2Preview=drawCrossSectionPreview;

// -----------------------------------------------------------------------------
// TAB 3 · Generate markings
// -----------------------------------------------------------------------------
function effectiveCrossSectionsUi(){if(!arm.activeCrossSectionIds.size)return arm.crossSections;return arm.crossSections.filter(sec=>arm.activeCrossSectionIds.has(idOf(sec)));}
function syncCrossSectionsEverywhere(){renderCrossSections();const active=effectiveCrossSectionsUi();window.setTab4AssembliesFromTab2?.(decorateSectionsForTab4(active));setText('t3_daGan',active.length);}
function decorateSectionsForTab4(sections=effectiveCrossSectionsUi()){return sections.map(sec=>{const profile=(arm.symbolProfiles||[]).find(p=>String(prop(p,'AssemblyId'))===idOf(sec));const raw={id:idOf(sec),name:prop(sec,'Name'),components:arr(ci(sec,'Components'))};if(!profile)return raw;const laneMap=new Map(arr(ci(profile,'Lanes')).map(l=>[String(prop(l,'LaneId')),l]));raw.components=raw.components.map((c,i)=>({...c,_profile:laneMap.get(String(prop(c,'Id',i)))}));return raw;});}
function renderPipelineStatus(d){const tc=Number(prop(d,'timCount',0)),ec=Number(prop(d,'edgeCount',0)),cc=Number(prop(d,'componentEdgeCount',0)),mc=Number(prop(d,'mcnCount',arm.crossSections.length)),pc=Number(prop(d,'polygonCount',0));arm.pipelineStatus={timCount:tc,edgeCount:ec,polygonCount:pc};setText('txtCountTim',tc);setText('txtCountMep',ec);setText('txtCountMepCauKien',cc);setText('t3_tongTim',tc);setText('t3_tongMep',ec);setText('t3_tongMepCauKien',cc);setText('t3_daGan',mc);setText('polygonCreated',pc);setText('sumIntersections',pc);const d713=Number(prop(d,'stopToCrosswalkDistance',NaN));if(Number.isFinite(d713)&&d713>0)setValue('t3_stopToPedDistance',d713);setStatus('statusTim',tc?'Đã chọn':'Chưa chọn',tc?'ready':'wait');setStatus('statusMep',ec?'Đã chọn':'Chưa chọn',ec?'ready':'wait');setStatus('statusMepCauKien',cc?'Đã chọn':'Tùy chọn',cc?'ready':'wait');setStatus('statusAssembly',mc?'Sẵn sàng':'Chưa có',mc?'ready':'wait');window.setTab3WorkflowGate?.({step1:tc>0&&ec>0&&mc>0,step2:tc>0,step3:pc>0,step4:pc>0});const step3=$('btnStep3');if(step3){step3.disabled=ec<=0;step3.title=pc>0?'Sinh vạch mép theo polygon nút giao':'Bước 2 chưa có polygon; bấm để xem hướng dẫn';}}
function resetTab3UiSession(data={}){
  arm.comparison=[];
  arm.pipelineStatus={timCount:0,edgeCount:0,polygonCount:0};
  arm.pendingDrawSubAction='';
  renderPipelineStatus({...data,timCount:0,edgeCount:0,componentEdgeCount:0,polygonCount:0,comparisonCount:0,matchedCount:0,warningCount:0});
  renderComparison();
  ['polygonCreated','polygonTrimmed','sumLongitudinal','sumIntersections','sumEdgeMarkings','sumStopCrosswalk','sumWarnings','sumErrors'].forEach(id=>setText(id,0));
  setStatus('stepStatus1','Chờ','wait');
  setStatus('stepStatus2','Chờ','wait');
  setStatus('stepStatus3','Chờ','wait');
  setStatus('stepStatus4','Chờ','wait');
  setValue('t3_edgeOffset','0.50');
  setValue('t3_edgeMarkingWidth','0.15');
  setValue('t3_crosswalkWidth','3.00');
  setValue('t3_stopToPedDistance','2.00');
  setCadStatus(true,'CAD · phiên mới');
}
window.chonTimTuCAD=()=>{markCadPick('CHỌN TIM','Quét các RoadAxis/TIM cần xử lý');post('SelectTimCAD',{});};
window.chonMepTuCAD=()=>{markCadPick('CHỌN MÉP','Quét toàn bộ mép ngoài phần xe chạy');post('SelectMepCAD',{});};
window.chonMepCauKienTuCAD=()=>{markCadPick('CHỌN MÉP CẤU KIỆN','Quét mép DPC, đảo, bó vỉa trung gian');post('SelectMepCauKienCAD',{});};
window.dongBoThuVienTab3=()=>{post('ReadCrossSections',{});post('ReadMarkingTemplates',{});post('RefreshPipelineStatus',{});};
window.resetTab3Session=()=>{
  toast('Đang đưa Tab 3 về trạng thái ban đầu...','info');
  return post('ResetPipelineSession',{});
};
// Alias tương thích các đoạn code/UI cũ; từ phiên bản này "LÀM MỚI DỮ LIỆU" là reset phiên, không quét CAD.
window.refreshTab3DataFromCad=window.resetTab3Session;
window.goiAction=uiAction=>{
  switch(uiAction){
    case 'Cancel_Cad_Interaction': clearCadPick(); return;
    case 'Refresh_Pipeline_Status': return window.resetTab3Session();
    case 'Refresh_Comparison_Results': return post('ReadComparisonResults',{});
    case 'Step1_PhanTichGhepCap': return post('AutoMatchCAD',{});
    case 'Step1_VeVachDocTuyen': return post('DrawMarkingsCAD',{subAction:'Step1_VeVachDocTuyen'});
    case 'Step1_ZoomKetQuaCanXuLy': return post('ZoomComparisonIssues',{});
    case 'Step2_VeDaGiacNut': window.setCadInteractionState?.({active:false}); toast('Đang phân tích TIM/MÉP: tự nhận sừng bò; ngã ba T hỗ trợ 2 sừng bò + 1 mép thẳng đối diện...','info'); return post('DrawMarkingsCAD',{subAction:uiAction,helperLayer:txt('t3_helperLayer','_9.HOTRO_VUNG_NUT_GIAO')});
    case 'Step2_CapNhatDaGiacNut': return post('DrawMarkingsCAD',{subAction:uiAction,helperLayer:txt('t3_helperLayer','_9.HOTRO_VUNG_NUT_GIAO')});
    case 'Step2_CatVachTrongDaGiac': return post('DrawMarkingsCAD',{subAction:uiAction});
    case 'Step3_VeVachMep': {
      // Không chặn action bằng polygonCount cache ở UI. Sau khi Bước 2 vừa tạo polygon,
      // RefreshPipelineStatus có thể chưa về kịp nên cache vẫn = 0 dù polygon thật đã tồn tại.
      // Backend là nguồn sự thật và sẽ trả lỗi rõ ràng nếu DWG thực sự chưa có polygon.
      if((arm.pipelineStatus?.polygonCount||0)<=0){
        post('RefreshPipelineStatus',{});
        toast('Đang xác nhận polygon trực tiếp từ CAD và gửi lệnh sinh vạch mép...','info');
      }
      const templateId=txt('t3_edgeLayer');
      if(!templateId){
        toast('Bước 3 chưa có template vạch mép từ Tab 1. Đang nạp lại thư viện...','warning');
        post('ReadMarkingTemplates',{});
        return false;
      }
      const offset=num('t3_edgeOffset',0.5);
      if(!(offset>0)){
        toast('Offset vạch mép phải lớn hơn 0.','warning');
        return false;
      }
      const markingWidth=num('t3_edgeMarkingWidth',0.15);
      if(!Number.isFinite(markingWidth)||markingWidth<0.05||markingWidth>0.30){
        toast('Bề rộng vạch mép phải trong khoảng 0.05–0.30 m.','warning');
        return false;
      }
      const selected=$('t3_edgeLayer')?.selectedOptions?.[0];
      arm.pendingDrawSubAction=uiAction;
      setStatus('stepStatus3','Đang sinh','running');
      toast(`Đang sinh vạch mép · ${selected?.textContent?.trim()||templateId} · offset ${offset.toFixed(2)} m · rộng ${markingWidth.toFixed(2)} m...`,'info');
      const sent=post('DrawMarkingsCAD',{
        subAction:uiAction,
        templateId,
        templateCode:selected?.dataset?.code||'',
        layerName:selected?.dataset?.layer||'',
        offset,
        markingWidth
      });
      if(!sent){ arm.pendingDrawSubAction=''; setStatus('stepStatus3','Chờ','wait'); }
      return sent;
    }
    case 'Step4_VeVachDungDiBo': {
      const centerDistance=num('t3_stopToPedDistance',2);
      const crossingWidth=num('t3_crosswalkWidth',3);
      if(!(crossingWidth>=3)){toast('Chiều dài vạch 7.3 phải từ 3.0 m trở lên.','warning');return false;}
      if(!(centerDistance>0)){toast('Khoảng cách tim 7.3 - tim 7.1 phải lớn hơn 0.','warning');return false;}
      return post('DrawMarkingsCAD',{
        subAction:'Step4_VeVachDungVaDiBo',
        stopTemplateId:txt('t3_stopLineLayer'),
        pedestrianTemplateId:txt('t3_pedLineLayer'),
        distance:centerDistance,
        crossingWidth
      });
    }
    case 'Step5_VeVachTiepCan': {
      const distance=num('t3_approachDistance',20);
      const code=txt('t3_approachCode','2.2');
      if(!(distance>0)){toast('Khoảng cách lùi phải lớn hơn 0.','warning');return false;}
      return post('GenerateBatchApproachLinesCAD',{
        markingCode:code,
        distance:distance
      });
    }
    case 'Tab1_UpdateLayerManagementSet': return window.updateTab1ManagementSet?.();
    case 'Tab1_ApplySharedLayerSet': return window.apDungBoLayerDaChon?.();
    case 'Tab1_EditLayer': return true;
    case 'Tab1_DuplicateLayer': return true;
    case 'Tab1_DeleteLayer': return true;
    case 'Tab1_DeleteSelectedLayers': return window.xoaLayersDaChon?.();
    default: return handleTab5Action(uiAction);
  }
};
function renderComparison(){const q=txt('t3ResultSearch').toLowerCase(),f=txt('t3ResultFilter','all');const rows=arm.comparison.filter(r=>(f==='all'||String(prop(r,'Status')).toLowerCase()===f)&&(!q||JSON.stringify(r).toLowerCase().includes(q)));const b=$('comparisonResultBody');if(b)b.innerHTML=rows.length?rows.map((r,i)=>{const st=String(prop(r,'Status')),kind=st==='matched'?'success':st==='unpaired'?'error':'running';return `<tr><td>${i+1}</td><td><strong>${esc(prop(r,'Road',prop(r,'RoadKey')))}</strong><small>TIM ${esc(prop(r,'TimHandle'))} · W=${fmt(prop(r,'Width'))}m · L/R=${fmt(prop(r,'LeftWidth'))}/${fmt(prop(r,'RightWidth'))}</small></td><td>${esc(prop(r,'Mcn','—'))}</td><td><span class="status-pill status-${kind}">${esc(st)}</span><small>${esc(prop(r,'Reason',''))}</small></td><td><button class="btn-outline compare-row-btn" onclick="window.armZoomComparison('${esc(prop(r,'Id'))}')">⌖</button></td></tr>`;}).join(''):'<tr class="compare-empty"><td colspan="5">Chưa có kết quả phù hợp.</td></tr>';const matched=arm.comparison.filter(x=>String(prop(x,'Status')).toLowerCase()==='matched').length,action=arm.comparison.filter(x=>String(prop(x,'Status')).toLowerCase()==='action').length,unpaired=arm.comparison.filter(x=>String(prop(x,'Status')).toLowerCase()==='unpaired').length;setText('compareMatched',matched);setText('compareNeedsAction',action);setText('compareUnpaired',unpaired);setText('compareTotal',arm.comparison.length);setText('compareVisibleCount',`${rows.length} kết quả`);setText('sumWarnings',action+unpaired);setStatus('stepStatus1',arm.comparison.length?'Đã phân tích':'Chờ',arm.comparison.length?'ready':'wait');}
window.filterComparisonResults=renderComparison; window.armZoomComparison=id=>post('ZoomComparisonResult',{recordId:id});
function updateDrawResult(d){const sub=String(prop(d,'subAction')),count=Number(prop(d,'count',0));if(sub==='Step1_VeVachDocTuyen'){setText('sumLongitudinal',count);setStatus('stepStatus1','Đã sinh','success');}if(sub==='Step2_VeDaGiacNut'||sub==='Step2_CapNhatDaGiacNut'){const pc=Number(prop(d,'polygonCount',count));arm.pipelineStatus=arm.pipelineStatus||{};arm.pipelineStatus.polygonCount=pc;setText('polygonCreated',pc);setText('sumIntersections',pc);setStatus('stepStatus2','Có polygon','success');if(sub==='Step2_VeDaGiacNut'&&count>0)toast(`Đã tự tạo ${count} polygon nút giao. Có thể grip-edit rồi bấm CẬP NHẬT ĐA GIÁC nếu cần.`, 'success');}if(sub==='Step2_CatVachTrongDaGiac'){setText('polygonTrimmed',count);setStatus('stepStatus2','Đã xén','success');}if(sub==='Step3_VeVachMep'){setText('sumEdgeMarkings',count);setStatus('stepStatus3','Đã sinh','success');}if(sub==='Step4_VeVachDungVaDiBo'){setText('sumStopCrosswalk',count);setStatus('stepStatus4',count>0?'Đã sinh':'Chưa sinh',count>0?'success':'wait');const detail=prop(d,'detail',null),rejected=arr(detail?prop(detail,'Rejected',prop(detail,'rejected',[])):[]);if(rejected.length){const reason=String(prop(rejected[0],'Reason',prop(rejected[0],'reason','Không đủ điều kiện hình học.')));toast(`${rejected.length} approach 7.1/7.3 bị bỏ qua · ${reason}`,'warning');}const dist=Number(prop(d,'stopToCrosswalkDistance',NaN));if(Number.isFinite(dist)&&dist>0)setValue('t3_stopToPedDistance',dist);}window.setQuantitySnapshotState?.({dirty:true});}

// -----------------------------------------------------------------------------
// TAB 4 · Blocks 7.6 / 9.3
// -----------------------------------------------------------------------------
arm.symbolProfiles=[];
window.refreshSymbolPlacementWorkspace=()=>post('ReadSymbolPlacementWorkspace',{});
window.requestTab4AssembliesFromHost=window.refreshSymbolPlacementWorkspace;
window.selectSymbolLibraryFolder=()=>{markCadPick('CHỌN THƯ MỤC BLOCK','Hộp thoại chọn thư viện DWG sẽ mở');post('SelectSymbolLibraryFolder',{});};
window.scanSymbolBlockLibrary=()=>post('ScanSymbolBlockLibrary',{libraryPath:txt('b4LibraryPath')});
window.refreshSymbolBlockLibrary=window.scanSymbolBlockLibrary;
function renderBlockLibrary(d){const blocks=arr(ci(d,'blocks'));setValue('b4LibraryPath',prop(d,'path',prop(d,'folderPath','')));setText('b4LibraryStatus',`${blocks.length} Block DWG đã nhận dạng`);setText('b4BlockReady',blocks.length);setStatus('b4CadStatus','Đã đọc thư viện',blocks.length?'ready':'running');}
function renderSymbolWorkspace(d){arm.symbolProfiles=arr(ci(d,'profiles'));arm.crossSections=arr(ci(d,'crossSections')).length?arr(ci(d,'crossSections')):arm.crossSections;{const activeIds=arr(ci(d,'activeCrossSectionIds'));if(activeIds.length||ci(d,'activeCrossSectionIds')!==undefined)arm.activeCrossSectionIds=new Set(activeIds.map(String));}arm.proposals=arr(ci(d,'Proposals',ci(d,'proposals')));const blocks=arr(ci(d,'Blocks',ci(d,'blocks'))),nodes=arr(ci(d,'Nodes',ci(d,'nodes'))),axes=arr(ci(d,'roadAxes'));if(axes.length){arm.roadCatalog=axes;syncRoadsEverywhere?.();}const approaches=nodes.flatMap(n=>arr(ci(n,'Approaches')));setValue('b4LibraryPath',prop(d,'LibraryPath',prop(d,'libraryPath','')));setText('b4NodeCount',nodes.length);setText('b4ApproachCount',approaches.length);setText('b4SourceAxisCount',axes.length||arm.roadCatalog.length);setText('b4Source71Count',nodes.filter(n=>ci(n,'Station71')!==null&&ci(n,'Station71')!==undefined).length);setText('b4Source73Count',nodes.filter(n=>ci(n,'Station73')!==null&&ci(n,'Station73')!==undefined).length);setText('b4AnchorCount',nodes.filter(n=>(ci(n,'Station71')!==null&&ci(n,'Station71')!==undefined)||(ci(n,'Station73')!==null&&ci(n,'Station73')!==undefined)).length);setText('b4LaneCount',approaches.reduce((sum,a)=>sum+arr(ci(a,'Lanes')).length,0));setText('b4OutboundLaneCount',approaches.filter(a=>String(prop(a,'InboundDirection')).toUpperCase()==='REVERSE').reduce((sum,a)=>sum+arr(ci(a,'Lanes')).length,0));setText('b4BlockReady',blocks.length);applyBlockRulesFromHost(ci(d,'rules'));window.setTab4AssembliesFromTab2?.(decorateSectionsForTab4());applySavedSymbolProfile(txt('b4AssemblyProfileSelect'));renderProposals();setStatus('b4CadStatus','Dữ liệu mới','ready');}
function applyBlockRulesFromHost(r){if(!r)return;const map={b4Distance76:'distance76',b4Anchor93:'anchor93',b4FirstDistance93:'firstDistance93',b4ClusterCount93:'clusterCount93',b4ClusterSpacing93:'clusterSpacing93',b4OutDistance93:'outDistance93'};for(const [id,key] of Object.entries(map)){const v=ci(r,key);if(v!==undefined)setValue(id,v);}if(ci(r,'inboundOnly76')!==undefined&&$('b4InboundOnly76'))$('b4InboundOnly76').checked=!!ci(r,'inboundOnly76');if(ci(r,'rotateWithTraffic')!==undefined&&$('b4Rotate93'))$('b4Rotate93').checked=!!ci(r,'rotateWithTraffic');}
function blockRules(){return {distance76:num('b4Distance76',30),inboundOnly76:checked('b4InboundOnly76'),anchor93:txt('b4Anchor93','71'),firstDistance93:num('b4FirstDistance93',20),clusterCount93:Math.max(1,num('b4ClusterCount93',3)),clusterSpacing93:num('b4ClusterSpacing93',25),outDistance93:num('b4OutDistance93',15),rotateWithTraffic:checked('b4Rotate93')};}
window.updateSymbolRules=()=>{const dirty=$('b4ProfileDirty');if(dirty){dirty.hidden=false;dirty.dataset.dirty='true';}};
window.changeSymbolNode=window.changeSymbolApproach=()=>renderProposals();
window.saveSymbolPlacementDefaults=()=>post('SaveSymbolPlacementDefaults',{rules:blockRules()});
window.resetSymbolPlacementRules=()=>{setValue('b4Distance76',30);setValue('b4Anchor93','71');setValue('b4FirstDistance93',20);setValue('b4ClusterCount93',3);setValue('b4ClusterSpacing93',25);setValue('b4OutDistance93',15);if($('b4InboundOnly76'))$('b4InboundOnly76').checked=true;if($('b4Rotate93'))$('b4Rotate93').checked=true;};
function canonicalLaneIdFromCard(c){const side=String(c.dataset.sourceSide||'').toLowerCase(),index=Number(c.dataset.laneIndex||0)+1;if(side==='left')return `L${index}`;if(side==='right')return `R${index}`;const raw=String(c.dataset.laneId||'');const m=raw.match(/(\d+)\s*$/);return `${c.dataset.direction==='out'?'R':'L'}${m?m[1]:index}`;}
function symbolTrafficRole(lane){const explicit=String(prop(lane,'TrafficRole','')).toUpperCase();if(explicit==='OUTBOUND'||explicit==='INBOUND')return explicit;return String(prop(lane,'Direction','')).toUpperCase()==='REVERSE'?'OUTBOUND':'INBOUND';}
function applySavedSymbolProfile(assemblyId){if(!assemblyId)return;const profile=arm.symbolProfiles.find(x=>String(prop(x,'AssemblyId')).toLowerCase()===String(assemblyId).toLowerCase());if(!profile)return;const lanes=arr(ci(profile,'Lanes'));for(const c of document.querySelectorAll('#tabKyHieuBlock .b4-lane-card')){const role=c.dataset.direction==='out'?'OUTBOUND':'INBOUND',laneId=canonicalLaneIdFromCard(c),idx=Number(c.dataset.laneIndex||0)+1,side=c.dataset.sourceSide||'';const lane=lanes.find(x=>symbolTrafficRole(x)===role&&String(prop(x,'LaneId')).toUpperCase()===laneId)||lanes.find(x=>symbolTrafficRole(x)===role&&Number(prop(x,'LaneIndex'))===idx&&String(prop(x,'Side')).toLowerCase()===String(side).toLowerCase());if(!lane)continue;const e76=c.querySelector('.b4-lane-76'),e93=c.querySelector('.b4-lane-93');if(e76)e76.value=prop(lane,'Enable76',true)?'1':'0';if(e93)e93.value=prop(lane,'Enable93',true)?String(prop(lane,'Movement','STRAIGHT')).toLowerCase():'none';}const dirty=$('b4ProfileDirty');if(dirty){dirty.hidden=true;delete dirty.dataset.dirty;}}
const armUiChangeTab4Profile=window.changeTab4AssemblyProfile;window.changeTab4AssemblyProfile=function(id){const r=armUiChangeTab4Profile?.(id);setTimeout(()=>applySavedSymbolProfile(id),0);return r;};
window.saveTab4AssemblyProfile=()=>{const assemblyId=txt('b4AssemblyProfileSelect');if(!assemblyId)return toast('Chọn MCN trước.','warning');const cards=[...document.querySelectorAll('#tabKyHieuBlock .b4-lane-card')];const lanes=cards.map((c,i)=>{const trafficRole=c.dataset.direction==='out'?'OUTBOUND':'INBOUND',laneId=canonicalLaneIdFromCard(c);return {laneId,side:c.dataset.sourceSide||'',laneIndex:Number(c.dataset.laneIndex||i)+1,enable76:c.querySelector('.b4-lane-76')?.value==='1',enable93:(c.querySelector('.b4-lane-93')?.value||'none')!=='none',movement:(c.querySelector('.b4-lane-93')?.value||'STRAIGHT').replace(/^none$/,'STRAIGHT').toUpperCase(),trafficRole};});post('SaveSymbolProfile',{profile:{id:`SP_${assemblyId}`,assemblyId,name:`Profile ${assemblyId}`,lanes}});};
window.analyzeSymbolPlacement=()=>{const dirty=$('b4ProfileDirty');if(dirty?.dataset.dirty==='true')return toast('Cấu hình MCN đang thay đổi. Hãy LƯU PROFILE trước khi phân tích toàn bộ nút giao.','warning');if(!effectiveCrossSectionsUi().length)return toast('Chưa có MCN đã đồng bộ từ Tab 2.','warning');post('AnalyzeSymbolBlockPlacement',{analyzeAll:true,rules:blockRules()});};
function renderProposals(){const f=txt('b4ResultType','all'),st=txt('b4ResultStatus','all');const rows=arm.proposals.filter(p=>(f==='all'||String(prop(p,'Code'))===f)&&(st==='all'||String(prop(p,'Status')).toLowerCase()===st.toLowerCase()));const b=$('b4PlacementBody');if(b)b.innerHTML=rows.length?rows.map((p,i)=>`<tr><td>${i+1}</td><td>${esc(prop(p,'Road'))}</td><td>${esc(prop(p,'Approach'))}</td><td>${esc(prop(p,'Approach'))}</td><td>${esc(prop(p,'Direction'))}</td><td>${esc(prop(p,'Lane'))}</td><td>${esc(prop(p,'Code'))}</td><td>${esc(prop(p,'Movement'))}</td><td>${esc(prop(p,'Block'))}</td><td>${esc(prop(p,'Code')==='7.6'?'7.3':txt('b4Anchor93','7.1'))}</td><td>${esc(prop(p,'Cluster'))}</td><td>${esc(prop(p,'Station'))}</td><td>${esc(prop(p,'Status'))}</td><td><button class="btn-outline" onclick="window.armZoomProposal('${esc(prop(p,'Id'))}')">—</button></td><td><input type="checkbox" ${prop(p,'Selected',true)?'checked':''} onchange="window.armSelectProposal('${esc(prop(p,'Id'))}',this.checked)"></td></tr>`).join(''):'<tr><td colspan="15" class="b4-empty-cell">Chưa có đề xuất.</td></tr>';setText('b4ProposalCount',arm.proposals.length);setText('b4SelectedCount',arm.proposals.filter(p=>prop(p,'Selected',true)).length);setText('b4Count76',arm.proposals.filter(p=>prop(p,'Code')==='7.6').length);setText('b4Count93',arm.proposals.filter(p=>prop(p,'Code')==='9.3').length);setText('b4ProposalSummary',arm.proposals.length);if($('b4GenerateBtn'))$('b4GenerateBtn').disabled=!arm.proposals.some(p=>prop(p,'Selected',true));}
window.renderSymbolProposals=renderProposals;window.filterSymbolProposals=renderProposals;
window.armSelectProposal=(id,on)=>{const p=arm.proposals.find(x=>String(prop(x,'Id'))===String(id));if(p)p.Selected=on;renderProposals();};
window.selectAllSymbolProposals=on=>{arm.proposals.forEach(p=>p.Selected=on);renderProposals();};
window.generateSymbolBlocks=()=>{const selected=arm.proposals.filter(p=>prop(p,'Selected',true));if(!selected.length)return toast('Chưa chọn đề xuất.','warning');post('GenerateSymbolBlocksCAD',{blockReferences:selected});};

// -----------------------------------------------------------------------------
// TAB 5 · Supplementary markings / existing blocks
// -----------------------------------------------------------------------------
function syncRoadsEverywhere(){
  arm.supplementary.roads=arm.roadCatalog;
  window.setTab5Roads?.(arm.roadCatalog.map(r=>({
    id:roadKeyOf(r),
    key:roadKeyOf(r),
    axisKey:roadKeyOf(r),
    roadKey:roadKeyOf(r),
    roadName:roadNameOf(r),
    name:roadNameOf(r),
    handle:prop(r,'Handle'),
    length:prop(r,'Length'),
    startStation:prop(r,'StartStation',0),
    endStation:prop(r,'EndStation',prop(r,'Length',0)),
    axisType:prop(r,'AxisType',prop(r,'EntityType','')),
    identitySource:prop(r,'IdentitySource',''),
    nativeNamed:!!prop(r,'IsNativeNamed',false),
    identity:roadKeyOf(r),
    axis:prop(r,'Handle')
  })));
}
function syncTemplatesEverywhere(){arm.supplementary.templates=arm.templates;const rows=arm.templates.map(toUiLayerRow);syncTemplateSelects();window.armBroadcastSharedLayerSet?.(rows,{source:'host'});window.setTab5LayerTemplates?.(rows.map(t=>({...t,id:t.TemplateId,name:t.Layer,layerName:t.Layer,code:t.Code,markingCode:t.Code,width:t.Width,paintRatio:Number(prop(t,'PaintRatio',1))||1,source:'Shared Layer Library'})));}
function receiveSupplementaryWorkspace(d){arm.roadCatalog=arr(ci(d,'roads'));arm.supplementary.roads=arm.roadCatalog;arm.supplementary.templates=arr(ci(d,'templates'));arm.supplementary.groups=arr(ci(d,'groups'));arm.supplementary.blockRules=arr(ci(d,'blockRules'));if(arm.supplementary.templates.length){arm.templates=arm.supplementary.templates;renderTemplates();}syncRoadsEverywhere();syncTemplatesEverywhere();window.setTab5ManagedGroups?.(arm.supplementary.groups);window.setTab5BlockRules?.(arm.supplementary.blockRules);setStatus('s5CadStatus','Đã đọc CAD','ready');}
function currentRoad(){const key=txt('s5RoadSelect');return arm.roadCatalog.find(r=>roadKeyOf(r)===key)||{};}
function currentTemplate5(){const id=txt('s5LayerTemplate');return arm.templates.find(t=>idOf(t)===id)||{};}
function receiveSupplementaryBoundary(d){const h=prop(d,'handle','');if(arm.pendingPick==='boundary1'){arm.supplementary.boundary1=h;window.acknowledgeArmCadPick?.('s5Boundary1',h);}else{arm.supplementary.boundary2=h;window.acknowledgeArmCadPick?.('s5Boundary2',h);}arm.pendingPick='';}
function receiveSupplementaryStation(d){
  const s=Number(prop(d,'station',0));
  if(arm.pendingPick==='start'){
    arm.supplementary.startStation=s;setValue('s5StartStation',s.toFixed(2));window.acknowledgeArmCadPick?.('s5StartStation',s.toFixed(2));
  }else if(arm.pendingPick==='end'){
    arm.supplementary.endStation=s;setValue('s5EndStation',s.toFixed(2));window.acknowledgeArmCadPick?.('s5EndStation',s.toFixed(2));
  }else if(arm.pendingPick==='direction'){
    const anchor=num('s5ClusterAnchor',arm.supplementary.anchorStation||0), sign=s>=anchor?1:-1;
    setValue('s5ClusterDirectionSign',sign);setText('s5ClusterDirectionPoint',`${s.toFixed(2)} · ${sign>0?'tăng':'giảm'} lý trình`);
    window.acknowledgeArmCadPick?.('s5ClusterDirectionPoint',s.toFixed(2));
  }else{
    arm.supplementary.anchorStation=s;setValue('s5ClusterAnchor',s.toFixed(2));window.acknowledgeArmCadPick?.('s5ClusterAnchor',s.toFixed(2));
  }
  arm.pendingPick='';window.invalidateTab5Preview?.('Lý trình vừa thay đổi');
}
function receiveSupplementaryEntities(d,blocks){
  const items=arr(ci(d,'items')), handles=items.map(x=>prop(x,'handle')).filter(Boolean);
  if(blocks){arm.supplementary.selectedHandles=handles;window.setTab5SelectedExistingBlocks?.(items);}
  else{arm.supplementary.manualHandles=handles;window.setTab5ManualSelection?.(items);setText('s5SessionStatus',`${handles.length} đối tượng đã chọn`);}
}
function receiveManualPolyline(d){const h=prop(d,'handle');if(h&&!arm.supplementary.manualHandles.includes(h))arm.supplementary.manualHandles.push(h);setText('s5SessionStatus',`${arm.supplementary.manualHandles.length} đối tượng trong Group`);}
function speedPayload(){
  const r=currentRoad(),t=currentTemplate5(),whole=document.querySelector('#tabPhatSinh [data-s5-scope="whole"].active');
  const roadLength=Number(prop(r,'Length',0)),axisStart=Number(prop(r,'StartStation',0)),axisEnd=Number(prop(r,'EndStation',axisStart+roadLength));
  const start=whole?axisStart:num('s5StartStation',arm.supplementary.startStation||axisStart),end=whole?axisEnd:num('s5EndStation',arm.supplementary.endStation||axisEnd);
  return {roadKey:roadKeyOf(r),roadName:roadNameOf(r),boundary1:arm.supplementary.boundary1||$('s5Boundary1')?.textContent||'',boundary2:arm.supplementary.boundary2||$('s5Boundary2')?.textContent||'',
    mode:document.querySelector('#tabPhatSinh [data-speed-mode].active')?.dataset.speedMode||'uniform',startStation:start,endStation:end,spacing:num('s5UniformSpacing',5),balanceRemainder:checked('s5BalanceRemainder'),
    anchorStation:num('s5ClusterAnchor',arm.supplementary.anchorStation),clusterDirectionSign:Number(txt('s5ClusterDirectionSign','1'))>=0?1:-1,
    clusterCount:num('s5ClusterCount',1),clusterOffset:num('s5ClusterOffset',0),clusterSpacing:num('s5ClusterSpacing',10),barsPerCluster:num('s5BarsPerCluster',3),barSpacing:num('s5BarSpacing',0.5),
    stripWidth:Number(prop(t,'Width',0.4))||0.4,markingCode:prop(t,'Code','GGT'),templateId:idOf(t),targetLayer:prop(t,'Layer',''),paintRatio:Number(prop(t,'PaintRatio',1))||1,
    groupId:$('s5PreviewGroup')?.textContent?.includes('Tạo mới')?'':$('s5PreviewGroup')?.textContent};
}
function handleTab5Action(a){
  const r=currentRoad(),t=currentTemplate5(),roadKey=roadKeyOf(r),roadName=roadNameOf(r),gid=tab5GroupIdFromEvent();
  switch(a){
    case 'Tab5_RefreshContext': return post('ReadSupplementaryWorkspace',{});
    case 'Tab5_ZoomRoad': return roadKey?post('ZoomRoadAxisByKey',{roadKey}):toast('Chưa chọn tuyến.','warning');
    case 'Tab5_PickBoundary1': arm.pendingPick='boundary1';markCadPick('CHỌN BIÊN 1','Chọn đường biên thứ nhất');return post('SelectSupplementaryBoundary',{});
    case 'Tab5_PickBoundary2': arm.pendingPick='boundary2';markCadPick('CHỌN BIÊN 2','Chọn đường biên thứ hai');return post('SelectSupplementaryBoundary',{});
    case 'Tab5_PickStart': arm.pendingPick='start';markCadPick('CHỌN LÝ TRÌNH ĐẦU','Pick điểm trên tuyến');return post('SelectSupplementaryStation',{roadKey,prompt:'Chọn lý trình đầu:'});
    case 'Tab5_PickEnd': arm.pendingPick='end';markCadPick('CHỌN LÝ TRÌNH CUỐI','Pick điểm trên tuyến');return post('SelectSupplementaryStation',{roadKey,prompt:'Chọn lý trình cuối:'});
    case 'Tab5_PickClusterAnchor': arm.pendingPick='anchor';markCadPick('CHỌN MỐC CỤM','Pick mốc bố trí cụm');return post('SelectSupplementaryStation',{roadKey,prompt:'Chọn mốc cụm:'});
    case 'Tab5_PickClusterDirection': arm.pendingPick='direction';markCadPick('CHỈ PHÍA BỐ TRÍ','Pick một điểm về phía cần rải cụm');return post('SelectSupplementaryStation',{roadKey,prompt:'Chỉ một điểm về phía cần bố trí:'});
    case 'Tab5_Preview': return post('PreviewSpeedHump',speedPayload());
    case 'Tab5_GenerateOrUpdate': return post('GenerateSpeedHump',speedPayload());
    case 'Tab5_DrawManualMarking': markCadPick('VẼ VẠCH PHÁT SINH','Chọn >=2 điểm, Enter để kết thúc');return post('DrawSupplementaryManualPolyline',{targetLayer:prop(t,'Layer','')});
    case 'Tab5_AdoptExistingMarking': markCadPick('CHỌN VẠCH CÓ SẴN','Quét hình học cần tiếp nhận');return post('SelectSupplementaryEntities',{});
    case 'Tab5_AssignLayerToSelected':
    case 'Tab5_FinishManualMarkingGroup': {
      if(!arm.supplementary.manualHandles.length)return toast('Chưa chọn đối tượng CAD cần đưa vào quản lý.','warning');
      return post('RegisterSupplementaryEntities',{handles:arm.supplementary.manualHandles,roadKey,roadName,type:'manual_marking',markingCode:txt('s5ManualMarkingCode',prop(t,'Code','SUPPLEMENTARY')),templateId:idOf(t),width:prop(t,'Width',0),paintRatio:prop(t,'PaintRatio',1),groupId:txt('s5ManualGroupId')});
    }
    case 'Tab5_SelectExistingBlocks': markCadPick('CHỌN BLOCK CÓ SẴN','Quét BlockReference cần quản lý');return post('SelectSupplementaryBlocks',{});
    case 'Tab5_ScanUnmanagedBlocks': return post('ScanSupplementaryBlocks',{scopeLayer:txt('s5BlockScanScope')==='layer'?txt('s5BlockRuleLayer'):''});
    case 'Tab5_CheckSelectedBlocks': return arm.supplementary.selectedHandles.length?toast(`${arm.supplementary.selectedHandles.length} Block đã chọn; kiểm tra Rule rồi đồng bộ.`,'info'):toast('Chưa chọn Block hiện hữu.','warning');
    case 'Tab5_SaveBlockManagementRule': return saveBlockRule5();
    case 'Tab5_SyncExistingBlockProperties': return arm.supplementary.selectedHandles.length?post('SyncSupplementaryBlocks',{handles:arm.supplementary.selectedHandles,roadKey,roadName,groupId:''}):toast('Chưa chọn Block hiện hữu.','warning');
    case 'Tab5_RegisterQuickLayerTemplate': return saveQuickTemplate5();
    case 'Tab5_ZoomGroup': return gid&&post('ZoomManagedGroup',{groupId:gid});
    case 'Tab5_LockGroup': {const g=arm.supplementary.groups.find(x=>String(prop(x,'GroupId'))===gid);return gid&&post('SetManagedGroupLock',{groupId:gid,locked:!prop(g,'IsLocked',false)});}
    case 'Tab5_UnmanageExternalBlock': return gid&&confirm(`Bỏ quản lý ARM cho ${gid}?`)&&post('RemoveManagedGroup',{groupId:gid});
    case 'Tab5_EditBlockMapping': {const g=arm.supplementary.groups.find(x=>String(prop(x,'GroupId'))===gid);if(g){setValue('s5BlockRuleCode',prop(g,'MarkingCode'));setValue('s5BlockRuleLayer',prop(g,'Layer'));toast(`Đã nạp Group ${gid}; chỉnh Rule rồi đồng bộ lại.`,'info');}return true;}
    case 'Tab5_EditGroup': {const g=arm.supplementary.groups.find(x=>String(prop(x,'GroupId'))===gid);if(g){setText('s5PreviewGroup',gid);setValue('s5ManualGroupId',gid);toast(`Đã nạp Group ${gid} để cập nhật.`,'info');}return true;}
    default:return false;
  }
}
function saveBlockRule5(){const blockName=txt('s5BlockRuleTarget')||txt('s5BlockRuleSelect');const selectedTemplateId=txt('s5BlockRuleLayer')||txt('s5LayerTemplate');const t=arm.templates.find(x=>idOf(x)===selectedTemplateId)||currentTemplate5();if(!blockName)return toast('Chọn BlockName để lưu rule.','warning');post('SaveSupplementaryBlockRule',{rule:{id:`BR_${normalizeKey(blockName)}`,name:blockName,blockName,markingCode:txt('s5BlockRuleCode',prop(t,'Code','BLOCK')),layerTemplateId:idOf(t),targetLayer:prop(t,'Layer',''),enabled:true}});}
function saveQuickTemplate5(){const code=txt('s5QuickLayerCode');if(!code)return toast('Nhập mã vạch nhanh.','warning');post('SaveMarkingTemplate',{template:{id:'',code,name:code,description:txt('s5QuickLayerDescription'),layer:`_1.VS_MARKING.${code}`,pattern:'CUSTOM_REAL',customDash1:1,customGap1:0,width:num('s5QuickLayerWidth'),color:txt('s5QuickLayerColor','#ffffff'),linetypeScale:num('s5QuickLayerScale',1),reference:txt('s5QuickLayerStandardRef'),verified:true}});}
function receiveScannedBlocks(d){const items=arr(ci(d,'items'));window.setTab5SelectedExistingBlocks?.(items);setText('s5BlockUnmanagedCount',items.filter(x=>!prop(x,'isManaged',false)).length);setText('s5BlockManagedCount',items.filter(x=>prop(x,'isManaged',false)).length);}
window.editTab5Group=id=>toast(`Group ${id}: sửa bằng cách nạp lại cấu hình rồi Generate/Update.`, 'info');
window.zoomTab5Group=id=>post('ZoomManagedGroup',{groupId:id});
window.lockTab5Group=(id,locked=true)=>post('SetManagedGroupLock',{groupId:id,locked});
window.unmanageTab5Group=id=>{if(confirm('Gỡ ARM metadata khỏi Group này?'))post('RemoveManagedGroup',{groupId:id});};

// -----------------------------------------------------------------------------
// TAB 6 · Quantity
// -----------------------------------------------------------------------------
window.refreshQuantityFromCad=()=>post('ReadMarkingQuantitiesCAD',{});
function receiveQuantities(d){
  arm.quantityRows=arr(ci(d,'rows'));
  setText('q6UnmanagedCount',prop(d,'unmanagedCount',0));
  window.setQuantitySnapshotState?.({dirty:!!prop(d,'dirty',false),timestamp:formatTimestamp(prop(d,'timestampUtc',''))});
  renderQuantityOwners();
  renderQuantities();
}
function quantityUnit(r){return String(prop(r,'QuantityUnit',Number(prop(r,'Count',0))>0?'Cái':'m²'));}
function quantityScope(r){return String(prop(r,'QuantityScope',prop(r,'OwnerType','ROAD'))).toUpperCase();}
function quantityScopeLabel(r){return String(prop(r,'ScopeLabel',quantityScope(r)==='INTERSECTION'?'NÚT GIAO':quantityScope(r)==='SUPPLEMENTARY'?'PHÁT SINH':'TUYẾN'));}
function quantityGroupLabel(value){
  const key=String(value||'').toUpperCase();
  return ({VACH_DOC_TUYEN:'Vạch dọc tuyến',VACH_MEP_NUT:'Vạch mép nút',VACH_DUNG:'Vạch dừng',VACH_DI_BO:'Vạch đi bộ',VACH_NUT_KHAC:'Vạch nút khác',PHAT_SINH:'Phát sinh'})[key]||value||'Chưa phân nhóm';
}
function quantityOwnerKey(r){return `${String(prop(r,'OwnerType','road')).toLowerCase()}|${String(prop(r,'OwnerId',''))}`;}
function quantityTypeLabel(type){const t=String(type||'').toLowerCase();return t.includes('intersection')?'NÚT GIAO':t.includes('supplementary')?'PHÁT SINH':'TUYẾN';}
function renderQuantityOwners(){
  const owners=new Map();
  for(const r of arm.quantityRows){
    const key=quantityOwnerKey(r),type=String(prop(r,'OwnerType','road')).toLowerCase();
    if(!owners.has(key))owners.set(key,{key,type,id:prop(r,'OwnerId',''),name:prop(r,'OwnerName',prop(r,'RoadName','')),count:0,groups:new Set(),roads:new Set(),area:0});
    const o=owners.get(key);o.count++;o.groups.add(String(prop(r,'QuantityGroup','')));o.roads.add(String(prop(r,'RoadName','')).trim());o.area+=Number(prop(r,'PaintedArea',0))||0;
  }
  const q=txt('q6OwnerSearch').toLowerCase();
  const items=[...owners.values()].filter(x=>(arm.quantityOwnerType==='all'||x.type.includes(arm.quantityOwnerType))&&(!q||`${x.name}|${x.id}|${[...x.groups].join('|')}`.toLowerCase().includes(q)));
  const h=$('q6OwnerList');
  if(h)h.innerHTML=items.length?items.map(o=>{const roads=[...o.roads].filter(Boolean);const name=o.type.includes('intersection')&&roads.length?`Nút ${roads.join(' × ')}`:(o.name||o.id);return `<button class="q6-owner-item" onclick="window.armQuantityOwner('${esc(o.key)}')"><strong>${esc(name)}</strong><span>${quantityTypeLabel(o.type)} · ${o.groups.size} nhóm · ${o.count} đối tượng</span></button>`;}).join(''):'<div class="q6-empty">Không có nhóm phù hợp.</div>';
  setText('q6OwnerCount',`${owners.size} nhóm`);
}
arm.quantityOwnerKey='';
window.armQuantityOwner=key=>{
  arm.quantityOwnerKey=key;
  const rows=arm.quantityRows.filter(r=>quantityOwnerKey(r)===key);
  const roadNames=[...new Set(rows.map(r=>String(prop(r,'RoadName','')).trim()).filter(Boolean))];
  const ownerTitle=rows.length&&String(prop(rows[0],'OwnerType','')).toLowerCase().includes('intersection')&&roadNames.length?`Nút ${roadNames.join(' × ')}`:(rows.length?prop(rows[0],'OwnerName',prop(rows[0],'RoadName','')):'Tất cả');
  setText('q6CurrentOwner',ownerTitle);
  const groups=[...new Set(rows.map(r=>quantityGroupLabel(prop(r,'QuantityGroup',''))).filter(Boolean))];
  setText('q6CurrentOwnerMeta',rows.length?`${quantityTypeLabel(prop(rows[0],'OwnerType','road'))} · ${groups.join(' · ')}`:'Chưa có dữ liệu');
  renderQuantities();
};
window.setQuantityOwnerType=(type,btn)=>{
  arm.quantityOwnerType=type;
  arm.quantityOwnerKey='';
  document.querySelectorAll('[data-owner-type],[data-q6-owner-type]').forEach(x=>x.classList.remove('active'));
  btn?.classList.add('active');
  setText('q6CurrentOwner','Tất cả tuyến và nút giao');
  setText('q6CurrentOwnerMeta','Phân khai theo nhóm nghiệp vụ');
  renderQuantityOwners();
  renderQuantities();
};
function filteredQuantityRows(){
  const q=txt('q6Search').toLowerCase(),mark=txt('q6MarkingFilter','all'),src=txt('q6SourceFilter','all');
  return arm.quantityRows.filter(r=>{
    const ownerType=String(prop(r,'OwnerType','')).toLowerCase();
    if(arm.quantityOwnerType!=='all'&&!ownerType.includes(arm.quantityOwnerType))return false;
    if(arm.quantityOwnerKey&&quantityOwnerKey(r)!==arm.quantityOwnerKey)return false;
    if(mark!=='all'&&String(prop(r,'MarkingCode'))!==mark)return false;
    if(src!=='all'&&!String(prop(r,'Source')).toLowerCase().includes(src.toLowerCase()))return false;
    if(q&&!JSON.stringify(r).toLowerCase().includes(q))return false;
    return true;
  }).sort((a,b)=>{
    const A=`${quantityScope(a)}|${prop(a,'OwnerId','')}|${prop(a,'QuantityGroup','')}|${prop(a,'MarkingCode','')}|${prop(a,'ObjectHandle','')}`;
    const B=`${quantityScope(b)}|${prop(b,'OwnerId','')}|${prop(b,'QuantityGroup','')}|${prop(b,'MarkingCode','')}|${prop(b,'ObjectHandle','')}`;
    return A.localeCompare(B,'vi',{numeric:true,sensitivity:'base'});
  });
}
function renderQuantities(){
  const rows=filteredQuantityRows(),b=$('q6QuantityBody');
  if(b)b.innerHTML=rows.length?rows.map((r,i)=>`<tr><td>${i+1}</td><td>${esc(prop(r,'OwnerName',prop(r,'RoadName','')))}</td><td>${esc(quantityScopeLabel(r))}</td><td>${esc(prop(r,'MarkingCode'))}</td><td title="${esc(prop(r,'QuantityCategory',''))}">${esc(quantityGroupLabel(prop(r,'QuantityGroup','')))}</td><td>${esc(prop(r,'Source'))}</td><td>${esc(prop(r,'TemplateLayer'))}</td><td>${esc(prop(r,'CadLayer'))}</td><td>${quantityUnit(r).toLowerCase().includes('cái')?Number(prop(r,'Count',0)):1}</td><td>${esc(quantityUnit(r))}</td><td>${fmt(prop(r,'PaintedLength',prop(r,'GeometryLength',0)))}</td><td>${fmt(prop(r,'PaintedArea',0))}</td><td><button class="btn-outline" onclick="window.armZoomQuantity('${esc(prop(r,'RecordId'))}','${esc(prop(r,'ObjectHandle'))}')">⌖</button></td></tr>`).join(''):'<tr class="q6-empty-row"><td colspan="13">Chưa có dữ liệu khối lượng.</td></tr>';
  const roads=new Set(arm.quantityRows.filter(r=>quantityScope(r)==='ROAD').map(r=>String(prop(r,'AxisKey',prop(r,'RoadName','')))).filter(Boolean));
  const nodes=new Set(arm.quantityRows.filter(r=>quantityScope(r)==='INTERSECTION').map(r=>String(prop(r,'OwnerId',''))).filter(Boolean));
  const blocks=arm.quantityRows.filter(r=>Number(prop(r,'Count',0))>0).reduce((sum,r)=>sum+Number(prop(r,'Count',0)),0);
  const area=rows.reduce((sum,r)=>sum+Number(prop(r,'PaintedArea',0)),0);
  setText('q6TotalObjects',arm.quantityRows.length);setText('q6RoadCount',roads.size);setText('q6NodeCount',nodes.size);setText('q6BlockCount',blocks);
  setText('q6PaintedLength',`${fmt(arm.quantityRows.reduce((sum,r)=>sum+Number(prop(r,'PaintedLength',0)),0))} m`);
  setText('q6PaintedArea',`${fmt(arm.quantityRows.reduce((sum,r)=>sum+Number(prop(r,'PaintedArea',0)),0))} m²`);
  setText('q6VisibleCount',`${rows.length} đối tượng`);setText('q6FilteredArea',`${fmt(area)} m²`);populateQuantityFilters();
}
window.filterQuantityTable=renderQuantities;window.filterQuantityOwners=renderQuantityOwners;
function populateQuantityFilters(){const m=$('q6MarkingFilter');if(m&&m.options.length<=1){const old=m.value;const codes=[...new Set(arm.quantityRows.map(r=>prop(r,'MarkingCode')).filter(Boolean))].sort();m.innerHTML='<option value="all">Tất cả mã</option>'+codes.map(x=>`<option value="${esc(x)}">${esc(x)}</option>`).join('');m.value=old||'all';}}
window.armZoomQuantity=(recordId,handle)=>post('ZoomMarkingQuantity',{recordId,handle});
window.exportQuantityExcel=scope=>{const rows=scope==='filtered'?filteredQuantityRows():arm.quantityRows;if(!rows.length)return toast('Không có dữ liệu để xuất.','warning');post('ExportMarkingQuantitiesExcel',{recordIds:rows.map(r=>prop(r,'RecordId')),fileName:`AutoRoadMarking_KhoiLuong_${new Date().toISOString().slice(0,10)}.xlsx`});};

// Finalized UI compatibility aliases.
window.cancelTab1LayerEdit=()=>resetTemplateForm(true);
window.scanCadAxisIdentities=()=>post('ReadCadAxisIdentitiesCAD',{});

// -----------------------------------------------------------------------------
// XỬ LÝ NÚT SỬA/NHÂN BẢN/XÓA TỪ GIAO DIỆN HTML TAB1.JS
// -----------------------------------------------------------------------------
window.armEditLayer=id=>{
  const t=arm.templates.find(x=>idOf(x)===id); if(!t)return;
  const layer=String(prop(t,'Layer')); const parts=layer.split('.');
  setValue('t1_LayerSuffix',parts.length>=3?parts.slice(2).join('.'):prop(t,'Code')); setValue('t1_MoTa',prop(t,'Description'));
  setValue('t1_KieuNet',prop(t,'Pattern')); setValue('t1_Width',prop(t,'Width')); setValue('t1_Scale',prop(t,'LinetypeScale',1));
  setValue('t1_ColorHex',prop(t,'Color')); setValue('t1_ColorRGB',prop(t,'Rgb'));
  setValue('t1_DashLength',prop(t,'DashLength')); setValue('t1_GapLength',prop(t,'GapLength'));
  setValue('t1_CustomDash1',prop(t,'CustomDash1')); setValue('t1_CustomGap1',prop(t,'CustomGap1'));
  setValue('t1_CustomDash2',prop(t,'CustomDash2')); setValue('t1_CustomGap2',prop(t,'CustomGap2')); setValue('t1_CustomPhase',prop(t,'CustomPhase'));
  setValue('t1_StandardRef',prop(t,'Reference')); setValue('t1_StandardClause',prop(t,'StandardClause'));
  const custom=prop(t,'CustomProperties',{}); setValue('t1_CustomProperties',JSON.stringify(custom));
  const host=$('t1PropertyRows'); if(host){host.innerHTML='';Object.entries(custom||{}).forEach(([k,v])=>window.addTab1CustomProperty?.(k,v));}
  if($('btnSubmitLayer')){$('btnSubmitLayer').dataset.editId=id;$('btnSubmitLayer').textContent='LƯU THAY ĐỔI LAYER';}
  if($('btnCancelLayerEdit'))$('btnCancelLayerEdit').hidden=false;
  window.toggleCustomLineTypeFields?.(); window.updateCustomRealCycle?.(); window.vePreviewLayerTab1?.();
};

window.armDemoEditLayer=index=>{
  const row=(window.armGetTab1LayerRows?.()||[])[index];
  const id=String(row?.TemplateId||row?.Id||'');
  if(id) window.armEditLayer(id);
};

window.armDemoDuplicateLayer=index=>{
  const row=(window.armGetTab1LayerRows?.()||[])[index]; if(!row)return false;
  const code=`${row.Code||'COPY'}_COPY`;
  return post('SaveMarkingTemplate',{template:{...row,id:'',Id:'',TemplateId:'',code,Code:code,name:code,layer:`${row.Layer||'_1.VS_MARKING'}.COPY`,Layer:`${row.Layer||'_1.VS_MARKING'}.COPY`,description:`${row.Description||''} (bản sao)`,managementState:'DIRTY'}});
};

window.armDemoDeleteLayer=index=>{
  const row=(window.armGetTab1LayerRows?.()||[])[index],id=String(row?.TemplateId||row?.Id||'');
  return id&&post('DeleteMarkingTemplates',{ids:[id]});
};

// -----------------------------------------------------------------------------
// Shared helpers / event hooks
// -----------------------------------------------------------------------------
function downloadText(name,text,type){const blob=new Blob([text],{type});const a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000);}
function csv(v){const s=String(v??'');return /[",\r\n]/.test(s)?`"${s.replace(/"/g,'""')}"`:s;}
function parseCsv(line){const out=[];let s='',q=false;for(let i=0;i<line.length;i++){const c=line[i];if(c==='"'){if(q&&line[i+1]==='"'){s+='"';i++;}else q=!q;}else if(c===','&&!q){out.push(s);s='';}else s+=c;}out.push(s);return out;}
function formatTimestamp(v){if(!v)return new Date().toLocaleString('vi-VN');const d=new Date(v);return Number.isNaN(d.getTime())?String(v):d.toLocaleString('vi-VN');}

$('t0RoadName')?.addEventListener('input',previewCadAxisName);
$('t0Search')?.addEventListener('input',renderRoadAxes);
$('t1_Search')?.addEventListener('input',renderTemplates);
$('t1_KieuNet')?.addEventListener('change',()=>{window.toggleCustomLineTypeFields();window.vePreviewLayerTab1();});
['t1_LayerSuffix','t1_Width','t1_Scale','t1_ColorHex','t1_DashLength','t1_GapLength','t1_CustomDash1','t1_CustomGap1','t1_CustomDash2','t1_CustomGap2'].forEach(id=>$(id)?.addEventListener('input',()=>window.vePreviewLayerTab1?.()));
$('csvFileInput')?.addEventListener('change',e=>window.nhapCSV(e.target));
$('jsonFileInput')?.addEventListener('change',e=>window.nhapJSON(e.target));
$('timKiemMatCat')?.addEventListener('input',renderCrossSections);
$('t3ResultSearch')?.addEventListener('input',renderComparison);$('t3ResultFilter')?.addEventListener('change',renderComparison);
$('b4ResultType')?.addEventListener('change',renderProposals);$('b4ResultStatus')?.addEventListener('change',renderProposals);
$('q6OwnerSearch')?.addEventListener('input',renderQuantityOwners);['q6Search','q6MarkingFilter','q6SourceFilter'].forEach(id=>$(id)?.addEventListener(id==='q6Search'?'input':'change',renderQuantities));

// Override preview-only stubs explicitly.
window.refreshQuantityFromCad=window.refreshQuantityFromCad;
window.renderDanhSachDaLuu=renderCrossSections;
window.toggleSelectAllLayers=window.toggleSelectAllLayers;
window.toggleSelectAllAssemblies=window.toggleSelectAllAssemblies;

// Local initial state before Ping response.
renderCurrentParts(); window.onLoaiChange?.(); window.toggleCustomLineTypeFields?.(); window.vePreviewLayerTab1?.();
const tab2PreviewHost=$('t2PreviewPanel');
if(tab2PreviewHost&&typeof ResizeObserver!=='undefined'){new ResizeObserver(()=>drawCrossSectionPreview()).observe(tab2PreviewHost);}
window.addEventListener('resize',drawCrossSectionPreview);