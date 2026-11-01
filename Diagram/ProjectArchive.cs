using System.Diagnostics;
using System.IO.Compression;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace CSharpCodeGraph.Diagram
{
    /// <summary>
    /// Reads a project's source files out of an uploaded archive: .zip with .NET's own reader, and
    /// 7z, rar and tar (also .tar.gz, .tar.bz2 and .tar.xz) with SharpCompress. Only the files the
    /// diagram uses (.cs, .razor, .js, .mjs, .cjs and .csproj) are taken out, except for packages in node_modules; everything else is skipped.
    /// </summary>
    public static class ProjectArchive
    {
        public static readonly string[] SupportedExtensions =
        {
            ".zip", ".7z", ".rar", ".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tar.xz", ".txz"
        };

        // The files a diagram is drawn from, for messages. .csproj files are read too, but only for the root namespace.
        public const string SourceFileTypes = ".cs, .razor, .js, .mjs or .cjs";

        // How long reading goes on before giving the browser a turn (see CodeAnalyzer.AnalyzeAsync).
        private const int YieldIntervalMs = 50;

        private const string PasswordProtectedMessage = "The archive is password-protected. Use one without a password.";

        public static bool IsSupported(string fileName) =>
            SupportedExtensions.Any(extension => fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

        public static bool IsProjectFile(string path) =>
            path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            // Installed packages can be tens of thousands of files, none of them the project's own.
            || (JavaScriptAnalyzer.IsJavaScriptFile(path) && !path.Split('/').Contains("node_modules", StringComparer.OrdinalIgnoreCase));

        // Build output (bin and obj folders) is listed in the file tree but not ticked, and so are JavaScript libraries
        // and bundles: copies of other people's code (wwwroot/lib), or of the project's own after building it.
        private static readonly string[] BuildOutputFolders = { "bin", "obj" };
        private static readonly string[] LibraryFolders = { "lib", "dist", "vendor", "webpack-bundle" };

        public static bool IsBuildOutputOrLibrary(string path)
        {
            var folders = path.Split('/').SkipLast(1).ToList();
            if (folders.Any(folder => BuildOutputFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)))
                return true;

            return JavaScriptAnalyzer.IsJavaScriptFile(path)
                && (folders.Any(folder => LibraryFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    || path.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".bundle.js", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns the project files in the archive, with '/' separated paths. The format is told from the
        /// content, not the file name. Throws <see cref="InvalidDataException"/> with a message for the user
        /// when the archive can't be read.
        /// </summary>
        public static async Task<List<SourceFile>> ReadAsync(Stream archive)
        {
            if (!archive.CanSeek)
                throw new ArgumentException("The archive stream has to be seekable.", nameof(archive));

            try
            {
                var files = IsZip(archive) ? await ReadZipAsync(archive) : await ReadOtherAsync(archive);

                // Otherwise the app would show an empty project, as a .csproj on its own has nothing to draw.
                if (files.All(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"There are no {SourceFileTypes} files in the archive. Use an archive of the project folder.");

                // One path can be in an archive twice: a repeated zip entry, src\a.js next to src/a.js, a tar
                // that was appended to. As when extracting the archive, the later one wins.
                return files.GroupBy(f => f.Path, StringComparer.Ordinal).Select(g => g.Last()).ToList();
            }
            catch (InvalidDataException ex) when (ex.Source == "System.IO.Compression")
            {
                // .NET's zip reader throws the same exception type as the messages here, for a damaged zip.
                throw new InvalidDataException($"The archive couldn't be read ({ex.Message}).", ex);
            }
            catch (Exception ex) when (ex is not InvalidDataException)
            {
                // 7z archives with encrypted file names fail while opening, before any entry says it's encrypted.
                if (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || ex is CryptographicException)
                    throw new InvalidDataException(PasswordProtectedMessage, ex);

                throw new InvalidDataException($"The archive couldn't be read ({ex.Message}).", ex);
            }
        }

        private static async Task<List<SourceFile>> ReadZipAsync(Stream archive)
        {
            var files = new List<SourceFile>();
            var stopwatch = Stopwatch.StartNew();

            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in zip.Entries)
            {
                var path = NormalizePath(entry.FullName);
                if (path.EndsWith('/') || !IsProjectFile(path))
                    continue;

                // .NET can't decrypt zip entries: a stored one would be read as scrambled text.
                if (entry.IsEncrypted)
                    throw new InvalidDataException(PasswordProtectedMessage);

                using var stream = entry.Open();
                files.Add(new SourceFile(path, await ReadTextAsync(stream)));
                await YieldIfBusyAsync(stopwatch);
            }

            return files;
        }

        private static async Task<List<SourceFile>> ReadOtherAsync(Stream archive)
        {
            var files = new List<SourceFile>();
            var stopwatch = Stopwatch.StartNew();

            archive.Position = 0;
            bool isSevenZip = SevenZipArchive.IsSevenZipFile(archive);
            archive.Position = 0;

            // 7z needs random access to open; after that, one pass over all entries (ExtractAllEntries) is
            // much faster than opening entries one by one, because solid archives compress them together.
            using var sevenZip = isSevenZip ? SevenZipArchive.OpenArchive(archive, new ReaderOptions { LeaveStreamOpen = true }) : null;
            using var reader = sevenZip != null
                ? sevenZip.ExtractAllEntries()
                : OpenReader(archive);

            while (reader.MoveToNextEntry())
            {
                var entry = reader.Entry;
                if (entry.IsDirectory || entry.Key == null)
                    continue;

                var path = NormalizePath(entry.Key);
                if (!IsProjectFile(path))
                    continue;

                if (entry.IsEncrypted)
                    throw new InvalidDataException(PasswordProtectedMessage);

                using var stream = reader.OpenEntryStream();
                files.Add(new SourceFile(path, await ReadTextAsync(stream)));
                await YieldIfBusyAsync(stopwatch);
            }

            return files;
        }

        // Rar, tar and compressed tar (.tar.gz, .tar.bz2, .tar.xz) are told apart by their content.
        private static IReader OpenReader(Stream archive)
        {
            try
            {
                return ReaderFactory.OpenReader(archive, new ReaderOptions { LeaveStreamOpen = true });
            }
            catch (InvalidFormatException)
            {
                throw new InvalidDataException("This isn't an archive the app can read. Use a .zip, .7z, .rar or .tar.gz of the project folder.");
            }
        }

        private static bool IsZip(Stream archive)
        {
            Span<byte> signature = stackalloc byte[4];
            archive.Position = 0;
            int read = archive.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
            archive.Position = 0;

            // "PK\3\4" starts a zip with entries, "PK\5\6" an empty one.
            return read == 4 && signature[0] == 'P' && signature[1] == 'K'
                && ((signature[2] == 3 && signature[3] == 4) || (signature[2] == 5 && signature[3] == 6));
        }

        private static async Task<string> ReadTextAsync(Stream stream)
        {
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }

        private static async Task YieldIfBusyAsync(Stopwatch stopwatch)
        {
            if (stopwatch.ElapsedMilliseconds < YieldIntervalMs)
                return;

            await Task.Delay(1);
            stopwatch.Restart();
        }

        // A tar made inside the project folder (tar -czf project.tar.gz .) starts every path with "./".
        private static string NormalizePath(string path) =>
            string.Join('/', path.Replace('\\', '/').Split('/').Where(segment => segment != ".")).TrimStart('/');
    }
}
