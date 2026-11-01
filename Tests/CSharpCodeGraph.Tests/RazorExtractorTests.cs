using CSharpCodeGraph.Diagram;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpCodeGraph.Tests
{
    public class RazorExtractorTests
    {
        private static void AssertValidCSharp(string code)
        {
            var errors = CSharpSyntaxTree.ParseText(code).GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
            Assert.Empty(errors);
        }

        // Extracts the Razor files the way the app does and analyzes them together with the C# files.
        private static CodeGraph AnalyzeProject(params SourceFile[] files)
        {
            var context = RazorProjectContext.Build(files);
            var sources = files
                .Where(f => !f.Path.EndsWith(".csproj"))
                .Select(f => RazorExtractor.IsRazorFile(f.Path) ? new SourceFile(f.Path, RazorExtractor.Extract(f.Content, f.Path, context)) : f)
                .ToList();
            return CodeAnalyzer.Analyze(sources);
        }

        // The code with each run of whitespace as one space, so a statement reads the same whatever its line breaks.
        private static string OneLine(string code) =>
            string.Join(" ", code.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        [Fact]
        public void DirectiveAttributes_DoNotBecomeMembers()
        {
            var razor = """
                <button @onclick="Save">Save</button>
                <input @bind="Name" @bind:event="oninput" />
                <Child @ref="child" @key="Name" />
                <p>@await LoadAsync()</p>
                @code {
                    private Child child = default!;
                    string Name = "";
                    void Save() { }
                    Task<int> LoadAsync() => Task.FromResult(1);
                }
                """;

            var code = RazorExtractor.Extract(razor, "Pages/Editor.razor");

            AssertValidCSharp(code);
            Assert.DoesNotContain("object onclick", code);
            Assert.DoesNotContain(" ref;", code);
            Assert.Contains("Save();", code);
            Assert.Contains("_ = LoadAsync();", code);
            Assert.Contains("__builder.OpenComponent<Child>(0);", code);
        }

        [Fact]
        public void Directives_AreAppliedToTheClass()
        {
            var razor = """
                @page "/orders"
                @using Shop.Services
                @inject IOrderService Orders;
                @inherits LayoutBase
                @implements IDisposable
                @typeparam TItem where TItem : class
                <h1>Orders</h1>
                @code { public void Dispose() { } }
                """;

            var code = RazorExtractor.Extract(razor, "Orders.razor");

            AssertValidCSharp(code);
            Assert.Contains("using Shop.Services;", code);
            Assert.Contains("public partial class Orders<TItem> : LayoutBase, IDisposable where TItem : class", code);
            Assert.Contains("[Inject] private IOrderService Orders { get; set; } = default!;", code);
        }

        [Fact]
        public void QuotesInsideAttributeExpressions_DoNotBreakTheTag()
        {
            var razor = """
                <label class="btn @(IsLoading ? "disabled" : "")" for="x">Upload</label>
                <FileTree OnUpload="Upload" />
                @code { bool IsLoading; void Upload() { } }
                """;

            var code = RazorExtractor.Extract(razor, "Panel.razor");

            AssertValidCSharp(code);
            Assert.Contains("_ = (IsLoading ? \"disabled\" : \"\");", code);
            Assert.Contains("__builder.OpenComponent<FileTree>(0);", code);
            Assert.Contains("Upload();", code);
        }

        [Fact]
        public void CodeBlocksWithBracesInStrings_AreReadCompletely()
        {
            var razor = """
                @code {
                    string Open = "{";
                    char Close = '}';
                    // a } in a comment
                    void After() { }
                }
                """;

            var code = RazorExtractor.Extract(razor, "Braces.razor");

            AssertValidCSharp(code);
            Assert.Contains("void After() { }", code);
        }

        [Fact]
        public void MarkupInCodeBlocks_StartsOnlyWhereAStatementCan()
        {
            var razor = """
                @{
                    var o = Repo.Load<Order>();
                    if (o.Total < Limit) Repo.Refresh();
                    var t = Compute();
                    <p>@t</p>
                    switch (o.State)
                    {
                        case 1:
                            <Badge Value="o.Total" />
                            break;
                    }
                    @:Total: @o.Total
                    <text>Done</text>
                }
                """;

            var code = RazorExtractor.Extract(razor, "Orders.razor");

            AssertValidCSharp(code);
            Assert.DoesNotContain("OpenComponent<Order>", code);
            Assert.Contains("var o = Repo.Load<Order>();", code);
            Assert.Contains("if (o.Total < Limit) Repo.Refresh();", code);
            Assert.Contains("var t = Compute();", code);
            Assert.Contains("_ = t;", code);
            Assert.Contains("__builder.OpenComponent<Badge>(0);", code);
            Assert.Contains("_ = o.Total;", code);
        }

        [Fact]
        public void GenericsInCodeBlocks_AreCalls_NotChildComponents()
        {
            var graph = AnalyzeProject(
                new SourceFile("App/App.csproj", "<Project />"),
                new SourceFile("App/Models.cs", "namespace App { public class Order { } public class Repository { public T Load<T>() => default!; public void Refresh() { } } }"),
                new SourceFile("App/OrderPage.razor", "@inject Repository Repo\n@{ var o = Repo.Load<Order>(); Repo.Refresh(); }"));

            Assert.DoesNotContain(graph.Edges, e => e.Kind == EdgeKind.Renders);
            var calls = Assert.Single(graph.Edges, e => e.SourceId == "App.OrderPage" && e.TargetId == "App.Repository" && e.Kind == EdgeKind.Calls);
            Assert.Equal(new[] { "BuildRenderTree() → Load()", "BuildRenderTree() → Refresh()" }, calls.Calls.Select(c => c.ToString()));
        }

        [Fact]
        public void ControlFlowBlocks_AreCodeBlocks_WithTheirElseChainsAndCases()
        {
            var razor = """
                @if (Repo.Count() > 0)
                {
                    var y = Compute();
                    <p>@y</p>
                }
                else if (Repo.Count() < 0)
                {
                    Warn();
                    @Message
                }
                else
                {
                    @:None
                }
                @foreach (var item in Repo.Items())
                {
                    var label = Format(item);
                    @if (item.Done)
                    {
                        <s>@label</s>
                    }
                    else
                    {
                        <Badge Text="label" />
                    }
                }
                @switch (Mode)
                {
                    case 1:
                        <p>One</p>
                        Track(1);
                        break;
                    default:
                        @(Mode + 1)
                        break;
                }
                """;

            var code = RazorExtractor.Extract(razor, "Orders.razor");
            var oneLine = OneLine(code);

            AssertValidCSharp(code);
            Assert.Contains("if (Repo.Count() > 0) { var y = Compute(); } else if (Repo.Count() < 0) { Warn(); } else { }", oneLine);
            Assert.Contains("foreach (var item in Repo.Items()) { var label = Format(item); if (item.Done) { } else { } }", oneLine);
            Assert.Contains("switch (Mode) { case 1: Track(1); break; default: break; }", oneLine);
            Assert.Contains("_ = y;", code);
            Assert.Contains("_ = Message;", code);
            Assert.Contains("_ = label;", code);
            Assert.Contains("__builder.OpenComponent<Badge>(0);", code);
            Assert.Contains("_ = (Mode + 1);", code);
        }

        [Fact]
        public void LoopLockUsingAndTryBlocks_AreCodeBlocks()
        {
            var razor = """
                @for (var i = 0; i < Rows; i++)
                {
                    <tr>@i</tr>
                    Count(i);
                }
                @while (Pending())
                {
                    Next();
                }
                @do
                {
                    <p>Retrying</p>
                    Retry();
                } while (Failed());
                @lock (Sync)
                {
                    Enter();
                }
                @using (var scope = Open())
                {
                    Use(scope);
                }
                @try
                {
                    Load();
                }
                catch (Exception ex) when (Log(ex))
                {
                    <p>@ex.Message</p>
                }
                finally
                {
                    Close();
                }
                """;

            var code = RazorExtractor.Extract(razor, "Jobs.razor");
            var oneLine = OneLine(code);

            AssertValidCSharp(code);
            Assert.Contains("for (var i = 0; i < Rows; i++) { Count(i); }", oneLine);
            Assert.Contains("while (Pending()) { Next(); }", oneLine);
            Assert.Contains("do { Retry(); } while (Failed());", oneLine);
            Assert.Contains("lock (Sync) { Enter(); }", oneLine);
            Assert.Contains("using (var scope = Open()) { Use(scope); }", oneLine);
            Assert.Contains("try { Load(); } catch (Exception ex) when (Log(ex)) { } finally { Close(); }", oneLine);
            Assert.Contains("_ = ex.Message;", code);
        }

        [Fact]
        public void CallsInControlFlowBlocks_AreRecorded()
        {
            var graph = AnalyzeProject(
                new SourceFile("App/App.csproj", "<Project />"),
                new SourceFile("App/Calculator.cs", "namespace App { public class Calculator { public int Compute() => 1; } }"),
                new SourceFile("App/Summary.razor", "@inject Calculator Calc\n@if (Visible)\n{\n    var y = Calc.Compute();\n    <p>@y</p>\n}\n@code { bool Visible; }"));

            var calls = Assert.Single(graph.Edges, e => e.SourceId == "App.Summary" && e.TargetId == "App.Calculator" && e.Kind == EdgeKind.Calls);
            Assert.Equal("BuildRenderTree() → Compute()", Assert.Single(calls.Calls).ToString());
        }

        [Fact]
        public void ControlFlowWithoutABlock_KeepsItsCondition()
        {
            var code = RazorExtractor.Extract("@if (Ready()) Start();\n<p>After</p>", "Start.razor");

            AssertValidCSharp(code);
            Assert.Contains("if (Ready()) { }", code);
        }

        [Fact]
        public void TemplatesInsideCode_AreScannedAsMarkup_AndLeaveValidCSharp()
        {
            var razor = """
                @code {
                    private RenderFragment RenderRow(Item item) => @<div class="row">
                        <input type="checkbox" checked="@item.Done" />
                        <Badge Value="item.Count" />
                        <button @onclick="() => Remove(item)">x</button>
                    </div>;

                    void Remove(Item item) { }
                }
                """;

            var code = RazorExtractor.Extract(razor, "List.razor");

            AssertValidCSharp(code);
            Assert.Contains("RenderRow(Item item) => default!;", code);
            Assert.Contains("__builder.OpenComponent<Badge>(0);", code);
            Assert.Contains("_ = () => Remove(item);", code);
            Assert.Contains("void Remove(Item item) { }", code);
        }

        [Fact]
        public void CodeBlocksWithTemplatesInCommentsAndStrings_AreReadCompletely()
        {
            var razor = """
                @code {
                    // Wraps rows in @<div> elements
                    private List<Row> rows = new();
                    /* One @<li> per row */
                    private string Help = "Type @<b> for bold";
                    private string Italic(string s) => '"' + s + "@<i>" + '"';
                    private RenderFragment Empty => @<p>No rows</p>;
                    void Save() { }
                }
                """;

            var code = RazorExtractor.Extract(razor, "Rows.razor");

            AssertValidCSharp(code);
            Assert.Contains("// Wraps rows in @<div> elements", code);
            Assert.Contains("private List<Row> rows = new();", code);
            Assert.Contains("private string Help = \"Type @<b> for bold\";", code);
            Assert.Contains("'\"' + s + \"@<i>\" + '\"';", code);
            Assert.Contains("Empty => default!;", code);
            Assert.Contains("void Save() { }", code);
        }

        [Fact]
        public void ComparisonsAndGenericsInMarkupExpressions_AreNotTags()
        {
            var razor = """
                @{
                    <p>@(Count<Limit ? "few" : "many")</p>
                    Track();
                }
                @code {
                    private RenderFragment Row(Item item) => @<tr>
                        <td>@if (item.Count<Limit) { <Warning /> }</td>
                        <td>@{ var tags = item.Tags.OfType<Tag>(); }</td>
                    </tr>;

                    void Save() { }
                }
                """;

            var code = RazorExtractor.Extract(razor, "Rows.razor");
            var oneLine = OneLine(code);

            AssertValidCSharp(code);
            Assert.Contains("Track();", code);
            Assert.Contains("_ = (Count<Limit ? \"few\" : \"many\");", code);
            Assert.Contains("Row(Item item) => default!;", code);
            Assert.Contains("if (item.Count<Limit) { }", oneLine);
            Assert.Contains("{ var tags = item.Tags.OfType<Tag>(); }", oneLine);
            Assert.Contains("__builder.OpenComponent<Warning>(0);", code);
            Assert.DoesNotContain("OpenComponent<Limit>", code);
            Assert.DoesNotContain("OpenComponent<Tag>", code);
            Assert.Contains("void Save() { }", code);
        }

        [Fact]
        public void ImportsFile_ProducesNoClass()
        {
            Assert.Equal(string.Empty, RazorExtractor.Extract("@using System.Net.Http", "_Imports.razor"));
        }

        [Fact]
        public void Namespace_ComesFromCodeBehind_ThenImports_ThenProject()
        {
            var context = RazorProjectContext.Build(new[]
            {
                new SourceFile("src/App/App.csproj", "<Project><PropertyGroup><RootNamespace>My.App</RootNamespace></PropertyGroup></Project>"),
                new SourceFile("src/App/Admin/_Imports.razor", "@namespace My.App.Backoffice\n@using My.App.Services"),
                new SourceFile("src/App/Pages/Index.razor.cs", "namespace Custom.Place { public partial class Index { } }")
            });

            Assert.Equal("Custom.Place", context.GetNamespace("src/App/Pages/Index.razor"));
            Assert.Equal("My.App.Backoffice.Users", context.GetNamespace("src/App/Admin/Users/List.razor"));
            Assert.Equal("My.App.Shared", context.GetNamespace("src/App/Shared/NavMenu.razor"));
            Assert.Equal(new[] { "My.App.Services" }, context.GetImportedUsings("src/App/Admin/Users/List.razor"));
        }

        [Fact]
        public void InheritsAndInjects_ComeFromTheComponent_ThenTheNearestImports()
        {
            var context = RazorProjectContext.Build(new[]
            {
                new SourceFile("App/_Imports.razor", "@inherits AppBase\n@implements IAuditable\n@inject IOrderService Orders\n@inject ILogger Log"),
                new SourceFile("App/Admin/_Imports.razor", "@inherits AdminBase\n@inject IAuditLog Log")
            });

            var home = RazorExtractor.Extract("<h1>Home</h1>", "App/Home.razor", context);
            var users = RazorExtractor.Extract("<h1>Users</h1>", "App/Admin/Users/List.razor", context);
            var layout = RazorExtractor.Extract("@inherits LayoutComponentBase\n@inject IOrderQueries Orders\n@Body", "App/Admin/AdminLayout.razor", context);

            AssertValidCSharp(users);
            Assert.Contains("public partial class Home : AppBase, IAuditable", home);
            Assert.Contains("[Inject] private ILogger Log { get; set; } = default!;", home);

            Assert.Contains("public partial class List : AdminBase, IAuditable", users);
            Assert.Contains("[Inject] private IOrderService Orders { get; set; } = default!;", users);
            Assert.Contains("[Inject] private IAuditLog Log { get; set; } = default!;", users);
            Assert.DoesNotContain("ILogger", users);

            Assert.Contains("public partial class AdminLayout : LayoutComponentBase, IAuditable", layout);
            Assert.Contains("[Inject] private IOrderQueries Orders { get; set; } = default!;", layout);
            Assert.DoesNotContain("IOrderService", layout);
        }

        [Fact]
        public void ImportsFile_InjectsAndInherits_IntoTheComponentsBelowIt()
        {
            var graph = AnalyzeProject(
                new SourceFile("App/App.csproj", "<Project />"),
                new SourceFile("App/_Imports.razor", "@inject IOrderService Orders\n@inherits AppComponentBase"),
                new SourceFile("App/Services.cs", "namespace App { public interface IOrderService { void Load(); } public class AppComponentBase { } }"),
                new SourceFile("App/Pages/OrderList.razor", "@code { void Reload() => Orders.Load(); }"));

            Assert.Contains(graph.Edges, e => e.SourceId == "App.Pages.OrderList" && e.TargetId == "App.AppComponentBase" && e.Kind == EdgeKind.Inherits);
            var calls = Assert.Single(graph.Edges, e => e.SourceId == "App.Pages.OrderList" && e.TargetId == "App.IOrderService" && e.Kind == EdgeKind.Calls);
            Assert.Equal("Reload() → Load()", Assert.Single(calls.Calls).ToString());
        }

        [Fact]
        public void Component_MergesWithItsCodeBehind_AndRendersChildComponents()
        {
            var files = new[]
            {
                new SourceFile("App/App.csproj", "<Project />"),
                new SourceFile("App/Pages/Index.razor", "<Counter OnChanged=\"Refresh\" />"),
                new SourceFile("App/Pages/Index.razor.cs", "namespace App.Pages { public partial class Index { public void Refresh() { } } }"),
                new SourceFile("App/Shared/Counter.razor", "<button @onclick=\"Increment\">+</button>\n@code { public void Increment() { } }")
            };
            var context = RazorProjectContext.Build(files);

            var sources = files
                .Where(f => !f.Path.EndsWith(".csproj"))
                .Select(f => RazorExtractor.IsRazorFile(f.Path) ? new SourceFile(f.Path, RazorExtractor.Extract(f.Content, f.Path, context)) : f)
                .ToList();
            var graph = CodeAnalyzer.Analyze(sources);

            var index = Assert.Single(graph.Types, t => t.Name == "Index");
            Assert.Equal("App.Pages.Index", index.Id);
            Assert.Contains(index.Members, m => m.Display == "void Refresh()");
            Assert.Contains(graph.Edges, e => e.SourceId == "App.Pages.Index" && e.TargetId == "App.Shared.Counter" && e.Kind == EdgeKind.Renders);
        }
    }
}
