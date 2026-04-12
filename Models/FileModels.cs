namespace CSharpCodeGraph.Models
{
    public class ZipFileEntry
    {
        public string FullPath { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public bool IsSelected { get; set; } = true;
    }

    public class FileTreeNode
    {
        public string Name { get; set; } = string.Empty;
        public bool IsFolder { get; set; }
        public ZipFileEntry? Entry { get; set; }
        public SortedDictionary<string, FileTreeNode> Children { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}