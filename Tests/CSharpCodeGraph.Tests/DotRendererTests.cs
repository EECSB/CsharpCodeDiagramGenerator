using CSharpCodeGraph.Diagram;
using Xunit;

namespace CSharpCodeGraph.Tests
{
    public class DotRendererTests
    {
        private static string Render(string source, DiagramSettings? settings = null) =>
            DotRenderer.Render(CodeAnalyzer.Analyze(source, settings));

        [Fact]
        public void GenericMembers_AreEscapedInRecordLabels()
        {
            var dot = Render("class Order { } class Shop { public List<Order> Orders { get; set; } public Dictionary<string, int> Totals(Order o) => null; }");

            Assert.Contains(@"List\<Order\> Orders\l", dot);
            Assert.Contains(@"Dictionary\<string, int\> Totals(Order o)\l", dot);
        }

        [Fact]
        public void Inheritance_PointsFromDerivedToBase()
        {
            var dot = Render("class Base { } class Derived : Base { }");

            Assert.Contains("\"Derived\" -> \"Base\" [arrowhead=empty, label=\"inherits\"];", dot);
        }

        [Fact]
        public void SelfCalls_AreNotDrawn()
        {
            var dot = Render("class A { public void X() { Y(); } public void Y() { } }");

            Assert.DoesNotContain("->", dot);
        }

        [Fact]
        public void AggregatedCalls_ShowACountWithTheDetailsInTheTooltip()
        {
            var dot = Render("class A { public void X() { } public void Y() { } } class B { void P(A a) { a.X(); a.Y(); } }");

            Assert.Contains("label=\"2 calls\"", dot);
            Assert.Contains(@"tooltip=""P() → X()\nP() → Y()""", dot);
        }

        [Fact]
        public void TypesGetAStereotype()
        {
            var dot = Render("interface IRepo { } static class Helpers { } enum Status { On }");

            Assert.Contains(@"label=""{«interface»\nIRepo}""", dot);
            Assert.Contains(@"label=""{«static»\nHelpers}""", dot);
            Assert.Contains(@"label=""{«enum»\nStatus|On\l}""", dot);
        }
    }
}
