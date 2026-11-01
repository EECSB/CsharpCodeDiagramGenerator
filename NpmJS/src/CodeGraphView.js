import cytoscape from 'cytoscape';
import fcose from 'cytoscape-fcose';
import dagre from 'cytoscape-dagre';

cytoscape.use(fcose);
cytoscape.use(dagre);

// The interactive diagram. The C# side (Layout/Main.razor) analyzes the code and passes the result
// in as plain data (Diagram/GraphData.cs); everything about showing it lives here.
//
// The Graphviz layout is drawn by Graphviz instead (see GRAPHVIZ below). Cytoscape still holds the
// elements, the filters, the selection and the focus; Graphviz draws what Cytoscape would show.

const FONT_FAMILY = 'Consolas, "Cascadia Mono", Menlo, monospace';
const FONT_SIZE = 12;
const LINE_HEIGHT = 1.3;
const MAX_MEMBER_LINES = 25;
// A selected node shows its edges' labels only if there are few enough to stay readable; otherwise hover shows them.
const MAX_LABELED_EDGES = 8;
const GLOBAL_NAMESPACE = '(global namespace)';
const EXTERNAL_GROUP = 'Framework & libraries';
const MIN_ZOOM = 0.03;
const MAX_ZOOM = 4;

let cy = null;
let container = null;
let dotNet = null;
let resizeObserver = null;
let graph = { nodes: [], edges: [] };
let savedPositions = null;
let fitWhenShown = false;
let centerWhenShown = null; // an element selected while the pane was hidden, centred once it's shown
let viewSize = null; // the diagram's size when it was last visible
let measureContext = null;

const state = {
	view: 'types',            // 'types' | 'namespaces'
	layout: 'fcose',          // 'fcose' (clustered) | 'dagre' (layered) | 'graphviz'
	groupBy: 'namespace',     // 'namespace' | 'file' | 'none': the boxes drawn around the types
	showMembers: false,
	showFunctions: false,     // a node per function inside its type's box, and calls from function to function
	hiddenEdgeKinds: new Set(),
	hiddenNodeKinds: new Set()
};

// ---------- COLORS ----------
// Defined once as CSS variables in wwwroot/css/app.css, so the page's legend and the diagram always match.
function cssVar(name, fallback) {
	const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
	return value || fallback;
}

function kindColors(kind) {
	const key = kind === 'recordStruct' ? 'record' : kind;
	return {
		border: cssVar(`--kind-${key}`, '#c4560c'),
		fill: cssVar(`--kind-${key}-fill`, '#fdf0e6')
	};
}

function edgeColor(kind) {
	return cssVar(`--edge-${kind}`, '#8c8c8c');
}

// ---------- LABELS & SIZES ----------
function measure(text, bold) {
	measureContext ??= document.createElement('canvas').getContext('2d');
	measureContext.font = `${bold ? 'bold ' : ''}${FONT_SIZE}px ${FONT_FAMILY}`;
	return measureContext.measureText(text).width;
}

function sizeFor(lines) {
	const width = Math.max(...lines.map((l, i) => measure(l, i === 0))) + 24;
	const height = lines.length * FONT_SIZE * LINE_HEIGHT + 16;
	return { w: Math.max(width, 60), h: Math.max(height, 30) };
}

function typeLabel(node) {
	const lines = [];
	if (state.showMembers && node.stereotype) lines.push(node.stereotype);
	lines.push(node.name);

	if (state.showMembers && node.members.length > 0) {
		const members = node.members.slice(0, MAX_MEMBER_LINES);
		if (node.members.length > MAX_MEMBER_LINES) members.push(`… ${node.members.length - MAX_MEMBER_LINES} more`);
		const width = Math.max(...lines.concat(members).map(l => l.length));
		lines.push('─'.repeat(Math.min(width, 60)), ...members);
	}

	return { label: lines.join('\n'), ...sizeFor(lines) };
}

function namespaceKey(node) {
	if (node.kind === 'external') return EXTERNAL_GROUP;
	return node.namespace || GLOBAL_NAMESPACE;
}

// Files are labelled by their path below the folder all of them are in.
function fileLabels() {
	const files = [...new Set(graph.nodes.map(n => n.file).filter(Boolean))];
	const folders = files.map(f => f.split('/').slice(0, -1));
	let common = folders[0] ?? [];
	for (const folder of folders) {
		let i = 0;
		while (i < common.length && i < folder.length && common[i] === folder[i]) i++;
		common = common.slice(0, i);
	}
	const prefix = common.length ? common.join('/') + '/' : '';
	return new Map(files.map(f => [f, f.slice(prefix.length)]));
}

function isNodeHidden(node) {
	return state.hiddenNodeKinds.has(node.kind);
}

// ---------- ELEMENTS ----------
// The "Functions" option: the functions that call or are called (graph.calls, see Diagram/GraphData.cs), per type,
// and an edge per calling function and called function. A call is named after the function it reaches, so a
// callee is matched to the name it has as a caller: render() is View.render() when that is the only one.
function functionId(typeId, name) {
	return typeId + '\n' + name; // as OnGraphSelection in Layout/Main.razor reads it
}

function functionGraph() {
	const types = new Set(graph.nodes.map(n => n.id));
	const calls = (graph.calls ?? []).filter(c => types.has(c.source) && types.has(c.target));
	const names = new Map();
	const add = (type, name) => {
		const list = names.get(type) ?? names.set(type, []).get(type);
		if (!list.includes(name)) list.push(name);
	};
	for (const call of calls) add(call.source, call.caller);

	const resolve = (type, callee) => {
		const known = names.get(type) ?? [];
		if (known.includes(callee)) return callee;
		const qualified = known.filter(name => name.endsWith('.' + callee));
		return qualified.length === 1 ? qualified[0] : callee;
	};
	const links = new Map();
	for (const call of calls) {
		const callee = resolve(call.target, call.callee);
		add(call.target, callee);
		const source = functionId(call.source, call.caller);
		const target = functionId(call.target, callee);
		const key = source + '\u0000' + target;
		const link = links.get(key) ?? links.set(key, { source, target, count: 0 }).get(key);
		link.count++;
	}

	const edges = [...links.values()].map((link, i) => ({
		group: 'edges',
		data: { id: 'f' + i, source: link.source, target: link.target, kind: 'calls', count: link.count, label: link.count > 1 ? link.count + ' calls' : '', color: edgeColor('calls') },
		classes: 'calls function-call'
	}));
	for (const list of names.values()) list.sort((a, b) => a.localeCompare(b));
	return { names, edges };
}

