(function(ARM){
    "use strict";
    const byId = ARM.byId;
    window.setTab3CompactStep = function(step){
        const value = Math.max(1, Math.min(5, Number(step) || 1));
    
    const dashboard = document.querySelector("#tabVach2D .t3-dashboard");

        if(dashboard){
            dashboard.dataset.compactStep = String(value);
        }

        document.querySelectorAll("#t3CompactStepbar [data-step-index]")
            .forEach(button => {
                button.classList.toggle(
                    "active",
                    Number(button.dataset.stepIndex) === value
                );
            });
    };


})(window.ARM);
