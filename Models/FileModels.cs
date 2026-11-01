namespace CSharpCodeGraph.Models
{
    public class ProjectFileEntry
    {
        public string FullPath { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public bool IsSelected { get; set; } = true;
    }

    public class FileTreeNode
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public bool IsFolder { get; set; }
        public ProjectFileEntry? Entry { get; set; }
        // Case-sensitive, as the paths are: an archive can hold both src/a.cs and Src/a.cs.
        public SortedDictionary<string, FileTreeNode> Children { get; set; } = new(StringComparer.Ordinal);
    }
}