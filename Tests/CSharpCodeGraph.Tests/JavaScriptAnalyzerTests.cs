using CSharpCodeGraph.Diagram;
using Xunit;

namespace CSharpCodeGraph.Tests
{
    public class JavaScriptAnalyzerTests
    {
        private static readonly DiagramSettings ShowEverything = new() { OnlyShowMyCode = false };

        private static CodeGraph AnalyzeFiles(DiagramSettings? settings, params (string Path, string Content)[] files) =>
            CodeAnalyzer.Analyze(files.Select(f => new SourceFile(f.Path, f.Content)).ToList(), settings);

        private static GraphEdge? FindEdge(CodeGraph graph, string source, string target, EdgeKind kind) =>
            graph.Edges.SingleOrDefault(e => e.SourceId == source && e.TargetId == target && e.Kind == kind);

        private static TypeNode Node(CodeGraph graph, string id) => graph.Types.Single(t => t.Id == id);


        [Fact]
        public void Module_ListsTopLevelFunctionsAndVariables_WithTheirLines()
        {
            var graph = AnalyzeFiles(null, ("app/util.js", "const limit = 10;\n\nfunction format(value, digits = 2) {\n  return value.toFixed(digits);\n}\nconst twice = x => x * 2;\n"));

            var module = Node(graph, "js:app/util.js");
            Assert.Equal(NodeKind.Module, module.Kind);
            Assert.Equal("util.js", module.Name);
            Assert.Equal("app", module.Namespace);
            Assert.Equal(new[] { "limit", "format(value, digits = 2)", "twice(x)" }, module.Members.Select(m => m.Display));
            Assert.Equal(2, module.Members[1].Line);
            Assert.Equal("app/util.js", module.Members[1].FilePath);
        }

        [Fact]
        public void Classes_AreNodes_WithMembersAndInheritance()
        {
            var graph = AnalyzeFiles(null, ("shapes.js", """
                export class Shape {
                    #id = 0;
                    name = "shape";
                    constructor(name) { this.name = name; }
                    get label() { return this.name; }
                    area() { return 0; }
                    static create(kind) { return new Circle(1); }
                }
                export class Circle extends Shape {
                    onClick = (event) => { this.area(); };
                    area() { return Math.PI * this.r ** 2; }
                }
                """));

            Assert.DoesNotContain(graph.Types, t => t.Kind == NodeKind.Module); // only classes
            var shape = Node(graph, "js:shapes.js#Shape");
            Assert.Equal(new[] { "name", "label", "area()", "create(kind)" }, shape.Members.Select(m => m.Display));
            Assert.NotNull(FindEdge(graph, "js:shapes.js#Circle", "js:shapes.js#Shape", EdgeKind.Inherits));

            var creation = FindEdge(graph, "js:shapes.js#Shape", "js:shapes.js#Circle", EdgeKind.Calls);
            Assert.NotNull(creation);
            Assert.Equal("create() → new Circle()", creation!.Calls.Single().ToString());
        }

        [Fact]
        public void Imports_LinkModules_AndCallsGoToTheImportedFunction()
        {
            var graph = AnalyzeFiles(null,
                ("src/math.js", "export function add(a, b) { return a + b; }\nexport const half = n => n / 2;\n"),
                ("src/main.js", "import { add } from './math.js';\nimport * as m from \"./math\";\nimport cytoscape from 'cytoscape';\nfunction run() { return add(1, 2) + m.half(4); }\n"));

            Assert.NotNull(FindEdge(graph, "js:src/main.js", "js:src/math.js", EdgeKind.Uses));
            var calls = FindEdge(graph, "js:src/main.js", "js:src/math.js", EdgeKind.Calls);
            Assert.Equal(new[] { "run() → add()", "run() → half()" }, calls!.Calls.Select(c => c.ToString()));
            Assert.DoesNotContain(graph.Types, t => t.IsExternal);
        }

        [Fact]
        public void DefaultImports_GoToTheFunctionOrClassExportedAsDefault()
        {
            var graph = AnalyzeFiles(ShowEverything,
                ("render.js", "export default function render() { }\n"),
                ("paint.js", "function paint() { }\nexport default paint;\n"),
                ("shapes.js", "class Square { }\nexport default class Circle { }\n"),
                ("main.js", "import draw from './render.js';\nimport fill from './paint.js';\nimport Shape from './shapes.js';\nfunction run() { draw(); fill(); return new Shape(); }\n"));

            Assert.Equal("run() → render()", FindEdge(graph, "js:main.js", "js:render.js", EdgeKind.Calls)!.Calls.Single().ToString());
            Assert.Equal("run() → paint()", FindEdge(graph, "js:main.js", "js:paint.js", EdgeKind.Calls)!.Calls.Single().ToString());
            Assert.Equal("run() → new Circle()", FindEdge(graph, "js:main.js", "js:shapes.js#Circle", EdgeKind.Calls)!.Calls.Single().ToString());
            Assert.DoesNotContain(graph.Types, t => t.IsExternal);
        }

