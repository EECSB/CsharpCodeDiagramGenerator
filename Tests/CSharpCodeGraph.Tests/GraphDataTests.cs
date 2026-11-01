using CSharpCodeGraph.Diagram;
using Xunit;

namespace CSharpCodeGraph.Tests
{
    public class GraphDataTests
    {
        [Fact]
        public void Nodes_CarryKindStereotypeAndMembersInUmlOrder()
        {
            var graph = CodeAnalyzer.Analyze("namespace Shop { public record struct Money(decimal Amount); public static class Helpers { public static void Run() { } public static int Count { get; set; } } }");

            var data = GraphData.FromGraph(graph);

            var money = data.Nodes.Single(n => n.Id == "Shop.Money");
            Assert.Equal(("recordStruct", "«record struct»", "Shop"), (money.Kind, money.Stereotype, money.Namespace));

            var helpers = data.Nodes.Single(n => n.Id == "Shop.Helpers");
            Assert.Equal(new[] { "int Count", "void Run()" }, helpers.Members);
        }

        [Fact]
        public void Edges_LeaveOutSelfCalls_AndCountAggregatedCalls()
        {
            var graph = CodeAnalyzer.Analyze("class A { public void X() { Y(); } public void Y() { } } class B { void P(A a) { a.X(); a.Y(); } }");

            var data = GraphData.FromGraph(graph);

            Assert.DoesNotContain(data.Edges, e => e.Source == e.Target);
            var edge = Assert.Single(data.Edges, e => e.Kind == "calls");
            Assert.Equal(("B", "A", 2, "2 calls"), (edge.Source, edge.Target, edge.Count, edge.Label));
        }

        [Fact]
        public void EveryEdgeEnd_IsANode()
        {
            var graph = CodeAnalyzer.Analyze("interface I { } class Base { } class A : Base, I, System.IDisposable { public List<Base> Items { get; set; } public void Dispose() { System.Console.WriteLine(); } }",
                new DiagramSettings { OnlyShowMyCode = false });

            var data = GraphData.FromGraph(graph);

            var ids = data.Nodes.Select(n => n.Id).ToHashSet();
            Assert.All(data.Edges, e => Assert.True(ids.Contains(e.Source) && ids.Contains(e.Target), $"{e.Source} -> {e.Target}"));
            Assert.All(data.Nodes, n => Assert.False(string.IsNullOrEmpty(n.Id)));
        }

        [Fact]
        public void CSharpMethodNamedConstructor_KeepsItsName()
        {
            var graph = CodeAnalyzer.Analyze("class A { void constructor() { Run(); } void Run() { } }");

            var data = GraphData.FromGraph(graph);

            Assert.Equal(new[] { "constructor() → Run()" }, data.Calls.Select(c => $"{c.Caller} → {c.Callee}"));
        }
    }
}
