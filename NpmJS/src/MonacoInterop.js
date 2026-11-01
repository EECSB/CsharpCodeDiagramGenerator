// The editor API alone; MonacoWebpackPlugin (webpack.config.js) adds the editor features and C#.
// Importing 'monaco-editor' itself would bundle every language Monaco knows.
import * as monaco from 'monaco-editor/esm/vs/editor/editor.api';

let monacoEditor;
let revealDecorations;

window.InitializeMonaco = function (elementId, content) {
	monacoEditor = monaco.editor.create(document.getElementById(elementId), {
		value: content,
		language: "csharp",
		automaticLayout: true,
		theme: "vs",
		minimap: { enabled: false },
		fontSize: 13
	});

	// A revealed line stays highlighted until the editor is clicked or edited.
	monacoEditor.onMouseDown(() => revealDecorations?.clear());
	monacoEditor.onDidChangeModelContent(() => revealDecorations?.clear());
}

window.GetMonacoContent = function () {
	return monacoEditor.getValue();
}

window.SetMonacoContent = function (text) {
	monacoEditor.setValue(text);
}

// The marker line a file starts after (see Diagram/SourceFile.cs): a whole line, with as many '=' on each side
// as the first marker in the editor has.
function findMarker(model, path) {
	const first = model.findMatches('^// (={5,}) File: .+ \\1$', false, true, true, null, true, 1)[0];
	if (!first) return null;

	const equals = first.matches[1];
	const escapedPath = path.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
	return model.findMatches(`^// ${equals} File: ${escapedPath} ${equals}$`, false, true, true, null, false, 1)[0] ?? null;
}

// Scrolls to a file in the editor (see Diagram/SourceFile.cs for the "// ===== File: ... =====" marker lines)
// and, when a line is given, to that 0-based line of the file.
window.RevealMonacoFile = function (path, line) {
	if (!monacoEditor) return;

	const model = monacoEditor.getModel();
	const marker = path ? findMarker(model, path) : null;

	// A file starts on the line after its marker. A path without one is the text above the first marker, or
	// all of it, which SourceBundle.Split calls Source.cs.
	const markerLine = marker ? marker.range.startLineNumber : 0;

	if (line === null || line === undefined) {
		revealDecorations?.clear();
		monacoEditor.revealLineNearTop(Math.max(markerLine, 1));
		monacoEditor.setPosition({ lineNumber: Math.max(markerLine, 1), column: 1 });
		return;
	}

	const lineNumber = Math.min(markerLine + 1 + line, model.getLineCount());
	monacoEditor.revealLineInCenter(lineNumber);
	monacoEditor.setPosition({ lineNumber, column: 1 });
	monacoEditor.focus();

	revealDecorations?.clear();
	revealDecorations = monacoEditor.createDecorationsCollection([{
		range: new monaco.Range(lineNumber, 1, lineNumber, 1),
		options: { isWholeLine: true, className: 'monaco-reveal-highlight', overviewRuler: { color: '#f25958', position: monaco.editor.OverviewRulerLane.Full } }
	}]);
}

window.DisposeMonaco = function () {
	if (monacoEditor) {
		monacoEditor.dispose();
		monacoEditor = null;
	}
}
