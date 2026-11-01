using System.Diagnostics;
using System.Text;
using CSharpCodeGraph.Diagram;
using Xunit;

namespace CSharpCodeGraph.Tests
{
    public class CodeAnalyzerTests
    {
        private static readonly DiagramSettings ShowEverything = new() { OnlyShowMyCode = false };

        private static CodeGraph Analyze(string source, DiagramSettings? settings = null) =>
            CodeAnalyzer.Analyze(source, settings);

        private static CodeGraph AnalyzeFiles(DiagramSettings? settings, params (string Path, string Content)[] files) =>
            CodeAnalyzer.Analyze(files.Select(f => new SourceFile(f.Path, f.Content)).ToList(), settings);

        private static GraphEdge? FindEdge(CodeGraph graph, string source, string target, EdgeKind kind) =>
            graph.Edges.SingleOrDefault(e => e.SourceId == source && e.TargetId == target && e.Kind == kind);

        private static TypeNode Node(CodeGraph graph, string id) => graph.Types.Single(t => t.Id == id);


        [Fact]
        public void ExternalCall_WithExternalCodeShown_DoesNotLinkToEveryType()
        {
            var graph = Analyze("class A { public void Run() { System.Console.WriteLine(1); } } class B { } class C { }", ShowEverything);

            var calls = graph.Edges.Where(e => e.Kind == EdgeKind.Calls).ToList();
            var call = Assert.Single(calls);
            Assert.Equal("A", call.SourceId);
            Assert.Equal("ext:Console", call.TargetId);
        }

        [Fact]
        public void FileScopedNamespaces_InSeparateFiles_AreNotNested()
        {
            var graph = AnalyzeFiles(null,
                ("User.cs", "namespace App.Models;\npublic class User { }\n"),
                ("UserService.cs", "using App.Models;\nnamespace App.Services;\npublic class UserService { public User Get() => null; }\n"));

            Assert.Contains(graph.Types, t => t.Id == "App.Models.User");
            Assert.Contains(graph.Types, t => t.Id == "App.Services.UserService");
            Assert.NotNull(FindEdge(graph, "App.Services.UserService", "App.Models.User", EdgeKind.Uses));
        }

        [Fact]
        public void BaseList_DistinguishesInterfacesFromBaseClasses()
        {
            var graph = Analyze("interface IRepo { } class Base { } class Repo : Base, IRepo { } class Svc : System.IDisposable { public void Dispose() { } }", ShowEverything);

            Assert.NotNull(FindEdge(graph, "Repo", "Base", EdgeKind.Inherits));
            Assert.NotNull(FindEdge(graph, "Repo", "IRepo", EdgeKind.Implements));
            Assert.NotNull(FindEdge(graph, "Svc", "ext:IDisposable", EdgeKind.Implements));
        }

        [Fact]
        public void PartialClass_IsOneNodeWithAllMembers()
        {
            var graph = Analyze("namespace N { public partial class P { public int A { get; set; } } } namespace N { public partial class P { public void M() { } } }");

            var node = Assert.Single(graph.Types);
            Assert.Equal(new[] { "int A", "void M()" }, node.Members.Select(m => m.Display));
        }

        [Fact]
        public void Call_IsResolvedByReceiverType_NotByMethodName()
        {
            var graph = Analyze("class Cart { public void Add(int x) { } } class Report { void Build() { var list = new List<int>(); list.Add(1); new Cart().Add(2); } }");

            var edge = Assert.Single(graph.Edges, e => e.Kind == EdgeKind.Calls);
            Assert.Equal(("Report", "Cart"), (edge.SourceId, edge.TargetId));
            Assert.Equal(new[] { "Build() → Add()", "Build() → new Cart()" }, edge.Calls.Select(c => $"{c.Caller} → {c.Callee}"));
        }

        [Fact]
        public void UnresolvableCall_DoesNotFallBackToNameMatching()
        {
            // MyMethod2 only exists on MyClass, so the unqualified call from MyClass2 doesn't compile and isn't an edge.
            var graph = Analyze("class MyClass { public void MyMethod2() { } } class MyClass2 { public void MyMethod3() { MyMethod2(); } }");

            Assert.DoesNotContain(graph.Edges, e => e.Kind == EdgeKind.Calls);
        }

