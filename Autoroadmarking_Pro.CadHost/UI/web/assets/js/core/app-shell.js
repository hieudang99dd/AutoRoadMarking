(function(){
    "use strict";
    const ARM = window.ARM = window.ARM || {};
    const byId = ARM.byId = ARM.byId || (id => document.getElementById(id));
    ARM.version = "FINAL_LOCKED_V2_MODULAR";

    window.openTab = function(event, tabId){
        document.querySelectorAll(".tab-content").forEach(panel=>{
            const active=panel.id===tabId;
            panel.classList.toggle("active",active);
            panel.setAttribute("aria-hidden",active?"false":"true");
        });
        const button=event?.currentTarget || document.querySelector(`.tab-btn[data-tab="${tabId}"]`);
        document.querySelectorAll(".tab-btn").forEach(tab=>{
            const active=tab===button || tab.dataset.tab===tabId;
            tab.classList.toggle("active",active);
            tab.setAttribute("aria-selected",active?"true":"false");
            tab.tabIndex=active?0:-1;
        });
        const tabs=[...document.querySelectorAll(".tab-btn[data-tab]")];
        const index=Math.max(0,tabs.findIndex(tab=>tab.dataset.tab===tabId));
        const crumb=byId("activeWorkflowBreadcrumb");
        if(crumb && button) crumb.textContent=`BƯỚC ${index+1}/${tabs.length} · ${(button.textContent||"").trim()}`;
    };

    const primaryTablist=document.querySelector(".tab-header[role='tablist']");
    primaryTablist?.addEventListener("keydown",event=>{
        const tabs=[...primaryTablist.querySelectorAll('[role="tab"]')];
        const current=Math.max(0,tabs.indexOf(document.activeElement));
        let next=null;
        if(event.key==="ArrowRight") next=(current+1)%tabs.length;
        if(event.key==="ArrowLeft") next=(current-1+tabs.length)%tabs.length;
        if(event.key==="Home") next=0;
        if(event.key==="End") next=tabs.length-1;
        if(next!==null){event.preventDefault();tabs[next]?.focus();tabs[next]?.click();}
    });


    window.showArmToast = function(message,type="info",timeout=3600){
        const host=byId("toast-container"); if(!host) return;
        const el=document.createElement("div"); el.className=`arm-toast ${type}`; el.textContent=message; host.appendChild(el);
        setTimeout(()=>el.remove(),timeout);
    };
    const previewOnly = function(){ window.showArmToast("Chức năng này chưa được host C#/WebView2 kết nối.","warning"); return false; };



})();
