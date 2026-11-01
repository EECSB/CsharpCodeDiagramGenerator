using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CSharpCodeGraph.Diagram
{
    /// <summary>
    /// Reads a project's source files from a public GitHub repository, in the browser. GitHub's zip
    /// downloads can't be fetched from another site (no CORS), so the file list comes from the REST API
    /// and each project file (.cs, .razor, .js, .mjs, .cjs, .csproj) from raw.githubusercontent.com. Without signing in, the
    /// API allows 60 requests an hour; one load uses two (three for a folder of a tag or of a shortened commit SHA, and four
    /// or five when the tag's name has slashes).
    /// </summary>
    public static class GitHubRepository
    {
        public const int MaxFiles = 3000;
        private const int ParallelDownloads = 8;

        private const string NotFoundMessage = "The repository or branch wasn't found. Only public repositories can be read.";

        // RefAndFolder is what follows /tree/ in a link: a branch, tag or commit, then maybe a folder. Branch and tag
        // names can have slashes too, so where the one ends and the other starts is found out when reading.
        public sealed record Location(string Owner, string Repository, string? RefAndFolder);

        // github.com/owner/repo, optionally .git or /tree/<branch>/<folder>; "owner/repo" works too.
        private static readonly Regex UrlRegex = new(
            @"^(?:(?:https?://)?(?:www\.)?github\.com/)?(?<owner>[\w.-]+)/(?<repo>[\w.-]+?)(?:\.git)?(?:/tree/(?<path>[^?#]+?))?/?(?:[?#].*)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CommitShaRegex = new("^[0-9a-f]{40}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool TryParse(string url, out Location? location)
        {
            var match = UrlRegex.Match(url.Trim());
            // Links have special characters escaped (My%20App, fix%23123); requests escape them again.
            var refAndFolder = string.Join('/', Uri.UnescapeDataString(match.Groups["path"].Value).Split('/', StringSplitOptions.RemoveEmptyEntries));
            location = match.Success
                ? new Location(match.Groups["owner"].Value, match.Groups["repo"].Value, refAndFolder.Length > 0 ? refAndFolder : null)
                : null;
            return match.Success;
        }

        /// <summary>
        /// Returns the project files, with paths starting with the repository's name (as in an archive of it).
        /// Throws <see cref="InvalidDataException"/> with a message for the user when the repository can't be read.
        /// </summary>
        public static async Task<List<SourceFile>> ReadAsync(HttpClient http, Location location, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            var api = $"https://api.github.com/repos/{location.Owner}/{location.Repository}";

            progress?.Report("Reading the repository's file list...");
            var (commit, folder) = await ResolveAsync(http, api, location.RefAndFolder, cancellationToken);
            var name = $"{location.Owner}/{location.Repository}{(folder.Length > 0 ? "/" + folder : "")}";
            var noFilesMessage = $"There are no {ProjectArchive.SourceFileTypes} files in {name}.";

            // A folder is listed on its own, as GitHub cuts the list of a big repository's whole tree short.
            var tree = await GetJsonAsync<TreeResponse>(http, $"{api}/git/trees/{commit}{(folder.Length > 0 ? ":" + EscapePath(folder) : "")}?recursive=1", cancellationToken, noFilesMessage);
            if (tree.Truncated)
                throw new InvalidDataException($"{name} has too many files for GitHub to list at once. Link to one of its folders instead (…/tree/<branch>/<folder>).");

            // A folder's list has paths from the folder on.
            var folderPrefix = folder.Length > 0 ? folder + "/" : string.Empty;
            var paths = tree.Tree
                .Where(item => item.Type == "blob")
                .Select(item => folderPrefix + item.Path)
                .Where(ProjectArchive.IsProjectFile)
                .ToList();

            // A .csproj on its own has nothing to draw.
            if (paths.All(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(noFilesMessage);
            if (paths.Count > MaxFiles)
                throw new InvalidDataException($"{name} has {paths.Count} source files, more than the {MaxFiles} the app reads. Link to one of its folders instead (…/tree/<branch>/<folder>).");

            var files = new SourceFile[paths.Count];
            int done = 0;
            using var gate = new SemaphoreSlim(ParallelDownloads);
            // The first failed download fails the load, so the others are stopped then.
            using var failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await Task.WhenAll(paths.Select(async (path, i) =>
            {
                await gate.WaitAsync(failed.Token);
                try
                {
                    // By the commit the list came from, as raw.githubusercontent.com caches a branch's files for a few minutes.
                    using var response = await http.GetAsync($"https://raw.githubusercontent.com/{location.Owner}/{location.Repository}/{commit}/{EscapePath(path)}", failed.Token);
                    ThrowIfFailed(response);
                    files[i] = new SourceFile($"{location.Repository}/{path}", await response.Content.ReadAsStringAsync(failed.Token));
                    progress?.Report($"Downloading files ({Interlocked.Increment(ref done)} of {paths.Count})...");
                }
                catch
                {
                    failed.Cancel();
                    throw;
                }
                finally
                {
                    gate.Release();
                }
            }));

            return files.ToList();
        }

        // Finds the commit a link names, and the folder in it. Only branch and tag names have slashes, so a single
        // segment after /tree/, or a commit's SHA, is the ref as it is. Otherwise the branches starting with the first
        // segment are listed, with their commits; failing that, it's a tag or a shortened SHA, which GitHub finds by
        // name, or else a tag with slashes in its name.
        private static async Task<(string Commit, string Folder)> ResolveAsync(HttpClient http, string api, string? refAndFolder, CancellationToken cancellationToken)
        {
            var segments = (refAndFolder ?? "HEAD").Split('/');
            var first = segments[0];
            var rest = string.Join('/', segments[1..]);

            if (segments.Length == 1 || CommitShaRegex.IsMatch(first))
                return (await GetCommitAsync(http, api, first, cancellationToken), rest);

            if (await FindRefAsync(http, api, "heads", segments, cancellationToken) is { } branch)
                return (branch.Target.Sha, branch.Folder);

            if (await FindCommitAsync(http, api, first, cancellationToken) is { } commit)
                return (commit, rest);

            var tag = await FindRefAsync(http, api, "tags", segments, cancellationToken)
                ?? throw new InvalidDataException(NotFoundMessage);
            // An annotated tag points to a tag object, which points to the commit.
            return (tag.Target.Type == "commit" ? tag.Target.Sha : await GetCommitAsync(http, api, tag.Name, cancellationToken), tag.Folder);
        }

        // The branch or tag ("heads" or "tags") the path starts with, segment by segment. A ref's name can't also start
        // another's (feature and feature/x), so at most one fits.
        private static async Task<(string Name, GitObject Target, string Folder)?> FindRefAsync(HttpClient http, string api, string kind, string[] segments, CancellationToken cancellationToken)
        {
            var refs = await GetJsonAsync<List<GitRef>>(http, $"{api}/git/matching-refs/{kind}/{Uri.EscapeDataString(segments[0])}", cancellationToken);
            for (int length = segments.Length; length > 0; length--)
            {
                var name = string.Join('/', segments[..length]);
                if (refs.Find(r => r.Ref == $"refs/{kind}/{name}") is { } found)
                    return (name, found.Object, string.Join('/', segments[length..]));
            }

            return null;
        }

        private static async Task<string> GetCommitAsync(HttpClient http, string api, string reference, CancellationToken cancellationToken) =>
            await FindCommitAsync(http, api, reference, cancellationToken) ?? throw new InvalidDataException(NotFoundMessage);

        // The SHA of the commit a branch, tag or commit (or HEAD, the default branch) names; null when there's none.
        private static async Task<string?> FindCommitAsync(HttpClient http, string api, string reference, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{api}/commits/{EscapePath(reference)}");
            // With this media type GitHub answers with just the SHA.
            request.Headers.Accept.ParseAdd("application/vnd.github.sha");
            using var response = await http.SendAsync(request, cancellationToken);

            // GitHub answers 422 for a name that isn't a branch, tag or commit.
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                return null;

            ThrowIfFailed(response);
            return (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        }

        private static async Task<T> GetJsonAsync<T>(HttpClient http, string url, CancellationToken cancellationToken, string notFoundMessage = NotFoundMessage)
        {
            using var response = await http.GetAsync(url, cancellationToken);
            ThrowIfFailed(response, notFoundMessage);

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                ?? throw new InvalidDataException("GitHub sent an empty answer.");
        }

        // For the API and raw.githubusercontent.com alike.
        private static void ThrowIfFailed(HttpResponseMessage response, string notFoundMessage = NotFoundMessage)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidDataException(notFoundMessage);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new InvalidDataException("GitHub's limit of 60 requests an hour without signing in has been reached. Try again later, or download the repository as a .zip and drop it here.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidDataException($"GitHub answered {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

        private sealed class TreeResponse
        {
            [JsonPropertyName("tree")] public List<TreeItem> Tree { get; set; } = new();
            [JsonPropertyName("truncated")] public bool Truncated { get; set; }
        }

        private sealed class TreeItem
        {
            [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
            [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        }

        private sealed class GitRef
        {
            [JsonPropertyName("ref")] public string Ref { get; set; } = string.Empty;
            [JsonPropertyName("object")] public GitObject Object { get; set; } = new();
        }

        private sealed class GitObject
        {
            [JsonPropertyName("sha")] public string Sha { get; set; } = string.Empty;
            [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        }
    }
}
