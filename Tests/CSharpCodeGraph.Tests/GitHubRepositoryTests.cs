using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSharpCodeGraph.Diagram;
using Xunit;

namespace CSharpCodeGraph.Tests
{
    public class GitHubRepositoryTests
    {
        private const string MainSha = "1111111111111111111111111111111111111111";
        private const string FeatureSha = "2222222222222222222222222222222222222222";
        private const string TagSha = "3333333333333333333333333333333333333333";
        private const string ReleaseSha = "4444444444444444444444444444444444444444";
        // A commit no branch points to (any more).
        private const string EarlierSha = "5555555555555555555555555555555555555555";

        private const string RateLimitMessage = "GitHub's limit of 60 requests an hour without signing in has been reached. Try again later, or download the repository as a .zip and drop it here.";

        // o/r, with the files on its main branch.
        private static FakeGitHub Repository(params string[] paths)
        {
            var github = new FakeGitHub();
            github.Commits[MainSha] = paths;
            github.Branches["main"] = MainSha;
            return github;
        }

        private static Task<List<SourceFile>> ReadAsync(FakeGitHub github, string url)
        {
            Assert.True(GitHubRepository.TryParse(url, out var location));
            return GitHubRepository.ReadAsync(new HttpClient(github), location!);
        }

        private static IEnumerable<string> RawRequests(FakeGitHub github) =>
            github.Requests.Where(url => url.StartsWith("https://raw.githubusercontent.com/"));


        [Theory]
        [InlineData("https://github.com/o/r", "o/r")]
        [InlineData("https://github.com/o/r/tree/main/lib", "o/r/lib")]
        public async Task AListGitHubCutsShort_GetsAClearMessage_InsteadOfLoadingPartly(string url, string name)
        {
            var github = Repository("lib/A.cs", "lib/B.cs", "lib/C.cs", "src/App/Program.cs");
            github.MaxListed = 2;

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(github, url));

            Assert.Equal($"{name} has too many files for GitHub to list at once. Link to one of its folders instead (…/tree/<branch>/<folder>).", error.Message);
            Assert.Empty(RawRequests(github));
        }

        [Fact]
        public async Task AFolder_IsListedOnItsOwn()
        {
            // The whole repository's list is cut short before it gets to src/App.
            var github = Repository("lib/A.cs", "lib/B.cs", "lib/C.cs", "src/App/Program.cs");
            github.MaxListed = 2;

            var files = await ReadAsync(github, "https://github.com/o/r/tree/main/src/App");

            Assert.Equal(new[] { "r/src/App/Program.cs" }, files.Select(f => f.Path));
        }

        [Theory]
        [InlineData("https://github.com/o/r/tree/main/My%20App")]
        [InlineData("https://github.com/o/r/tree/fix%23123/My%20App")]
        public async Task EscapedCharactersInALink_AreReadUnescaped(string url)
        {
            var github = Repository("My App/Program.cs", "Other/Skipped.cs");
            github.Branches["fix#123"] = MainSha;

            var files = await ReadAsync(github, url);

            Assert.Equal(new[] { "r/My App/Program.cs" }, files.Select(f => f.Path));
        }

        [Theory]
        [InlineData("feature/x/src", "r/src/Feature.cs", 2)]
        [InlineData("feature/x", "r/src/Feature.cs", 2)]
        [InlineData("main/src", "r/src/Main.cs", 2)]
        [InlineData("main", "r/src/Main.cs", 2)]
        [InlineData("v1.0/src", "r/src/Tag.cs", 3)]
        [InlineData("release/v2/src", "r/src/Release.cs", 5)]
        [InlineData(EarlierSha + "/src", "r/src/Earlier.cs", 2)]
        [InlineData("5555555/src", "r/src/Earlier.cs", 3)]
        public async Task TheRefInALink_IsToldApartFromTheFolder_WithFewRequests(string refAndFolder, string file, int apiRequests)
        {
            // Branch and tag names can have slashes; "feature-y" also starts with "feature".
            var github = Repository("src/Main.cs");
            github.Commits[FeatureSha] = new[] { "src/Feature.cs" };
            github.Commits[TagSha] = new[] { "src/Tag.cs" };
            github.Commits[ReleaseSha] = new[] { "src/Release.cs" };
            github.Commits[EarlierSha] = new[] { "src/Earlier.cs" };
            github.Branches["feature/x"] = FeatureSha;
            github.Branches["feature-y"] = MainSha;
            github.Tags["v1.0"] = TagSha;
            github.Tags["release/v2"] = ReleaseSha;
            github.AnnotatedTags.Add("release/v2");

            var files = await ReadAsync(github, "https://github.com/o/r/tree/" + refAndFolder);

            Assert.Equal(new[] { file }, files.Select(f => f.Path));
            Assert.Equal(apiRequests, github.ApiRequests.Count);
        }