function buildTypeElements() {
	const elements = [];
	const groups = new Set();
	const labels = state.groupBy === 'file' ? fileLabels() : null;
	const functions = state.showFunctions ? functionGraph() : null;

	for (const node of graph.nodes) {
		// A box around each namespace or file; both are drawn as the "namespace" class.
		let parent;
		if (state.groupBy === 'namespace' || (state.groupBy === 'file' && !node.file)) {
			const key = namespaceKey(node);
			parent = 'ns:' + key;
			if (!groups.has(parent)) {
				groups.add(parent);
				elements.push({ group: 'nodes', data: { id: parent, label: key, name: key, ns: key }, classes: 'namespace' });
			}
		} else if (state.groupBy === 'file') {
			parent = 'file:' + node.file;
			if (!groups.has(parent)) {
				groups.add(parent);
				const label = labels.get(node.file);
				elements.push({ group: 'nodes', data: { id: parent, label, name: label, file: node.file }, classes: 'namespace file' });
			}
		}

		const colors = kindColors(node.kind);
		// A type with functions to show is a box around them, labelled with its name only.
		const names = functions?.names.get(node.id);
		elements.push({
			group: 'nodes',
			data: {
				id: node.id, parent, name: node.name, kind: node.kind, ns: namespaceKey(node), stereotype: node.stereotype, members: node.members,
				...typeLabel(node), ...(names ? { label: node.name } : {}), ...colors
			},
			classes: `type ${node.kind}${node.stereotype === '«abstract»' ? ' abstract' : ''}${names ? ' holder' : ''}`
		});

		for (const name of names ?? []) {
			elements.push({
				group: 'nodes',
				data: { id: functionId(node.id, name), parent: node.id, name, kind: node.kind, ns: namespaceKey(node), label: name, ...sizeFor([name]), border: colors.border },
				classes: 'method'
			});
		}
	}

	if (functions) elements.push(...functions.edges);

	for (const edge of graph.edges) {
		// Calls between types are drawn function to function instead.
		if (functions && edge.kind === 'calls') continue;

		elements.push({
			group: 'edges',
			data: { id: edge.id, source: edge.source, target: edge.target, kind: edge.kind, count: edge.count, label: edge.label, color: edgeColor(edge.kind) },
			classes: edge.kind
		});
	}

	return elements;
}

// Overview: one node per namespace, one edge per pair of namespaces that have any (visible) relationship.
function buildNamespaceElements() {
	const namespaceOf = new Map();
	const typeCounts = new Map();

	for (const node of graph.nodes) {
		if (isNodeHidden(node)) continue;
		const key = namespaceKey(node);
		namespaceOf.set(node.id, key);
		typeCounts.set(key, (typeCounts.get(key) ?? 0) + 1);
	}

	const elements = [];
	for (const [key, count] of typeCounts) {
		const lines = [key, `${count} type${count === 1 ? '' : 's'}`];
		const colors = kindColors(key === EXTERNAL_GROUP ? 'external' : 'namespace');
		elements.push({
			group: 'nodes',
			data: { id: 'ns:' + key, name: key, ns: key, count, label: lines.join('\n'), ...sizeFor(lines), ...colors },
			classes: 'nsnode' + (key === EXTERNAL_GROUP ? ' external' : '')
		});
	}

	const links = new Map();
	for (const edge of graph.edges) {
		if (state.hiddenEdgeKinds.has(edge.kind)) continue;
		const from = namespaceOf.get(edge.source);
		const to = namespaceOf.get(edge.target);
		if (!from || !to || from === to) continue;

		const key = from + '\u0000' + to;
		const link = links.get(key) ?? { from, to, count: 0 };
		link.count += 1;
		links.set(key, link);
	}

	let i = 0;
	for (const link of links.values()) {
		elements.push({
			group: 'edges',
			data: { id: 'nse' + i++, source: 'ns:' + link.from, target: 'ns:' + link.to, kind: 'aggregate', count: link.count, label: `${link.count} relationship${link.count === 1 ? '' : 's'}`, color: edgeColor('uses') },
			classes: 'aggregate'
		});
	}

	return elements;
}

// ---------- STYLE ----------
// With many edges they're drawn faintly, and at full strength only around the selection.
const DENSE_EDGE_COUNT = 400;

