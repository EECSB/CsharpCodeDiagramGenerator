using CSharpCodeGraph.Diagram;
using Xunit;

namespace CSharpCodeGraph.Tests
{
    public class SourceBundleTests
    {
        [Fact]
        public void JoinThenSplit_KeepsFilesApart()
        {
            var files = new[]
            {
                new SourceFile("A/First.cs", "namespace X { public class First { } } // end"),
                new SourceFile("B/Second.cs", "namespace Y { public class Second { } }\n")
            };

            var split = SourceBundle.Split(SourceBundle.Join(files));

            Assert.Equal(new[] { "A/First.cs", "B/Second.cs" }, split.Select(f => f.Path));
        }

        [Fact]
        public void FileEndingInAComment_DoesNotHideTheNextFile()
        {
            var joined = SourceBundle.Join(new[]
            {
                new SourceFile("First.cs", "namespace X { public class First { } } // end"),
                new SourceFile("Second.cs", "namespace Y { public class Second { } }")
            });

            var graph = CodeAnalyzer.Analyze(joined);

            Assert.Equal(new[] { "X.First", "Y.Second" }, graph.Types.Select(t => t.Id));
        }

        [Fact]
        public void JoinThenSplit_GivesBackEachFile_FromItsFirstLine()
        {
            var files = new[]
            {
                new SourceFile("A.cs", "class A { }\n"),
                new SourceFile("B.cs", "// B\nclass B { }\n")
            };

            var split = SourceBundle.Split(SourceBundle.Join(files));

            // Line 0 is the file's first line, so the details panel's "B.cs:2" is the line class B is on.
            Assert.Equal(files.Select(f => f.Content), split.Select(f => f.Content));
            var b = CodeAnalyzer.Analyze(split).Types.Single(t => t.Id == "B");
            Assert.Equal(("B.cs", 1), (b.FilePath, b.Line));
        }

        [Fact]
        public void ALineLikeAMarkerInAFile_DoesNotSplitIt()
        {
            const string fixture = "class Fixtures\n{\n    const string Text = @\"\n// ===== File: Other.cs =====\nclass X { }\";\n}\n";
            var files = new[] { new SourceFile("Fixtures.cs", fixture), new SourceFile("Other.cs", "class Other { }\n") };

            var split = SourceBundle.Split(SourceBundle.Join(files));

            Assert.Equal(new[] { "Fixtures.cs", "Other.cs" }, split.Select(f => f.Path));
            Assert.Equal(fixture, split[0].Content);
            Assert.DoesNotContain(CodeAnalyzer.Analyze(split).Types, t => t.Id == "X");
        }

        [Fact]
        public void TextWithoutMarkers_IsOneFile()
        {
            var split = SourceBundle.Split("class A { }");

            var file = Assert.Single(split);
            Assert.Equal("class A { }", file.Content);
        }
    }
}
