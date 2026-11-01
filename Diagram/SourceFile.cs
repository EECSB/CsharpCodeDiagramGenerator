using System.Text;
using System.Text.RegularExpressions;

namespace CSharpCodeGraph.Diagram
{
    public class SourceFile
    {
        public SourceFile(string path, string content)
        {
            Path = path;
            Content = content;
        }

        public string Path { get; }
        public string Content { get; }
    }

    /// <summary>
    /// Joins several source files into one editor document and splits it back again.
    /// Each file is preceded by a marker line, so every file can still be parsed on its own.
    /// </summary>
    public static class SourceBundle
    {
        private const string DefaultFileName = "Source.cs";
        private const int MarkerWidth = 5;

        // "// ===== File: path =====": five '=' on each side, or more when a file has a line like that of its own
        // (see Join). Every marker in a document has the same number.
        private static readonly Regex MarkerRegex = new(@"^// (={5,}) File: (.+?) \1\r?$", RegexOptions.Multiline | RegexOptions.Compiled);

        public static string Join(IEnumerable<SourceFile> files)
        {
            var fileList = files.ToList();

            // A line like a marker in a file (in a string or a comment) would split the file there, so the markers
            // get more '=' than any such line has.
            int width = fileList
                .SelectMany(file => MarkerRegex.Matches(file.Content))
                .Select(match => match.Groups[1].Length + 1)
                .Append(MarkerWidth)
                .Max();
            var equals = new string('=', width);

            var sb = new StringBuilder();
            foreach (var file in fileList)
            {
                if (sb.Length > 0)
                    sb.AppendLine();

                sb.AppendLine($"// {equals} File: {file.Path} {equals}");
                sb.Append(file.Content);

                if (!file.Content.EndsWith('\n'))
                    sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Gives the files back, each starting on the line after its marker, so their line numbers are the
        /// file's own. Text before the first marker, or without any, is a file of its own.
        /// </summary>
        public static List<SourceFile> Split(string text)
        {
            var files = new List<SourceFile>();

            // The first marker says how many '=' the markers have; a line with a different number is part of a file.
            var first = MarkerRegex.Match(text);
            var matches = first.Success
                ? MarkerRegex.Matches(text).Where(m => m.Groups[1].Length == first.Groups[1].Length).ToList()
                : new List<Match>();

            int firstMarker = matches.Count > 0 ? matches[0].Index : text.Length;
            var preamble = text.Substring(0, firstMarker);
            if (!string.IsNullOrWhiteSpace(preamble))
                files.Add(new SourceFile(DefaultFileName, preamble));

            for (int i = 0; i < matches.Count; i++)
            {
                int start = SkipLineBreak(text, matches[i].Index + matches[i].Length);
                int end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;

                // Join puts an empty line between a file and the next marker.
                if (i + 1 < matches.Count && end > start)
                    end = LineBreakStart(text, start, end);

                files.Add(new SourceFile(matches[i].Groups[2].Value, text.Substring(start, end - start)));
            }

            return files;
        }

        private static int SkipLineBreak(string text, int index)
        {
            if (index < text.Length && text[index] == '\r')
                index++;
            if (index < text.Length && text[index] == '\n')
                index++;
            return index;
        }

        // Where the line break that ends text[start..end] begins, or end when it doesn't end in one.
        private static int LineBreakStart(string text, int start, int end)
        {
            if (end > start && text[end - 1] == '\n')
                end--;
            if (end > start && text[end - 1] == '\r')
                end--;
            return end;
        }
    }
}