        [Fact]
        public async Task Files_AreDownloadedFromTheCommitTheListCameFrom()
        {
            // src/Pushed.cs was pushed a moment ago, and raw.githubusercontent.com still serves main's files from before.
            var github = Repository("src/Program.cs", "src/Pushed.cs");
            github.Commits[EarlierSha] = new[] { "src/Program.cs" };
            github.RawCache["main"] = EarlierSha;

            var files = await ReadAsync(github, "https://github.com/o/r");

            Assert.Equal(new[] { "r/src/Program.cs", "r/src/Pushed.cs" }, files.Select(f => f.Path));
            Assert.Equal(2, github.ApiRequests.Count);
        }

        [Theory]
        [InlineData(HttpStatusCode.TooManyRequests, RateLimitMessage)]
        [InlineData(HttpStatusCode.InternalServerError, "GitHub answered 500 (Internal Server Error).")]
        public async Task AFailedDownload_GetsTheSameMessageAsTheApi_AndStopsTheOthers(HttpStatusCode status, string message)
        {
            // The other downloads only end when they're stopped.
            var github = Repository(Enumerable.Range(0, 20).Select(i => $"src/File{i}.cs").ToArray());
            github.RawErrors["src/File0.cs"] = status;
            github.RawHangs = true;

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(github, "https://github.com/o/r").WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal(message, error.Message);
            // Downloads still waiting for their turn aren't started.
            Assert.True(RawRequests(github).Count() < 20);
        }

        [Theory]
        [InlineData("https://github.com/o/r", "o/r")]
        [InlineData("https://github.com/o/r/tree/main/docs", "o/r/docs")]
        [InlineData("https://github.com/o/r/tree/main/missing", "o/r/missing")]
        public async Task ALinkWithoutSourceFiles_GetsAClearMessage(string url, string name)
        {
            // A .csproj alone has nothing to draw, and packages in node_modules aren't the project's own.
            var github = Repository("docs/readme.md", "src/App.csproj", "node_modules/pkg/index.js");

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(github, url));