        [Fact]
        public void GenericAndArrayMemberTypes_CountAsDependencies()
        {
            var graph = Analyze("class Order { } class Line { } class Shop { public List<Order> Orders { get; set; } public Line[] Lines; }");

            Assert.NotNull(FindEdge(graph, "Shop", "Order", EdgeKind.Uses));
            Assert.NotNull(FindEdge(graph, "Shop", "Line", EdgeKind.Uses));
        }

        [Fact]
        public void ConstructorParameters_CountAsDependencies()
        {
            var graph = Analyze("interface IRepo { } class Service { public Service(IRepo repo) { } } class Primary(IRepo repo) { }");

            Assert.NotNull(FindEdge(graph, "Service", "IRepo", EdgeKind.Uses));
            Assert.NotNull(FindEdge(graph, "Primary", "IRepo", EdgeKind.Uses));
        }

        [Fact]
        public void ManyCallsBetweenTwoTypes_AreOneEdge()
        {
            var graph = Analyze("class A { public void X() { } public void Y() { } } class B { void P(A a) { a.X(); a.Y(); a.X(); } void Q(A a) { a.X(); } }");

            var edge = Assert.Single(graph.Edges, e => e.Kind == EdgeKind.Calls);
            Assert.Equal(3, edge.Calls.Count);
        }

        [Fact]
        public void InterfaceMembers_AreShownWithoutAnExplicitPublicModifier()
        {
            var graph = Analyze("interface IRepo { void Save(); }");

            Assert.Equal(new[] { "void Save()" }, Node(graph, "IRepo").Members.Select(m => m.Display));
        }

        [Fact]
        public void EnumsAndRecords_AreNodes()
        {
            var graph = Analyze("enum Status { On, Off } record Person(string Name, Status Status);");

            Assert.Equal(NodeKind.Enum, Node(graph, "Status").Kind);
            Assert.Equal(new[] { "On", "Off" }, Node(graph, "Status").Members.Select(m => m.Display));
            Assert.Equal(NodeKind.Record, Node(graph, "Person").Kind);
            Assert.NotNull(FindEdge(graph, "Person", "Status", EdgeKind.Uses));
        }

        [Fact]
        public void HiddenInterfaces_DoNotReappearAsExternalTypes()
        {
            var settings = new DiagramSettings { ShowInterfaces = false, OnlyShowMyCode = false };
            var graph = Analyze("interface IRepo { } class Repo : IRepo { public IRepo Self() => this; }", settings);

            Assert.Equal(new[] { "Repo" }, graph.Types.Select(t => t.Id));
            Assert.Empty(graph.Edges);
        }

        [Fact]
        public void ShowVariablesOff_HidesPropertiesAndTheirDependencies()
        {
            var settings = new DiagramSettings { ShowVariables = false };
            var graph = Analyze("class Order { } class Shop { public Order Current { get; set; } public void Run() { } }", settings);

            Assert.Equal(new[] { "void Run()" }, Node(graph, "Shop").Members.Select(m => m.Display));
            Assert.Null(FindEdge(graph, "Shop", "Order", EdgeKind.Uses));
        }

        [Fact]
        public void Nodes_KnowWhereTheyAreDeclared()
        {
            var graph = AnalyzeFiles(null,
                ("A.cs", "namespace N;\n\npublic class A { }\n"),
                ("B.cs", "namespace N;\npublic partial class B { }\n"),
                ("B.Part.cs", "namespace N;\n\n\npublic partial class B { }\n"));

            Assert.Equal(("A.cs", 2), (Node(graph, "N.A").FilePath, Node(graph, "N.A").Line));
            // A partial type points at its first declaration.
            Assert.Equal(("B.cs", 1), (Node(graph, "N.B").FilePath, Node(graph, "N.B").Line));
        }

        [Fact]
        public void CodeThatDoesNotParse_ProducesNoNamelessNodes()
        {
            var graph = Analyze("public class A { private RenderFragment Row => @<div class=\"row\">@Name</div>; } public class B { }");

            Assert.All(graph.Types, t => Assert.False(string.IsNullOrEmpty(t.Id)));
            Assert.Contains(graph.Types, t => t.Id == "B");
        }