function createStyle(dense) {
	const accent = cssVar('--accent', '#f25958');

	return [
		{
			selector: 'node.type, node.nsnode',
			style: {
				'shape': 'round-rectangle',
				'width': 'data(w)',
				'height': 'data(h)',
				'label': 'data(label)',
				'font-family': FONT_FAMILY,
				'font-size': FONT_SIZE,
				'line-height': LINE_HEIGHT,
				'text-wrap': 'wrap',
				'text-valign': 'center',
				'text-halign': 'center',
				'text-justification': 'left',
				'color': '#1f2328',
				'background-color': 'data(fill)',
				'border-color': 'data(border)',
				'border-width': 1.5
			}
		},
		{ selector: 'node.nsnode', style: { 'text-justification': 'center', 'border-width': 2 } },
		// With "Functions" on: a type holding its functions has its name on top, and each function is a small white box.
		{ selector: 'node.type.holder', style: { 'text-valign': 'top', 'text-margin-y': -3, 'font-weight': 'bold', 'padding': 10 } },
		{
			selector: 'node.method',
			style: {
				'shape': 'round-rectangle',
				'width': 'data(w)',
				'height': 'data(h)',
				'label': 'data(label)',
				'font-family': FONT_FAMILY,
				'font-size': FONT_SIZE,
				'text-valign': 'center',
				'text-halign': 'center',
				'color': '#1f2328',
				'background-color': '#ffffff',
				'border-color': 'data(border)',
				'border-width': 1
			}
		},
		{ selector: 'node.interface, node.external', style: { 'border-style': 'dashed' } },
		{ selector: 'node.external', style: { 'color': '#5f6670' } },
		{ selector: 'node.abstract', style: { 'font-style': 'italic' } },
		{
			selector: 'node.namespace',
			style: {
				'shape': 'round-rectangle',
				'label': 'data(label)',
				'font-family': 'system-ui, sans-serif',
				'font-size': 11,
				'font-weight': 'bold',
				'color': '#57606a',
				'text-valign': 'top',
				'text-halign': 'center',
				'text-margin-y': -2,
				'background-color': '#f6f7f9',
				'border-color': '#d0d5dc',
				'border-width': 1,
				'padding': 14
			}
		},
		{
			selector: 'edge',
			style: {
				'curve-style': 'bezier',
				'width': 1.4,
				'opacity': dense ? 0.3 : 0.85,
				'line-color': 'data(color)',
				'target-arrow-color': 'data(color)',
				'target-arrow-shape': 'vee',
				'arrow-scale': 0.9,
				'font-family': FONT_FAMILY,
				'font-size': 10,
				'color': '#24292f',
				'text-rotation': 'autorotate',
				'text-background-color': '#ffffff',
				'text-background-opacity': 0.9,
				'text-background-padding': 2
			}
		},
		{ selector: 'edge.inherits, edge.implements', style: { 'target-arrow-shape': 'triangle', 'target-arrow-fill': 'hollow', 'arrow-scale': 1.3 } },
		{ selector: 'edge.implements', style: { 'line-style': 'dashed' } },
		{ selector: 'edge.calls', style: { 'width': 'mapData(count, 1, 20, 1.4, 5)' } },
		{ selector: 'edge.renders', style: { 'width': 2.6 } },
		{ selector: 'edge.aggregate', style: { 'width': 'mapData(count, 1, 40, 1.2, 4)', 'label': 'data(count)' } },
		{ selector: 'edge.highlighted', style: { 'opacity': 1, 'z-index': 9 } },
		{ selector: 'edge.show-label', style: { 'label': 'data(label)', 'z-index': 10 } },
		{ selector: '.faded', style: { 'opacity': 0.12 } },
		{ selector: 'node.namespace.faded', style: { 'opacity': 0.35 } },
		// A glow as well as the border: class boxes are orange too, so the border color alone isn't enough.
		{ selector: 'node:selected', style: { 'border-width': 3, 'border-color': accent, 'underlay-color': accent, 'underlay-opacity': 0.22, 'underlay-padding': 6, 'underlay-shape': 'round-rectangle' } },
		{ selector: 'node.namespace:selected', style: { 'border-width': 2, 'border-color': accent, 'background-color': '#fdf1f1' } },
		{ selector: 'node.search-match', style: { 'underlay-color': '#ffcc33', 'underlay-opacity': 0.7, 'underlay-padding': 6, 'underlay-shape': 'round-rectangle' } },
		{ selector: '.filtered, .isolated-out', style: { 'display': 'none' } }
	];
}

// ---------- BUILD & LAYOUT ----------
function create(elements) {
	cy?.destroy();
	savedPositions = null;
	gvElements = new Map(); // the Graphviz drawing's elements are gone too; it's drawn again by runLayout

	const large = elements.length > 1500;
	const dense = elements.filter(e => e.group === 'edges').length > DENSE_EDGE_COUNT;
	cy = cytoscape({
		container,
		elements,
		style: createStyle(dense),
		minZoom: MIN_ZOOM,
		maxZoom: MAX_ZOOM,
		boxSelectionEnabled: false,
		// Cheaper redraws while panning/zooming big graphs.
		textureOnViewport: large,
		hideEdgesOnViewport: large
	});
	viewSize = cy.width() && cy.height() ? { w: cy.width(), h: cy.height() } : null;
	centerWhenShown = null;
	gvCenterWhenShown = null;

	// The selection is the app's alone (see selectElement): Cytoscape's own click selection would also select
	// edges, and toggle a node on a shift or ctrl click, without the details panel knowing.
	cy.elements().unselectify();

	cy.on('resize', onResized);
	cy.on('tap', 'node', evt => selectElement(evt.target, true));
	cy.on('tap', evt => {
		if (evt.target === cy) selectElement(null, true);
	});
	cy.on('dbltap', 'node.nsnode', evt => {
		dotNet?.invokeMethodAsync('OnNamespaceOpened', evt.target.data('ns'));
	});
	cy.on('mouseover', 'edge', evt => evt.target.addClass('show-label'));
	cy.on('mouseout', 'edge', evt => {
		// Keep the label if highlighting the selection shows it (see highlight).
		const edge = evt.target;
		const keep = edge.hasClass('highlighted') && cy.edges('.highlighted').length <= MAX_LABELED_EDGES;
		if (!keep) edge.removeClass('show-label');
	});
}

function visibleElements() {
	return cy.elements().not('.filtered, .isolated-out');
}

function layoutOptions(nodeCount) {
	if (state.layout === 'dagre') {
		return { name: 'dagre', rankDir: 'LR', nodeSep: 24, rankSep: 70, edgeSep: 8, animate: false, nodeDimensionsIncludeLabels: true, padding: 30 };
	}

	return {
		name: 'fcose',
		quality: nodeCount > 800 ? 'draft' : 'default',
		randomize: true,
		animate: false,
		padding: 30,
		nodeDimensionsIncludeLabels: true,
		packComponents: true,
		nodeRepulsion: 9000,
		idealEdgeLength: 90,
		edgeElasticity: 0.3,
		nestingFactor: 0.1,
		gravity: 0.25,
		gravityRangeCompound: 1.5,
		tile: true
	};
}