            Assert.Equal($"There are no .cs, .razor, .js, .mjs or .cjs files in {name}.", error.Message);
        }


        // The public repository o/r on a fake GitHub, which answers the API and raw.githubusercontent.com as GitHub
        // does, and notes down the requests.
        private sealed class FakeGitHub : HttpMessageHandler
        {
            // The tag object an annotated tag points to, which in turn points to the commit.
            private const string TagObjectSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

            // A commit's SHA -> the paths of its files.
            public Dictionary<string, string[]> Commits { get; } = new();
            // A branch's or tag's name -> its commit's SHA.
            public Dictionary<string, string> Branches { get; } = new();
            public Dictionary<string, string> Tags { get; } = new();
            public HashSet<string> AnnotatedTags { get; } = new();

            // GitHub cuts a recursive list short after 100,000 entries.
            public int MaxListed { get; set; } = int.MaxValue;

            // raw.githubusercontent.com caches a branch's files for a few minutes: branch -> the commit it serves.
            public Dictionary<string, string> RawCache { get; } = new();

            // Downloads that fail; with RawHangs, the others don't end until they're stopped.
            public Dictionary<string, HttpStatusCode> RawErrors { get; } = new();
            public bool RawHangs { get; set; }

            public List<string> Requests { get; } = new();
            public List<string> ApiRequests => Requests.Where(url => url.StartsWith("https://api.github.com/")).ToList();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Yield();
                var uri = request.RequestUri!;
                lock (Requests)
                    Requests.Add(uri.AbsoluteUri);

                var path = Uri.UnescapeDataString(uri.AbsolutePath);
                return uri.Host == "api.github.com" ? Api(path, request) : await RawAsync(path, cancellationToken);
            }

            private HttpResponseMessage Api(string path, HttpRequestMessage request)
            {
                if (path == "/repos/o/r")
                    return Json(new { default_branch = "main" });

                var match = Regex.Match(path, "^/repos/o/r/commits/(.+)$");
                if (match.Success)
                {
                    var sha = Resolve(match.Groups[1].Value);
                    if (sha == null)
                        return new HttpResponseMessage(HttpStatusCode.UnprocessableEntity);
                    return request.Headers.Accept.Any(a => a.MediaType == "application/vnd.github.sha") ? Text(sha) : Json(new { sha });
                }

                match = Regex.Match(path, "^/repos/o/r/git/matching-refs/(heads|tags)/(.+)$");
                if (match.Success)
                {
                    var kind = match.Groups[1].Value;
                    var refs = kind == "heads" ? Branches : Tags;
                    return Json(refs.Where(r => r.Key.StartsWith(match.Groups[2].Value, StringComparison.Ordinal)).Select(r => new
                    {
                        @ref = $"refs/{kind}/{r.Key}",
                        @object = kind == "tags" && AnnotatedTags.Contains(r.Key)
                            ? new { sha = TagObjectSha, type = "tag" }
                            : new { sha = r.Value, type = "commit" }
                    }));
                }

                // <commit or ref>, or <commit or ref>:<folder> for just that folder.
                match = Regex.Match(path, "^/repos/o/r/git/trees/([^:]+)(?::(.+))?$");
                if (match.Success && Resolve(match.Groups[1].Value) is { } commit)
                {
                    var folder = match.Groups[2].Success ? match.Groups[2].Value + "/" : string.Empty;
                    var files = Commits[commit].Where(p => p.StartsWith(folder, StringComparison.Ordinal)).Select(p => p[folder.Length..]).ToList();
                    if (folder.Length > 0 && files.Count == 0)
                        return new HttpResponseMessage(HttpStatusCode.NotFound);

                    // Folders are listed too, before their files; without recursive=1 only the top level is.
                    bool recursive = request.RequestUri!.Query.Contains("recursive=1");
                    var entries = files
                        .SelectMany(file =>
                        {
                            var parts = file.Split('/');
                            return parts.Select((_, i) => (Path: string.Join('/', parts[..(i + 1)]), Type: i == parts.Length - 1 ? "blob" : "tree"));
                        })
                        .Distinct()
                        .Where(entry => recursive || !entry.Path.Contains('/'))
                        .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                        .ToList();

                    return Json(new
                    {
                        sha = commit,
                        tree = entries.Take(MaxListed).Select(entry => new { path = entry.Path, type = entry.Type }),
                        truncated = entries.Count > MaxListed
                    });
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            // /o/r/<branch or commit>/<file>
            private async Task<HttpResponseMessage> RawAsync(string path, CancellationToken cancellationToken)
            {
                var match = Regex.Match(path, "^/o/r/([^/]+)/(.+)$");
                var file = match.Groups[2].Value;
                if (RawErrors.TryGetValue(file, out var status))
                    return new HttpResponseMessage(status);
                if (RawHangs)
                    await Task.Delay(Timeout.Infinite, cancellationToken);

                var reference = match.Groups[1].Value;
                var commit = RawCache.TryGetValue(reference, out var cached) ? cached : Resolve(reference);
                return commit != null && Commits[commit].Contains(file) ? Text("// " + file) : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            // The commit that HEAD (the default branch), a branch, a tag or a commit's SHA, also shortened, names.
            private string? Resolve(string reference)
            {
                if (reference == "HEAD")
                    reference = "main";
                if (Branches.TryGetValue(reference, out var sha) || Tags.TryGetValue(reference, out sha))
                    return sha;

                return reference.Length >= 7 ? Commits.Keys.FirstOrDefault(c => c.StartsWith(reference, StringComparison.OrdinalIgnoreCase)) : null;
            }

            private static HttpResponseMessage Json(object value) =>
                new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

            private static HttpResponseMessage Text(string text) =>
                new(HttpStatusCode.OK) { Content = new StringContent(text) };
        }
    }
}