        [Fact]
        public async Task AnalyzeAsync_GivesTheSameGraphAsAnalyze()
        {
            const string source = "interface I { } class A : I { public void X() { new B().Y(); } } class B { public void Y() { } }";
            var files = SourceBundle.Split(source);

            var expected = CodeAnalyzer.Analyze(files);
            var reports = new List<string>();
            var actual = await CodeAnalyzer.AnalyzeAsync(files, progress: new Progress<string>(reports.Add));

            Assert.Equal(expected.Types.Select(t => t.Id), actual.Types.Select(t => t.Id));
            Assert.Equal(expected.Edges.Select(e => (e.SourceId, e.TargetId, e.Kind)), actual.Edges.Select(e => (e.SourceId, e.TargetId, e.Kind)));
        }

        [Fact]
        public async Task AnalyzeAsync_CanBeCancelled()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CodeAnalyzer.AnalyzeAsync(SourceBundle.Split("class A { }"), cancellationToken: cts.Token));
        }

        [Fact]
        public void TopLevelStatements_BecomeAProgramNode_WithItsCalls()
        {
            var graph = AnalyzeFiles(null,
                ("Program.cs", "using App;\n\nvar runner = new Runner();\nrunner.Run();\n"),
                ("Runner.cs", "namespace App;\npublic class Runner { public void Run() { } }\n"));

            var program = Node(graph, "Program");
            Assert.Equal(("Program.cs", 2), (program.FilePath, program.Line));

            var calls = Assert.Single(graph.Edges, e => e.SourceId == "Program" && e.Kind == EdgeKind.Calls);
            Assert.Equal("App.Runner", calls.TargetId);
            Assert.Equal(new[] { "Main() → new Runner()", "Main() → Run()" }, calls.Calls.Select(c => c.ToString()));
        }

        [Fact]
        public void TopLevelServiceRegistrations_AreDependencies()
        {
            var graph = AnalyzeFiles(null,
                ("Program.cs", "using App;\nvar builder = WebApplication.CreateBuilder(args);\nbuilder.Services.AddScoped<IRepo, SqlRepo>();\nbuilder.Services.AddHostedService<Worker>();\nvar t = typeof(Options);\n"),
                ("App.cs", "namespace App; public interface IRepo { } public class SqlRepo : IRepo { } public class Worker { } public class Options { }"));

            foreach (var target in new[] { "App.IRepo", "App.SqlRepo", "App.Worker", "App.Options" })
                Assert.NotNull(FindEdge(graph, "Program", target, EdgeKind.Uses));
        }

        [Fact]
        public void TopLevelStatements_MergeWithADeclaredPartialProgram()
        {
            var graph = AnalyzeFiles(null,
                ("Program.cs", "System.Console.WriteLine(1);\n"),
                ("Program.Partial.cs", "public partial class Program { public static int Answer => 42; }\n"));

            var program = Assert.Single(graph.Types, t => t.Name == "Program");
            Assert.Contains(program.Members, m => m.Display == "int Answer");
        }

        [Fact]
        public void Delegates_AreNodes_WithTheirSignature()
        {
            var graph = Analyze("public class Order { } public delegate void OrderPlaced(Order order); public class Shop { public event OrderPlaced Placed; }");

            var placed = Node(graph, "OrderPlaced");
            Assert.Equal(NodeKind.Delegate, placed.Kind);
            Assert.Equal(new[] { "void Invoke(Order order)" }, placed.Members.Select(m => m.Display));
            Assert.NotNull(FindEdge(graph, "OrderPlaced", "Order", EdgeKind.Uses));
            Assert.NotNull(FindEdge(graph, "Shop", "OrderPlaced", EdgeKind.Uses));
        }

        [Fact]
        public void TypesThatPrintTheSame_GetIdsOfTheirOwn()
        {
            var graph = AnalyzeFiles(null,
                ("A/User.cs", "namespace App.Models; public class User { }"),
                ("B/User.cs", "namespace App.Models; public record User { }"),
                ("Orders.cs", "namespace Shop.Orders { public class Item { } } namespace Shop { public class Orders { public class Item { } } }"));

            var ids = graph.Types.Select(t => t.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.Equal(2, graph.Types.Count(t => t.Name == "User"));
            GraphData.FromGraph(graph); // threw on the repeated id
        }

        [Fact]
        public void TopLevelMain_IsAtTheFirstStatement()
        {
            // The partial class comes first, so Program's node is there; Main() is the statements.
            var graph = AnalyzeFiles(null,
                ("Program.Partial.cs", "public partial class Program\n{\n    static void Log() { }\n}\n"),
                ("Program.cs", "using App;\n\nvar runner = new Runner();\nrunner.Run();\n"),
                ("Runner.cs", "namespace App;\npublic class Runner { public void Run() { } }\n"));

            Assert.Equal(new SourceLocation("Program.cs", 2), Node(graph, "Program").FunctionLocations["Main()"]);
        }

        [Fact]
        public void ConstructorsThatAreNotWrittenOut_AreAtTheTypesDeclaration()
        {
            var graph = AnalyzeFiles(null, ("Models.cs", "namespace App;\n\npublic record Person(string Name);\n\npublic class Service(Person owner) { }\n\npublic class Plain\n{\n}\n"));

            Assert.Equal(new SourceLocation("Models.cs", 2), Node(graph, "App.Person").FunctionLocations["new Person()"]);
            Assert.Equal(new SourceLocation("Models.cs", 4), Node(graph, "App.Service").FunctionLocations["new Service()"]);
            Assert.Equal(new SourceLocation("Models.cs", 6), Node(graph, "App.Plain").FunctionLocations["new Plain()"]);
        }

        [Fact]
        public void AStaticConstructor_IsNotTheOneNewCalls()
        {
            var graph = AnalyzeFiles(null,
                ("Cache.cs", "public class Cache\n{\n    static Cache() { Load(); }\n    static void Load() { }\n}\n"),
                ("Program.cs", "public class Program { void Run() { new Cache(); } }\n"));

            var cache = Node(graph, "Cache");
            Assert.Equal(new SourceLocation("Cache.cs", 0), cache.FunctionLocations["new Cache()"]);
            Assert.Equal(2, cache.FunctionLocations["static Cache()"].Line);
            Assert.Contains("static Cache() → Load()", graph.Edges.Single(e => e.IsSelfLoop && e.SourceId == "Cache").Calls.Select(c => c.ToString()));
        }

        [Fact]
        public void AttributesAboveAMember_DoNotMoveItsFunctionLocation()
        {
            var graph = Analyze("""
                class A
                {
                    [Obsolete]
                    [Browsable(false)]
                    public int Total => Compute();
                    [Obsolete]
                    public int this[int i] => Compute();
                    [Obsolete]
                    public event Action Changed { add { Compute(); } remove { } }
                    [Obsolete]
                    public event Action Clicked;
                    [Obsolete]
                    static int cached = Compute();
                    static int Compute() => 0;
                }
                """);

            var locations = Node(graph, "A").FunctionLocations;
            Assert.Equal(new[] { 4, 6, 8, 10, 12 }, new[] { "Total", "this[]", "Changed", "Clicked", "cached" }.Select(name => locations[name].Line));
        }

        [Fact]
        public void UnicodeLineBreaks_DoNotShiftLineNumbers()
        {
            // Roslyn also breaks lines at U+0085, U+2028 and U+2029; the editor (Monaco) only at \r\n, \r and \n.
            var graph = AnalyzeFiles(null, ("A.cs", "/* caf\u0085e */\r\nclass A\r{\n    // \u2028 \u2029\r\n    public void Run() { }\n}\n"));

            var a = Node(graph, "A");
            Assert.Equal(1, a.Line);
            Assert.Equal(4, Assert.Single(a.Members).Line);
            Assert.Equal(4, a.FunctionLocations["Run()"].Line);
        }

        [Fact]
        public void ObjectsCreatedThroughAUsingAlias_AreCalls()
        {
            var graph = AnalyzeFiles(null,
                ("OrderRepository.cs", "namespace App.Data;\npublic class OrderRepository { }\n"),
                ("Usings.cs", "global using Orders = App.Data.OrderRepository;\n"),
                ("Shop.cs", "using Repo = App.Data.OrderRepository;\npublic class Shop { void Load() { new Repo(); } void Reload() { new Orders(); } }\n"));

            var calls = Assert.Single(graph.Edges, e => e.Kind == EdgeKind.Calls);
            Assert.Equal(("Shop", "App.Data.OrderRepository"), (calls.SourceId, calls.TargetId));
            Assert.Equal(new[] { "Load() → new OrderRepository()", "Reload() → new OrderRepository()" }, calls.Calls.Select(c => c.ToString()));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void VerbatimIdentifiers_AreNamedWithoutTheirAtSign(bool onlyShowMyCode)
        {
            var graph = Analyze("class @class { }\nclass A\n{\n    void @event() { Run(); }\n    void Run() { @event(); new @class(); }\n}\n", new DiagramSettings { OnlyShowMyCode = onlyShowMyCode });

            var calls = graph.Edges.Where(e => e.Kind == EdgeKind.Calls).SelectMany(e => e.Calls).Select(c => c.ToString());
            Assert.Equal(new[] { "event() → Run()", "Run() → event()", "Run() → new class()" }, calls);
            Assert.Equal(3, Node(graph, "A").FunctionLocations["event()"].Line);
        }

        [Fact]
        public void InvokeMethodAsync_CallsTheMethodWithThatJSInvokableIdentifier()
        {
            var graph = AnalyzeFiles(null,
                ("Page.cs", """
                    using Microsoft.JSInterop;
                    public class Page
                    {
                        [JSInvokable("onSelected")] public void HandleSelection() { }
                        [JSInvokable("Notify")] public void OnNotify() { }
                        [JSInvokable] public void Refresh() { }
                    }
                    public class Mailer { public void Notify() { } }
                    """),
                ("view.js", "window.View = { init(dotNet) { dotNet.invokeMethodAsync('onSelected'); dotNet.invokeMethodAsync('Notify'); dotNet.invokeMethodAsync('Refresh'); } };\n"));

            var calls = FindEdge(graph, "js:view.js", "Page", EdgeKind.Calls);
            Assert.Equal(new[] { "View.init() → HandleSelection()", "View.init() → OnNotify()", "View.init() → Refresh()" }, calls!.Calls.Select(c => c.ToString()));
            Assert.Null(FindEdge(graph, "js:view.js", "Mailer", EdgeKind.Calls));
        }

        [Fact]
        public void InteropCallsOnAModuleReference_DoNotGoToGlobalFunctions()
        {
            var graph = AnalyzeFiles(null,
                ("Chart.cs", """
                    using Microsoft.JSInterop;
                    public class Chart
                    {
                        private IJSRuntime? js;
                        private IJSObjectReference? module;
                        private IJSInProcessObjectReference? inProcessModule;
                        public async Task Draw()
                        {
                            module = await js!.InvokeAsync<IJSObjectReference>("import", "./chart.js");
                            await module.InvokeVoidAsync("render");
                            var local = await js.InvokeAsync<IJSObjectReference>("import", "./chart.js");
                            await local.InvokeVoidAsync("render");
                            inProcessModule!.InvokeVoid("render");
                            // Calls on the runtime itself do go to globals.
                            await js.InvokeVoidAsync("setup");
                            ((IJSInProcessRuntime)js).InvokeVoid("resize");
                        }
                    }
                    """),
                ("chart.js", "export function render() { }\n"),
                ("global.js", "window.render = () => { };\nwindow.setup = () => { };\nwindow.resize = () => { };\n"));

            var calls = Assert.Single(graph.Edges, e => e.Kind == EdgeKind.Calls);
            Assert.Equal(("Chart", "js:global.js"), (calls.SourceId, calls.TargetId));
            Assert.Equal(new[] { "Draw() → setup()", "Draw() → resize()" }, calls.Calls.Select(c => c.ToString()));
        }

        [Fact]
        public void LargeProject_IsAnalyzedQuickly()
        {
            const int typeCount = 300;
            var random = new Random(1);
            var files = new List<SourceFile>();
            for (int i = 0; i < typeCount; i++)
            {
                var sb = new StringBuilder($"namespace N{i % 15};\npublic class T{i} {{\n");
                for (int m = 0; m < 6; m++)
                {
                    sb.Append($"  public void M{i}_{m}() {{");
                    for (int c = 0; c < 4; c++)
                    {
                        int t = random.Next(typeCount);
                        sb.Append($" new N{t % 15}.T{t}().M{t}_{random.Next(6)}(); System.Console.WriteLine();");
                    }
                    sb.AppendLine(" }");
                }
                sb.AppendLine("}");
                files.Add(new SourceFile($"T{i}.cs", sb.ToString()));
            }

            var stopwatch = Stopwatch.StartNew();
            var graph = CodeAnalyzer.Analyze(files);
            stopwatch.Stop();

            Assert.Equal(typeCount, graph.Types.Count);
            // Calls are aggregated per pair of types: at most one call edge per type pair.
            Assert.True(graph.Edges.Count(e => e.Kind == EdgeKind.Calls) <= typeCount * 24);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Took {stopwatch.Elapsed}");
        }
    }
}