async function runLayout(elements) {
	const graphviz = state.layout === 'graphviz';
	showGraphviz(graphviz);
	if (graphviz) return renderGraphviz();

	const eles = elements ?? visibleElements();
	const layout = eles.layout({ ...layoutOptions(eles.nodes().length), fit: false });
	layout.one('layoutstop', () => fitTo(eles));
	layout.run();
}

// Fits the elements in view, but doesn't blow a handful of boxes up to fill the whole screen.
const MAX_FIT_ZOOM = 1.5;

function fitTo(eles) {
	// Fitting into a hidden (zero-sized) pane would break the zoom; fit once it's visible instead.
	if (container.clientWidth === 0 || container.clientHeight === 0) {
		fitWhenShown = true;
		return;
	}

	cy.fit(eles, 30);
	if (cy.zoom() > MAX_FIT_ZOOM) {
		cy.zoom(MAX_FIT_ZOOM);
		cy.center(eles);
	}
}

// Let the browser paint (e.g. the "Laying out..." message) before a long synchronous layout.
// A timer rather than requestAnimationFrame, which never fires while the tab is in the background.
function nextFrame() {
	return new Promise(resolve => setTimeout(resolve, 20));
}

async function rebuild() {
	const elements = state.view === 'namespaces' ? buildNamespaceElements() : buildTypeElements();
	create(elements);
	applyFilters();
	await nextFrame();
	await runLayout();
}

function applyFilters() {
	if (state.view === 'namespaces') return; // filters were applied while building

	cy.batch(() => {
		cy.elements().removeClass('filtered');
		cy.nodes('.type, .method').filter(n => state.hiddenNodeKinds.has(n.data('kind'))).addClass('filtered');
		cy.edges().filter(e => state.hiddenEdgeKinds.has(e.data('kind'))).addClass('filtered');
		cy.nodes('.namespace').filter(ns => ns.children().not('.filtered').empty()).addClass('filtered');
	});
}

// ---------- SELECTION & FOCUS ----------
// Nodes plus the visible edges between them and their direct neighbours.
function neighborhood(nodes) {
	const edges = nodes.connectedEdges().not('.filtered');
	// A neighbour of a hidden kind stays hidden (its edge isn't drawn either).
	return nodes.union(edges).union(edges.connectedNodes().not('.filtered'));
}

function seedNodes(element) {
	// A namespace box stands for the types inside it, and a type for its functions too.
	if (element.hasClass('namespace')) return element.children().union(element.children().children());
	return element.union(element.children());
}

// A function, the functions that (in turn) call it and the functions it (in turn) calls, with those calls.
function callChain(method) {
	// Functions have no other edges than calls.
	return method.union(method.predecessors()).union(method.successors()).not('.filtered');
}

// What selecting or focusing an element shows: a function's call chain, or the element and its neighbours.
function relatedTo(element) {
	return element.hasClass('method') ? callChain(element) : neighborhood(seedNodes(element));
}

function highlight(element) {
	cy.batch(() => {
		cy.elements().removeClass('faded show-label highlighted');
		if (!element) return;

		const hood = relatedTo(element);
		const keep = hood.union(hood.nodes().ancestors());
		cy.elements().not(keep).addClass('faded');
		hood.edges().addClass('highlighted');
		if (hood.edges().length <= MAX_LABELED_EDGES) hood.edges().addClass('show-label');
	});
	syncGraphviz();
}

// Keeps the middle of the view where it was when the container changes size (panes opened or closed),
// and fits a diagram that was laid out while the pane was hidden once the pane is shown.
// Runs on every cy.resize(), whichever observer noticed the change.
function onResized() {
	const size = { w: cy.width(), h: cy.height() };

	// The diagram pane is hidden: keep the last visible size, so showing it again changes nothing.
	if (size.w === 0 || size.h === 0) return;

	if (fitWhenShown) {
		fitWhenShown = false;
		fitTo(visibleElements());
	} else if (viewSize && (size.w !== viewSize.w || size.h !== viewSize.h)) {
		cy.panBy({ x: (size.w - viewSize.w) / 2, y: (size.h - viewSize.h) / 2 });
	}
	viewSize = size;

	if (centerWhenShown) {
		cy.center(centerWhenShown);
		centerWhenShown = null;
	}
}

function selectElement(element, fromUser) {
	// Clicks can't select (see create), so the elements are selectable only for this.
	cy.$(':selected').selectify().unselect().unselectify();
	element?.selectify().select().unselectify();
	highlight(element);

	if (fromUser) {
		const kind = !element ? null : element.hasClass('type') ? 'type' : element.hasClass('method') ? 'method' : element.hasClass('file') ? 'file' : 'namespace';
		const id = { type: element?.id(), method: element?.id(), file: element?.data('file'), namespace: element?.data('ns') }[kind] ?? null;
		dotNet?.invokeMethodAsync('OnGraphSelection', kind, id);
	}
}

function findElement(kind, id) {
	const element = cy.getElementById(kind === 'namespace' ? 'ns:' + id : kind === 'file' ? 'file:' + id : id);
	return element.nonempty() ? element : null;
}

// ---------- GRAPHVIZ ----------
// Draws the visible elements with Graphviz (Viz.js), like the first version of the app, as an SVG that is
// panned and zoomed with a CSS transform. Clicks select the Cytoscape element, so the details panel, focus
// and search work as usual; their highlighting is copied onto the SVG as classes (see Layout/Main.razor.css).
let gvContainer = null;
let gvCanvas = null;
let gvElements = new Map();   // SVG element id -> { element, g }
let gvContentSize = null;     // the SVG's size at zoom 1
let gvView = { x: 0, y: 0, scale: 1 };
let gvSize = null;            // the container's size when it was last visible
let gvFitWhenShown = false;
let gvCenterWhenShown = null; // as centerWhenShown
let gvRenderId = 0;
let gvDrag = null;
let vizPromise = null;

