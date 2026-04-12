(function () {
    let viewport = null;
    let content = null;
    let scale = 1;
    let translateX = 0;
    let translateY = 0;
    let isDragging = false;
    let startX = 0;
    let startY = 0;

    const MIN_SCALE = 0.1;
    const MAX_SCALE = 50;
    const ZOOM_SENSITIVITY = 0.002;
    const DEFAULT_SCALE = 4;

    function applyTransform() {
        if (content) {
            content.style.transform = `translate(${translateX}px, ${translateY}px) scale(${scale})`;
        }
    }

    function onMouseDown(e) {
        if (e.button !== 0) return; // left click only
        isDragging = true;
        startX = e.clientX - translateX;
        startY = e.clientY - translateY;
        viewport.style.cursor = 'grabbing';
        e.preventDefault();
    }

    function onMouseMove(e) {
        if (!isDragging) return;
        translateX = e.clientX - startX;
        translateY = e.clientY - startY;
        applyTransform();
    }

    function onMouseUp() {
        isDragging = false;
        if (viewport) {
            viewport.style.cursor = 'grab';
        }
    }

    function onWheel(e) {
        e.preventDefault();

        const rect = viewport.getBoundingClientRect();
        const mouseX = e.clientX - rect.left;
        const mouseY = e.clientY - rect.top;

        const delta = -e.deltaY * ZOOM_SENSITIVITY;
        const newScale = Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale * (1 + delta)));

        const scaleChange = newScale / scale;
        translateX = mouseX - scaleChange * (mouseX - translateX);
        translateY = mouseY - scaleChange * (mouseY - translateY);
        scale = newScale;

        applyTransform();
    }

    function normalizeSvg() {
        if (!content) return;

        const svg = content.querySelector('svg');
        if (!svg) return;

        if (!svg.getAttribute('viewBox')) {
            const w = svg.getAttribute('width');
            const h = svg.getAttribute('height');
            if (w && h) {
                const numW = parseFloat(w);
                const numH = parseFloat(h);
                if (numW && numH) {
                    svg.setAttribute('viewBox', `0 0 ${numW} ${numH}`);
                }
            }
        }

        svg.removeAttribute('width');
        svg.removeAttribute('height');

        svg.style.width = '100%';
        svg.style.height = '100%';
        svg.style.display = 'block';
    }

    window.initDiagramPanZoom = function () {
        viewport = document.getElementById('diagramViewport');
        content = document.getElementById('diagramContent');

        if (!viewport || !content) return;

        normalizeSvg();

        // Start zoomed in at DEFAULT_SCALE
        scale = DEFAULT_SCALE;
        translateX = 0;
        translateY = 0;
        applyTransform();

        viewport.removeEventListener('mousedown', onMouseDown);
        viewport.removeEventListener('mousemove', onMouseMove);
        viewport.removeEventListener('mouseup', onMouseUp);
        viewport.removeEventListener('mouseleave', onMouseUp);
        viewport.removeEventListener('wheel', onWheel);

        viewport.addEventListener('mousedown', onMouseDown);
        viewport.addEventListener('mousemove', onMouseMove);
        viewport.addEventListener('mouseup', onMouseUp);
        viewport.addEventListener('mouseleave', onMouseUp);
        viewport.addEventListener('wheel', onWheel, { passive: false });
    };

    window.resetDiagramView = function () {
        scale = DEFAULT_SCALE;
        translateX = 0;
        translateY = 0;
        applyTransform();
    };
})();