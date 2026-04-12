import { instance } from '@viz-js/viz';

// Viz.js 3.x: instance is created asynchronously
let vizInstancePromise = instance();

window.renderDotToSvg = async function (dot) {
	try {
		const viz = await vizInstancePromise;
		const svg = viz.renderString(dot, { format: "svg", engine: "dot" });
		return svg;
	} catch (e) {
		// Re-create the instance after an error (Viz instance is single-use on failure)
		vizInstancePromise = instance();
		if (e.message && e.message.includes('memory')) {
			return `<text x="10" y="20" fill="red">Error: Graph is too large to render. Try reducing the number of selected files or disabling some diagram options.</text>`;
		}
		return `<text x="10" y="20" fill="red">Error rendering graph: ${e.message}</text>`;
	}
}