// Viz.js (Graphviz compiled to WebAssembly, about 1.5 MB) is only downloaded when the Graphviz layout is picked.
function loadViz() {
	vizPromise ??= import(/* webpackChunkName: "viz" */ '@viz-js/viz').then(viz => viz.instance());
	return vizPromise;
}

function showGraphviz(on) {
	gvContainer.hidden = !on;
	container.style.visibility = on ? 'hidden' : '';
}

// Inside a quoted DOT string only backslashes and quotes need escaping.
function dotString(value) {
	return '"' + String(value).replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"';
}

function dotAttributes(attributes, recordLabel) {
	const parts = Object.entries(attributes).filter(([, value]) => value != null).map(([name, value]) => `${name}=${dotString(value)}`);
	if (recordLabel != null) parts.push(`label="${recordLabel}"`);
	return `[${parts.join(', ')}]`;
}

// Record labels treat { } | < > as structure, so generics like List<T> must be escaped.
function recordText(value) {
	return String(value).replace(/[\\"{}|<>]/g, c => '\\' + c);
}

// The first version's boxes: the name, then the fields and properties, then the methods (each left-aligned).
function graphvizNodeLabel(node) {
	if (node.hasClass('nsnode')) {
		const count = node.data('count');
		return `${recordText(node.data('name'))}\\n${count} type${count === 1 ? '' : 's'}`;
	}

	const stereotype = node.data('stereotype');
	const title = (stereotype ? recordText(stereotype) + '\\n' : '') + recordText(node.data('name'));
	if (node.hasClass('external')) return title;

	const members = node.data('members') ?? [];
	const section = lines => lines.length ? '|' + lines.map(recordText).join('\\l') + '\\l' : '';
	return `{${title}${section(members.filter(m => !m.includes('(')))}${section(members.filter(m => m.includes('(')))}}`;
}

function graphvizNode(node, id) {
	if (node.hasClass('method')) {
		return dotAttributes({ id, shape: 'box', style: 'rounded,filled', color: node.data('border'), margin: '0.15,0.06', height: 0.3, label: node.data('name') });
	}

	const external = node.hasClass('external');
	return dotAttributes({
		id,
		shape: node.hasClass('nsnode') || external ? 'box' : 'record',
		// Filled white, so a click anywhere in the box selects it.
		style: external ? 'filled,dashed' : 'filled',
		color: external ? 'gray50' : undefined,
		fontcolor: external ? 'gray40' : undefined
	}, graphvizNodeLabel(node));
}

// As in Diagram/DotRenderer.cs (the DOT download): labelled arrows, dotted calls, dashed implementations.
function graphvizEdge(edge, id, labelAttribute) {
	const kind = edge.data('kind');
	const count = edge.data('count') ?? 1;
	const label = kind === 'calls' ? edge.data('label') : kind === 'aggregate' ? String(count) : kind;
	return dotAttributes({
		id,
		[labelAttribute]: label,
		tooltip: edge.data('label') || kind,
		style: kind === 'implements' ? 'dashed' : kind === 'calls' ? 'dotted' : kind === 'renders' ? 'bold' : undefined,
		arrowhead: kind === 'inherits' || kind === 'implements' ? 'empty' : undefined
	});
}

// DOT for the given elements, drawn like the first version of the app. Namespace and file boxes, and the
// types holding functions, become clusters.
function toDot(eles) {
	const edgeCount = eles.edges().length;
	const lines = [
		'digraph G {',
		'graph [rankdir=LR, nodesep=1.0, ranksep=1.5, pad=0.3, bgcolor="white"];',
		// Graphviz measures text with Courier's widths; it's drawn in FONT_FAMILY, which is no wider (see renderGraphviz).
		'node [fontname="Courier", fontsize=12, margin="0.3,0.2", fillcolor="white"];',
		'edge [fontsize=10];'
	];
	// Fewer crossing-minimization passes: much faster on dense graphs (as in Diagram/DotRenderer.cs).
	if (edgeCount > 100) lines.push('mclimit=0.1;');
	// Edge labels take part in the layout and make big graphs about 10x slower; xlabels are placed afterwards.
	const labelAttribute = edgeCount > 100 ? 'xlabel' : 'label';

	const ids = new Map(); // SVG id -> Cytoscape element
	const nodeNames = new Map();
	const clusters = new Map(); // a type holding functions -> its cluster, and a function in it for its edges to end at
	const drawn = eles.nodes();
	const addNode = (node, indent) => {
		const name = 'n' + nodeNames.size;
		nodeNames.set(node.id(), name);
		ids.set('gv-' + name, node);
		lines.push(indent + name + ' ' + graphvizNode(node, 'gv-' + name) + ';');
	};

	// Namespace and file boxes, and (with "Functions" on) the types holding functions, become clusters.
	const addCluster = (box, indent) => {
		const i = ids.size;
		const id = 'gv-c' + i;
		ids.set(id, box);
		const holder = box.hasClass('holder');
		const attributes = holder
			? { id, label: box.data('name'), tooltip: box.data('name'), color: box.data('border'), fillcolor: box.data('fill'), style: 'rounded,filled', fontname: 'Courier-Bold' }
			: { id, label: box.data('label'), tooltip: box.data('label'), color: 'gray60' };
		lines.push(`${indent}subgraph cluster_${i} {`, `${indent}  graph ${dotAttributes(attributes)};`);
		box.children().intersection(drawn).forEach(child => child.isParent() ? addCluster(child, indent + '  ') : addNode(child, indent + '  '));
		lines.push(indent + '}');
		if (holder) clusters.set(box.id(), { cluster: 'cluster_' + i, anchor: nodeNames.get(box.children().intersection(drawn)[0]?.id()) });
	};
	drawn.filter(node => node.parent().intersection(drawn).empty()).forEach(node => node.isParent() ? addCluster(node, '') : addNode(node, ''));

	eles.edges().forEach((edge, i) => {
		// An edge of a hidden type has no class of its own, but its end isn't in the drawing.
		// An edge to or from a type holding functions ends at the edge of its box (compound=true).
		const end = node => nodeNames.get(node.id()) ?? clusters.get(node.id())?.anchor;
		const from = end(edge.source());
		const to = end(edge.target());
		if (!from || !to) return;
		ids.set('gv-e' + i, edge);
		const ltail = clusters.get(edge.source().id())?.cluster;
		const lhead = clusters.get(edge.target().id())?.cluster;
		const ends = (ltail ? `ltail=${ltail}, ` : '') + (lhead ? `lhead=${lhead}, ` : '');
		lines.push(`${from} -> ${to} ${graphvizEdge(edge, 'gv-e' + i, labelAttribute).replace('[', '[' + ends)};`);
	});
	if (clusters.size) lines.splice(1, 0, 'compound=true;');

	lines.push('}');
	return { dot: lines.join('\n'), ids };
}

async function renderGraphviz() {
	const renderId = ++gvRenderId;
	const { dot, ids } = toDot(visibleElements());

	let svg;
	try {
		const viz = await loadViz();
		if (renderId !== gvRenderId) return; // a newer drawing was asked for meanwhile
		svg = viz.renderSVGElement(dot);
	} catch (error) {
		// A Viz instance can't be used again after it failed (e.g. out of memory): start a new one next time.
		vizPromise = null;
		if (renderId === gvRenderId) showGraphvizError(error);
		return;
	}

	// The SVG's own titles would show the DOT names (n12, cluster_3) as tooltips; the tooltip attributes stay.
	svg.querySelectorAll('title').forEach(title => title.remove());
	svg.querySelectorAll('text').forEach(text => {
		if (/^Courier/.test(text.getAttribute('font-family'))) text.setAttribute('font-family', FONT_FAMILY);
	});
	// One SVG unit per pixel, so the text is the same size as in the Cytoscape layouts.
	const [, , w, h] = svg.getAttribute('viewBox').split(/[\s,]+/).map(Number);
	svg.setAttribute('width', w);
	svg.setAttribute('height', h);
	gvContentSize = { w, h };

	gvElements = new Map();
	for (const [id, element] of ids) {
		const g = svg.getElementById(id);
		if (g) gvElements.set(id, { element, g });
	}

	gvCanvas.replaceChildren(svg);
	syncGraphviz();
	graphvizFit();
}

function showGraphvizError(error) {
	gvContentSize = null;
	gvElements = new Map();
	const message = document.createElement('div');
	message.className = 'gv-error';
	message.textContent = /memory/i.test(error?.message ?? '')
		? 'The diagram is too large for Graphviz. Try the Clustered layout, the namespace overview or fewer files.'
		: `Graphviz couldn't draw the diagram: ${error?.message ?? error}`;
	gvCanvas.replaceChildren(message);
	gvView = { x: 0, y: 0, scale: 1 };
	applyGraphvizTransform();
}

// Copies the highlighting (selection, focus, search) from the Cytoscape elements onto the SVG.
function syncGraphviz() {
	if (state.layout !== 'graphviz') return;
	for (const { element, g } of gvElements.values()) {
		g.classList.toggle('gv-selected', element.selected());
		g.classList.toggle('gv-faded', element.hasClass('faded'));
		g.classList.toggle('gv-match', element.hasClass('search-match'));
	}
}

function applyGraphvizTransform() {
	gvCanvas.style.transform = `translate(${gvView.x}px, ${gvView.y}px) scale(${gvView.scale})`;
}

function graphvizZoomAt(factor, x, y) {
	const scale = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, gvView.scale * factor));
	const k = scale / gvView.scale;
	gvView = { scale, x: x - (x - gvView.x) * k, y: y - (y - gvView.y) * k };
	applyGraphvizTransform();
}