        [Fact]
        public void ExportAliases_LeadToTheFunctionTheyRename()
        {
            var graph = AnalyzeFiles(null,
                ("a.js", "function render() { }\nexport { render as draw, render as default };\n"),
                ("b.js", "import paint, { draw } from './a.js';\nimport * as a from './a.js';\nfunction one() { draw(); }\nfunction two() { paint(); }\nfunction three() { a.draw(); }\n"));

            var calls = FindEdge(graph, "js:b.js", "js:a.js", EdgeKind.Calls);
            Assert.Equal(new[] { "one() → render()", "two() → render()", "three() → render()" }, calls!.Calls.Select(c => c.ToString()));
        }

        [Fact]
        public void Packages_AreExternalNodes_WhenFrameworkTypesAreShown()
        {
            var graph = AnalyzeFiles(ShowEverything, ("main.js", "import * as monaco from 'monaco-editor/esm/vs/editor/editor.api';\nimport { instance } from '@viz-js/viz';\n"));

            Assert.NotNull(FindEdge(graph, "js:main.js", "ext:monaco-editor", EdgeKind.Uses));
            Assert.NotNull(FindEdge(graph, "js:main.js", "ext:@viz-js/viz", EdgeKind.Uses));
        }

        [Fact]
        public void AbsoluteImports_MatchWholeFileNames()
        {
            var graph = AnalyzeFiles(null,
                ("site/box.js", "export function f() { }\n"),
                ("lib/x.js", "export function f() { }\n"),
                ("site/main.js", "import { f } from '/x.js';\nf();\n"));

            Assert.NotNull(FindEdge(graph, "js:site/main.js", "js:lib/x.js", EdgeKind.Uses));
            Assert.Null(FindEdge(graph, "js:site/main.js", "js:site/box.js", EdgeKind.Uses));
        }

        [Fact]
        public void WindowObjects_AreListedByTheirFunctions_AndCalledFromOtherScripts()
        {
            var graph = AnalyzeFiles(null,
                ("view.js", "let cy = null;\nwindow.View = {\n  init(id) { cy = id; },\n  async render(data, options) { await draw(); },\n  version: 2\n};\nfunction draw() { }\n"),
                ("page.js", "document.addEventListener('load', () => View.init('x'));\nwindow.Start = function () { window.View.render(); };\n"));

            var view = Node(graph, "js:view.js");
            Assert.Equal(new[] { "cy", "View.init(id)", "View.render(data, options)", "draw()" }, view.Members.Select(m => m.Display));

            var calls = FindEdge(graph, "js:page.js", "js:view.js", EdgeKind.Calls);
            Assert.Equal(new[] { "(top level) → View.init()", "Start() → View.render()" }, calls!.Calls.Select(c => c.ToString()));
            Assert.Contains("render() → draw()", graph.Edges.Single(e => e.IsSelfLoop && e.SourceId == "js:view.js").Calls.Select(c => c.Caller.Replace("View.", "") + " → " + c.Callee));
        }

        [Fact]
        public void WindowObjects_SetInsideFunctionsAndMethods_AreGlobalsToo()
        {
            var graph = AnalyzeFiles(null,
                ("Page.razor.cs", """
                    using Microsoft.JSInterop;
                    public class Page
                    {
                        private IJSRuntime js;
                        public async Task Show()
                        {
                            await js.InvokeVoidAsync("Api.ping");
                            await js.InvokeVoidAsync("Tools.run");
                            await js.InvokeVoidAsync("Editor.open");
                            await js.InvokeVoidAsync("Loader.load");
                            await js.InvokeVoidAsync("Boot.start");
                        }
                    }
                    """),
                ("setup.js", """
                    function setup() { window.Api = { ping() { } }; }
                    const init = () => { window.Tools = { run() { } }; };
                    class Shell { start() { window.Editor = { open() { } }; } }
                    window.onload = function () { window.Loader = { load() { } }; };
                    const app = (function () { window.Boot = { start() { } }; return 1; })();
                    """));

            var calls = FindEdge(graph, "Page", "js:setup.js", EdgeKind.Calls);
            Assert.Equal(new[] { "Show() → Api.ping()", "Show() → Tools.run()", "Show() → Editor.open()", "Show() → Loader.load()", "Show() → Boot.start()" }, calls!.Calls.Select(c => c.ToString()));
        }

        [Fact]
        public void SelfInFunctions_IsTheGlobalObject_UnlessTheFileSetsItsOwn()
        {
            var graph = AnalyzeFiles(null,
                ("worker.js", "self.onmessage = function (e) { };\nfunction Job(el) {\n  var self = this;\n  self.el = el;\n  self.render = function () { };\n}\n"),
                ("setup.js", "function start() { self.Jobs = { run() { } }; }\n"));

            Assert.Equal(new[] { "onmessage(e)", "Job(el)" }, Node(graph, "js:worker.js").Members.Select(m => m.Display));
            Assert.Equal(new[] { "start()", "Jobs.run()" }, Node(graph, "js:setup.js").Members.Select(m => m.Display));
        }

