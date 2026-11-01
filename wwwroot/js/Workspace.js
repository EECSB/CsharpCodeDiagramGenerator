(function () {
    // A file dropped anywhere but on a file input (the drop zones in the Project panel) would make the
    // browser leave the app to open it. Only file drags are stopped, so text drags in the editor still work.
    function isFileDragOutsideInput(e) {
        const isFileInput = e.target instanceof HTMLInputElement && e.target.type === 'file';
        return !isFileInput && e.dataTransfer && Array.from(e.dataTransfer.types).includes('Files');
    }

    document.addEventListener('dragover', function (e) {
        if (isFileDragOutsideInput(e)) {
            e.preventDefault();
            e.dataTransfer.dropEffect = 'none';
        }
    });

    document.addEventListener('drop', function (e) {
        if (isFileDragOutsideInput(e)) {
            e.preventDefault();
        }
    });

    // Draggable splitter between two panes: dragging it sets the width of the pane before it.
    window.initSplitter = function (splitterId, paneId, minWidth) {
        const splitter = document.getElementById(splitterId);
        const pane = document.getElementById(paneId);
        if (!splitter || !pane || splitter.dataset.ready) return;
        splitter.dataset.ready = 'true';

        let startX = 0;
        let startWidth = 0;
        let maxWidth = 0;

        function onPointerMove(e) {
            const width = Math.max(minWidth, Math.min(maxWidth, startWidth + e.clientX - startX));
            pane.style.flexBasis = width + 'px';
        }

        function onPointerUp(e) {
            splitter.releasePointerCapture(e.pointerId);
            splitter.classList.remove('dragging');
            document.body.style.userSelect = '';
            splitter.removeEventListener('pointermove', onPointerMove);
            splitter.removeEventListener('pointerup', onPointerUp);
        }

        splitter.addEventListener('pointerdown', function (e) {
            startX = e.clientX;
            startWidth = pane.getBoundingClientRect().width;
            // The pane grows into the one after the splitter (the diagram), leaving that at least 320 px.
            const next = splitter.nextElementSibling;
            maxWidth = startWidth + Math.max(0, (next ? next.getBoundingClientRect().width : 0) - 320);
            splitter.setPointerCapture(e.pointerId);
            splitter.classList.add('dragging');
            document.body.style.userSelect = 'none';
            splitter.addEventListener('pointermove', onPointerMove);
            splitter.addEventListener('pointerup', onPointerUp);
        });
    };
})();
