import * as monaco from 'monaco-editor';

let monacoEditor;

//let dotNetObjRef;

//window.setDotNetObjRef = function (ref) {
//	dotNetObjRef = ref;
//}

//window.sendSourceCodeToCSharp = function (text) {
//	dotNetObjRef.invokeMethodAsync('SourceCodeEditorContentChanged', text);
//}
//
//window.sendAssemblyCodeToCSharp = function (text) {
//	dotNetObjRef.invokeMethodAsync('AssemblyCodeEditorContentChanged', text);
//}


window.InitializeMonaco = function (elementId, content) {
	monacoEditor = monaco.editor.create(document.getElementById(elementId), {
		value: content,
		language: "csharp",
		automaticLayout: true,
		theme: "vs"
	});
}

window.GetMonacoContent = function () {
	return monacoEditor.getValue();
}

window.SetMonacoContent = function (text) {
	monacoEditor.setValue(text);
}

window.DisposeMonaco = function () {
	if (monacoEditor) {
		monacoEditor.dispose();
		monacoEditor = null;
	}
}