        [Fact]
        public void BlazorInterop_LinksCSharpAndJavaScript_BothWays()
        {
            var graph = AnalyzeFiles(null,
                ("Page.razor.cs", """
                    using Microsoft.JSInterop;
                    public class Page
                    {
                        private IJSRuntime js;
                        public async Task Show() => await js.InvokeVoidAsync("Diagram.render", 1);
                        public async Task Setup() => await js.InvokeAsync<string>("window.setupEditor");
                        [JSInvokable] public void OnSelected(string id) { }
                    }
                    """),
                ("diagram.js", "let dotNet;\nwindow.Diagram = { render(data) { dotNet?.invokeMethodAsync('OnSelected', 'a'); } };\nwindow.setupEditor = () => 'ok';\n"));

            var toJs = FindEdge(graph, "Page", "js:diagram.js", EdgeKind.Calls);
            Assert.Equal(new[] { "Show() → Diagram.render()", "Setup() → setupEditor()" }, toJs!.Calls.Select(c => c.ToString()));

            var toCs = FindEdge(graph, "js:diagram.js", "Page", EdgeKind.Calls);
            Assert.Equal("Diagram.render() → OnSelected()", toCs!.Calls.Single().ToString());
        }

        [Fact]
        public void StringsCommentsTemplatesAndRegexes_DoNotConfuseTheScan()
        {
            var graph = AnalyzeFiles(null, ("tricky.js", """
                const pattern = /function fake\(\) {/g; // function alsoFake() {
                const text = `a ${ "}" + `nested ${ 1 }` } function notReal() {}`;
                /* class Nope { } */
                const s = 'it\'s { not a block';
                function real(a) { return a / 2 / 3; }
                """));

            var module = Node(graph, "js:tricky.js");
            Assert.Equal(new[] { "pattern", "text", "s", "real(a)" }, module.Members.Select(m => m.Display));
            Assert.Equal(4, module.Members[3].Line);
        }

        [Fact]
        public void RegexesInTemplates_DoNotSwallowTheRestOfTheFile()
        {
            var graph = AnalyzeFiles(null, ("clean.js", """
                const strip = s => `${ s.replace(/\{/g, '')
                }`;
                function after() { }
                """));

            var module = Node(graph, "js:clean.js");
            Assert.Equal(new[] { "strip(s)", "after()" }, module.Members.Select(m => m.Display));
            Assert.Equal(2, module.Members[1].Line);
        }

        [Theory]
        [InlineData("const x = new")]
        [InlineData("const x = new a.")]
        [InlineData("function f() { }\nlet")]
        [InlineData("var")]
        [InlineData("let a = 1,")]
        [InlineData("export { a as")]
        [InlineData("export default")]
        public void FilesCutOffAfterAKeyword_AreReadWithoutErrors(string content)
        {
            var graph = AnalyzeFiles(ShowEverything, ("cut.js", content));

            Assert.All(graph.Types.SelectMany(t => t.Members), m => Assert.False(string.IsNullOrEmpty(m.Display)));
            DotRenderer.Render(graph); // threw on a member without a name
        }

        [Fact]
        public void GraphData_ListsEveryCall_WithConstructorsNamedAsTheyAreCalled()
        {
            var graph = AnalyzeFiles(null,
                ("A.cs", "public class A\n{\n    public A() { Setup(); }\n    void Setup() { var b = new B(); b.Run(); }\n}\npublic class B\n{\n    public void Run() { }\n}\n"),
                ("c.js", "class C { constructor() { helper(); } }\nfunction helper() { }\n"));

            var calls = GraphData.FromGraph(graph).Calls.Select(c => $"{c.Source}.{c.Caller} → {c.Target}.{c.Callee}").ToList();
            Assert.Contains("A.new A() → A.Setup()", calls);   // within the type, a constructor
            Assert.Contains("A.Setup() → B.new B()", calls);
            Assert.Contains("A.Setup() → B.Run()", calls);
            Assert.Contains("js:c.js#C.new C() → js:c.js.helper()", calls);

            // Private functions too: the diagram opens them in the editor.
            Assert.Equal(new SourceLocation("A.cs", 3), Node(graph, "A").FunctionLocations["Setup()"]);
            Assert.Equal(2, Node(graph, "A").FunctionLocations["new A()"].Line);
            Assert.Equal(new SourceLocation("c.js", 0), Node(graph, "js:c.js#C").FunctionLocations["new C()"]);
            Assert.Equal(new SourceLocation("c.js", 1), Node(graph, "js:c.js").FunctionLocations["helper()"]);
        }

        [Fact]
        public void CSharpMembers_KnowWhereTheyAreDeclared()
        {
            var graph = AnalyzeFiles(null, ("A.cs", "public class A\n{\n    public int Count { get; set; }\n\n    public void Run() { }\n}\n"));

            var members = Node(graph, "A").Members;
            Assert.All(members, m => Assert.Equal("A.cs", m.FilePath));
            Assert.Equal(new[] { 2, 4 }, members.Select(m => m.Line));
        }
    }
}