// Shows the rectangle (in SVG pixels) in the middle of the view, at most at maxZoom.
function graphvizShowRect(rect, padding, maxZoom) {
	const w = gvContainer.clientWidth;
	const h = gvContainer.clientHeight;
	// Fitting into a hidden (zero-sized) pane would break the zoom; fit once it's visible instead.
	if (w === 0 || h === 0) {
		gvFitWhenShown = true;
		return;
	}

	const scale = Math.max(MIN_ZOOM, Math.min(maxZoom, (w - 2 * padding) / Math.max(rect.w, 1), (h - 2 * padding) / Math.max(rect.h, 1)));
	gvView = { scale, x: w / 2 - (rect.x + rect.w / 2) * scale, y: h / 2 - (rect.y + rect.h / 2) * scale };
	gvSize = { w, h }; // what a later resize is measured against
	applyGraphvizTransform();
}

function graphvizFit() {
	if (gvContentSize) graphvizShowRect({ x: 0, y: 0, ...gvContentSize }, 30, MAX_FIT_ZOOM);
}

// Centres the element's drawing in view, at the same zoom.
function graphvizCenter(element) {
	const rect = graphvizRectOf(element);
	if (rect) graphvizShowRect(rect, 0, gvView.scale);
}

// The bounding box of the elements' drawings, in SVG pixels.
function graphvizRectOf(elements) {
	const ids = new Set(elements.map(e => e.id()));
	const origin = gvCanvas.getBoundingClientRect();
	let left = Infinity, top = Infinity, right = -Infinity, bottom = -Infinity;
	for (const { element, g } of gvElements.values()) {
		if (!ids.has(element.id())) continue;
		const r = g.getBoundingClientRect();
		left = Math.min(left, r.left); top = Math.min(top, r.top);
		right = Math.max(right, r.right); bottom = Math.max(bottom, r.bottom);
	}
	if (left === Infinity) return null;
	return { x: (left - origin.left) / gvView.scale, y: (top - origin.top) / gvView.scale, w: (right - left) / gvView.scale, h: (bottom - top) / gvView.scale };
}

function graphvizElementAt(target) {
	const g = target?.closest?.('g.node, g.cluster');
	return g ? gvElements.get(g.id)?.element ?? null : null;
}

