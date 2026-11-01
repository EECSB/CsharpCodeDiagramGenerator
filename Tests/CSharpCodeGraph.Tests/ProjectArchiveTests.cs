using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using CSharpCodeGraph.Diagram;
using Xunit;

namespace CSharpCodeGraph.Tests
{
    public class ProjectArchiveTests
    {
        // The Fixtures folder has the same small project packed by 7-Zip in several formats (in ordinal order here).
        private static readonly string[] ProjectPaths =
        {
            "MyApp/Models/Order.cs",
            "MyApp/MyApp.csproj",
            "MyApp/Pages/Index.razor",
            "MyApp/Program.cs",
            "MyApp/bin/Debug/Generated.cs"
        };

        private static async Task<List<SourceFile>> ReadFixtureAsync(string name)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
            return await ProjectArchive.ReadAsync(new MemoryStream(bytes));
        }

        private static IEnumerable<string> SortedPaths(IEnumerable<SourceFile> files) =>
            files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal);

        private static void AssertIsTheProject(List<SourceFile> files)
        {
            Assert.Equal(ProjectPaths, SortedPaths(files));
            Assert.Contains("public class Order", files.Single(f => f.Path == "MyApp/Models/Order.cs").Content);
        }

        // A zip of the given files, each holding a comment with its path.
        private static MemoryStream ZipOf(params string[] paths)
        {
            var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var path in paths)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                    writer.Write("// " + path);
                }
            }

            archive.Position = 0;
            return archive;
        }

        [Theory]
        [InlineData("project.7z")]
        [InlineData("project.tar.bz2")]
        [InlineData("project.tar.xz")]
        public async Task ReadsTheProjectFiles_FromArchivesMadeWith7Zip(string fixture)
        {
            AssertIsTheProject(await ReadFixtureAsync(fixture));
        }

        [Fact]
        public async Task ReadsTheProjectFiles_FromAZip()
        {
            var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            {
                zip.CreateEntry("MyApp/Models/");
                foreach (var path in ProjectPaths.Append("MyApp/readme.md"))
                {
                    using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                    writer.Write(path.EndsWith("Order.cs") ? "namespace MyApp.Models; public class Order { }" : "// " + path);
                }
            }

            archive.Position = 0;
            AssertIsTheProject(await ProjectArchive.ReadAsync(archive));
        }

        [Fact]
        public async Task ReadsTheProjectFiles_FromATarGz()
        {
            var archive = new MemoryStream();
            using (var gzip = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip))
            {
                foreach (var path in ProjectPaths.Append("MyApp/readme.md"))
                {
                    var text = path.EndsWith("Order.cs") ? "namespace MyApp.Models; public class Order { }" : "// " + path;
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
                }
            }

            archive.Position = 0;
            AssertIsTheProject(await ProjectArchive.ReadAsync(archive));
        }

        [Fact]
        public async Task TarPathsStartingWithDotSlash_AreReadWithoutIt()
        {
            // tar -czf project.tar.gz . run in the project's folder starts every path with "./".
            var archive = new MemoryStream();
            using (var gzip = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip))
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./"));
                foreach (var path in new[] { "./src/app.js", "./src/./lib/util.js", "././Program.cs" })
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("// " + path)) });
            }

            archive.Position = 0;
            var files = await ProjectArchive.ReadAsync(archive);

            Assert.Equal(new[] { "Program.cs", "src/app.js", "src/lib/util.js" }, SortedPaths(files));
        }

        [Fact]
        public async Task PackagesInNodeModules_AreSkipped()
        {
            var files = await ProjectArchive.ReadAsync(ZipOf(
                "app/src/main.js", "app/src/node_modules.js", "app/node_modules/lodash/lodash.js", "app/Node_Modules/esm/index.mjs", "node_modules/cjs/index.cjs"));

            Assert.Equal(new[] { "app/src/main.js", "app/src/node_modules.js" }, files.Select(f => f.Path));
        }

        [Theory]
        [InlineData("project-password.7z")]
        [InlineData("project-password-headers.7z")]
        [InlineData("project-password.zip")]
        [InlineData("project-password-stored.zip")]
        [InlineData("project-password-aes.zip")]
        public async Task PasswordProtectedArchives_GetAClearMessage(string fixture)
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => ReadFixtureAsync(fixture));

            // The app's own message, not one that only mentions a password (SharpCompress's errors do too).
            Assert.Equal("The archive is password-protected. Use one without a password.", error.Message);
        }

        [Fact]
        public async Task APathInTheArchiveTwice_IsReadOnce_AndTheLaterOneWins()
        {
            var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, content) in new[] { ("src/a.js", "let first;"), ("src\\a.js", "let second;"), ("src/b.js", "let b;"), ("src/b.js", "let b2;") })
                {
                    using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                    writer.Write(content);
                }
            }

            archive.Position = 0;
            var files = await ProjectArchive.ReadAsync(archive);

            Assert.Equal(new[] { "src/a.js", "src/b.js" }, files.Select(f => f.Path));
            Assert.Equal(new[] { "let second;", "let b2;" }, files.Select(f => f.Content));
        }

        [Fact]
        public async Task AnArchiveWithoutSourceFiles_GetsAClearMessage()
        {
            // A .csproj is only read for its root namespace, so on its own there's nothing to draw.
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => ProjectArchive.ReadAsync(ZipOf("MyApp/MyApp.csproj", "MyApp/readme.md")));

            Assert.Equal("There are no .cs, .razor, .js, .mjs or .cjs files in the archive. Use an archive of the project folder.", error.Message);
        }

        [Fact]
        public async Task ADamagedZip_GetsTheSameMessageAsOtherArchives()
        {
            var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry("src/a.cs").Open());
                writer.Write("class A { }");
            }

            // Cut off before the zip's directory at its end, as an interrupted download would be.
            var cutOff = new MemoryStream(archive.ToArray()[..(int)(archive.Length / 2)]);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => ProjectArchive.ReadAsync(cutOff));
            Assert.StartsWith("The archive couldn't be read (", error.Message);
        }

        [Fact]
        public async Task SomethingElse_IsNotTakenForAnArchive()
        {
            var notAnArchive = new MemoryStream(Encoding.UTF8.GetBytes("public class NotAnArchive { }"));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => ProjectArchive.ReadAsync(notAnArchive));
            Assert.Contains("isn't an archive", error.Message);
        }

        [Theory]
        [InlineData("App/bin/Debug/Generated.cs", true)]
        [InlineData("App/OBJ/Debug/AssemblyInfo.cs", true)]
        [InlineData("App/wwwroot/lib/bootstrap/js/bootstrap.js", true)]
        [InlineData("App/dist/app.mjs", true)]
        [InlineData("App/wwwroot/js/site.min.js", true)]
        [InlineData("App/wwwroot/js/webpack-bundle/index.js", true)]
        [InlineData("App/wwwroot/js/app.bundle.js", true)]
        [InlineData("App/Program.cs", false)]
        [InlineData("App/lib/Helpers.cs", false)] // C# in a lib folder is the project's own
        [InlineData("App/Binary/Reader.cs", false)]
        [InlineData("App/wwwroot/js/site.js", false)]
        public void BuildOutputAndLibraries_StartUnticked(string path, bool expected)
        {
            Assert.Equal(expected, ProjectArchive.IsBuildOutputOrLibrary(path));
        }

        [Theory]
        [InlineData("Project.zip", true)]
        [InlineData("Project.7Z", true)]
        [InlineData("Project.rar", true)]
        [InlineData("Project.tar.gz", true)]
        [InlineData("Project.tgz", true)]
        [InlineData("Project.tar.xz", true)]
        [InlineData("Program.cs", false)]
        [InlineData("notes.gz", false)]
        public void IsSupported_GoesByTheFileName(string fileName, bool expected)
        {
            Assert.Equal(expected, ProjectArchive.IsSupported(fileName));
        }
    }
}