// Like onResized for Cytoscape: keeps the middle of the view in place, and fits a drawing made while hidden.
function onGraphvizResized() {
	const size = { w: gvContainer.clientWidth, h: gvContainer.clientHeight };
	if (size.w === 0 || size.h === 0) return;

	if (gvFitWhenShown) {
		gvFitWhenShown = false;
		graphvizFit();
	} else if (gvSize && (size.w !== gvSize.w || size.h !== gvSize.h)) {
		gvView.x += (size.w - gvSize.w) / 2;
		gvView.y += (size.h - gvSize.h) / 2;
		applyGraphvizTransform();
	}
	gvSize = size;

	if (gvCenterWhenShown) {
		graphvizCenter(gvCenterWhenShown);
		gvCenterWhenShown = null;
	}
}

// Drag to pan, wheel to zoom, click to select; a click is a press that didn't move.
function initGraphviz(element) {
	gvContainer = element;
	gvCanvas = document.createElement('div');
	gvCanvas.className = 'gv-canvas';
	gvContainer.replaceChildren(gvCanvas);

	gvContainer.addEventListener('pointerdown', e => {
		if (e.button !== 0) return;
		gvDrag = { startX: e.clientX, startY: e.clientY, viewX: gvView.x, viewY: gvView.y, target: e.target, moved: false };
		gvContainer.setPointerCapture(e.pointerId);
	});
	gvContainer.addEventListener('pointermove', e => {
		if (!gvDrag) return;
		const dx = e.clientX - gvDrag.startX;
		const dy = e.clientY - gvDrag.startY;
		if (!gvDrag.moved && Math.hypot(dx, dy) < 4) return;
		gvDrag.moved = true;
		gvContainer.classList.add('panning');
		gvView.x = gvDrag.viewX + dx;
		gvView.y = gvDrag.viewY + dy;
		applyGraphvizTransform();
	});
	const endDrag = e => {
		if (!gvDrag) return;
		const drag = gvDrag;
		gvDrag = null;
		gvContainer.classList.remove('panning');
		if (e.type === 'pointerup' && !drag.moved && cy) selectElement(graphvizElementAt(drag.target), true);
	};
	gvContainer.addEventListener('pointerup', endDrag);
	gvContainer.addEventListener('pointercancel', endDrag);
	gvContainer.addEventListener('dblclick', e => {
		const element = graphvizElementAt(e.target);
		if (element?.hasClass('nsnode')) dotNet?.invokeMethodAsync('OnNamespaceOpened', element.data('ns'));
	});
	gvContainer.addEventListener('wheel', e => {
		e.preventDefault();
		const box = gvContainer.getBoundingClientRect();
		const pixels = e.deltaMode === 1 ? e.deltaY * 16 : e.deltaY;
		graphvizZoomAt(Math.exp(-pixels * 0.0015), e.clientX - box.left, e.clientY - box.top);
	}, { passive: false });
}

async function exportGraphvizPng(fileName) {
	const svg = gvCanvas.querySelector('svg');
	if (!svg || !gvContentSize) return;

	const { w, h } = gvContentSize;
	const scale = Math.min(2, 12000 / Math.max(w, h));
	const url = URL.createObjectURL(new Blob([new XMLSerializer().serializeToString(svg)], { type: 'image/svg+xml' }));
	try {
		const image = new Image();
		await new Promise((resolve, reject) => {
			image.onload = resolve;
			image.onerror = reject;
			image.src = url;
		});
		const canvas = document.createElement('canvas');
		canvas.width = Math.round(w * scale);
		canvas.height = Math.round(h * scale);
		const context = canvas.getContext('2d');
		context.fillStyle = '#ffffff';
		context.fillRect(0, 0, canvas.width, canvas.height);
		context.drawImage(image, 0, 0, canvas.width, canvas.height);
		downloadBlob(await new Promise(resolve => canvas.toBlob(resolve, 'image/png')), fileName);
	} finally {
		URL.revokeObjectURL(url);
	}
}

function downloadBlob(blob, fileName) {
	const url = URL.createObjectURL(blob);
	const a = document.createElement('a');
	a.href = url;
	a.download = fileName;
	document.body.appendChild(a);
	a.click();
	a.remove();
	URL.revokeObjectURL(url);
}

function applyOptions(options) {
	state.view = options.view ?? state.view;
	state.layout = options.layout ?? state.layout;
	state.groupBy = options.groupBy ?? state.groupBy;
	state.showMembers = options.showMembers ?? state.showMembers;
	state.showFunctions = options.showFunctions ?? state.showFunctions;
	if (options.hiddenEdgeKinds) state.hiddenEdgeKinds = new Set(options.hiddenEdgeKinds);
	if (options.hiddenNodeKinds) state.hiddenNodeKinds = new Set(options.hiddenNodeKinds);
}

// ---------- API FOR C# ----------
window.CodeGraphView = {
	init(containerId, graphvizContainerId, dotNetRef) {
		container = document.getElementById(containerId);
		dotNet = dotNetRef;
		initGraphviz(document.getElementById(graphvizContainerId));

		// Cytoscape notices container size changes itself, but only after a 100 ms debounce:
		// resizing straight away keeps the diagram steady while a splitter is dragged.
		resizeObserver?.disconnect();
		resizeObserver = new ResizeObserver(() => {
			cy?.resize();
			onGraphvizResized();
		});
		resizeObserver.observe(container);
		resizeObserver.observe(gvContainer);
	},

	async render(graphData, options) {
		graph = graphData;
		applyOptions(options);
		await rebuild();
		return { nodes: cy.nodes('.type, .nsnode').length, edges: cy.edges().length };
	},

	// Changes that need new elements (view, grouping, member labels) rebuild and lay out again.
	async update(options) {
		if (!cy) return;
		applyOptions(options);
		await rebuild();
	},

	// Filters only hide and show elements, so the layout stays where it is. Graphviz draws them again.
	async setFilters(hiddenEdgeKinds, hiddenNodeKinds) {
		if (!cy) return;
		applyOptions({ hiddenEdgeKinds, hiddenNodeKinds });
		if (state.view === 'namespaces') {
			// The overview is built again, which loses the selection: select the same namespace again, or tell
			// the page it's gone (all its types are hidden now).
			const selectedId = cy.$(':selected').first().id();
			await rebuild();
			const element = selectedId ? cy.getElementById(selectedId) : null;
			if (element?.nonempty()) selectElement(element, false);
			else if (selectedId) selectElement(null, true);
		} else {
			applyFilters();
			const selected = cy.$(':selected');
			highlight(selected.nonempty() ? selected[0] : null);
			if (state.layout === 'graphviz') await renderGraphviz();
		}
	},

	// A new layout shows everything again, as "show all" does.
	async relayout(layout) {
		if (!cy) return;
		state.layout = layout;
		cy.elements().removeClass('isolated-out');
		savedPositions = null;
		const selected = cy.$(':selected');
		highlight(selected.nonempty() ? selected[0] : null);
		await nextFrame();
		await runLayout();
	},

	select(kind, id, center) {
		if (!cy) return false;
		const element = findElement(kind, id);
		if (!element) return false;

		selectElement(element, false);
		if (center && element.visible()) {
			// In a hidden pane there's nothing to centre in yet: that's done once it's shown (the resize handlers).
			if (state.layout === 'graphviz') {
				if (gvContainer.clientWidth === 0 || gvContainer.clientHeight === 0) gvCenterWhenShown = element;
				else graphvizCenter(element);
			} else {
				cy.resize(); // in case a pane was just opened or closed
				if (cy.width() === 0 || cy.height() === 0) centerWhenShown = element;
				else cy.animate({ center: { eles: element } }, { duration: 250 });
			}
		}
		return true;
	},

	clearSelection() {
		if (cy) selectElement(null, false);
	},

	// Shows only the element and its neighbours (depth 1 or 2), laid out on their own. False when the element
	// isn't in the diagram, which then stays as it was.
	async isolate(kind, id, depth) {
		const element = cy && findElement(kind, id);
		if (!element) return false;

		let hood = relatedTo(element);
		if (!element.hasClass('method')) {
			for (let i = 1; i < depth; i++) hood = hood.union(neighborhood(hood.nodes()));
		}
		// A type box shown keeps all its functions; only the boxes around it are left out. A namespace or file
		// box is kept only around something shown in it: an empty one makes the clustered layout throw.
		const shown = hood.not('.filtered');
		const keep = shown.union(shown.nodes().ancestors()).union(shown.nodes('.holder').children());

		if (!savedPositions) {
			savedPositions = new Map();
			cy.nodes().filter(n => n.isChildless()).forEach(n => savedPositions.set(n.id(), { ...n.position() }));
		}

		cy.batch(() => {
			cy.elements().removeClass('isolated-out faded');
			cy.elements().not(keep).addClass('isolated-out');
		});
		await nextFrame();
		await runLayout();
		selectElement(element, false);
		return true;
	},

	async showAll() {
		if (!cy) return;
		cy.elements().removeClass('isolated-out');
		if (savedPositions) {
			cy.nodes().filter(n => n.isChildless()).forEach(n => {
				const position = savedPositions.get(n.id());
				if (position) n.position(position);
			});
			savedPositions = null;
		}
		const selected = cy.$(':selected');
		highlight(selected.nonempty() ? selected[0] : null);
		if (state.layout === 'graphviz') await renderGraphviz();
		else fitTo(visibleElements());
	},

	search(text, matchCase) {
		if (!cy) return 0;
		const fold = value => matchCase ? value : value.toLowerCase();
		const query = fold((text ?? '').trim());
		cy.nodes().removeClass('search-match');
		let count = 0;
		if (query) {
			const matches = cy.nodes('.type, .nsnode, .method').filter(n => n.visible() && fold(n.data('name') ?? '').includes(query));
			matches.addClass('search-match');
			count = matches.length;
		}
		syncGraphviz();
		return count;
	},

	zoomToSearch() {
		const matches = cy?.nodes('.search-match');
		if (!matches || matches.empty()) return;

		const zoomToMatches = () => {
			// Like fitting, but without zooming a single match up to fill the screen.
			if (state.layout === 'graphviz') {
				const rect = graphvizRectOf(matches);
				if (rect) graphvizShowRect(rect, 80, MAX_FIT_ZOOM);
				return;
			}
			cy.resize();
			const box = matches.boundingBox();
			const zoom = Math.min(MAX_FIT_ZOOM, (cy.width() - 160) / Math.max(box.w, 1), (cy.height() - 160) / Math.max(box.h, 1));
			cy.animate({ zoom, center: { eles: matches } }, { duration: 300 });
		};

		if (matches.length === 1) {
			// Selecting opens the details panel, which narrows the diagram: zoom once it has.
			selectElement(matches[0], true);
			setTimeout(zoomToMatches, 150);
		} else {
			zoomToMatches();
		}
	},

	zoomBy(factor) {
		if (!cy) return;
		if (state.layout === 'graphviz') {
			graphvizZoomAt(factor, gvContainer.clientWidth / 2, gvContainer.clientHeight / 2);
			return;
		}
		cy.resize();
		cy.zoom({ level: cy.zoom() * factor, renderedPosition: { x: cy.width() / 2, y: cy.height() / 2 } });
	},

	fit() {
		if (!cy) return;
		if (state.layout === 'graphviz') {
			graphvizFit();
			return;
		}
		cy.resize(); // in case a pane was just opened or closed
		fitTo(visibleElements());
	},

	async exportPng(fileName) {
		if (!cy) return;
		if (state.layout === 'graphviz') {
			await exportGraphvizPng(fileName);
			return;
		}
		downloadBlob(cy.png({ output: 'blob', full: true, scale: 2, bg: '#ffffff', maxWidth: 12000, maxHeight: 12000 }), fileName);
	},

	dispose() {
		resizeObserver?.disconnect();
		resizeObserver = null;
		cy?.destroy();
		cy = null;
		gvElements = new Map();
		dotNet = null;
	}
};
