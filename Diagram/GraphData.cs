namespace CSharpCodeGraph.Diagram
{
    /// <summary>
    /// What the browser-side diagram (NpmJS/src/CodeGraphView.js) gets: plain data, serialized to JSON
    /// by JS interop. A type calling its own methods isn't an edge here (the details panel lists those),
    /// only an entry in <see cref="Calls"/>.
    /// </summary>
    public class GraphData
    {
        public List<GraphNodeData> Nodes { get; } = new();
        public List<GraphEdgeData> Edges { get; } = new();
        // Every call between methods, also within one type: the "Functions" option draws them method to method.
        public List<GraphCallData> Calls { get; } = new();

        public static GraphData FromGraph(CodeGraph graph)
        {
            var data = new GraphData();

            foreach (var type in graph.Types)
            {
                data.Nodes.Add(new GraphNodeData
                {
                    Id = type.Id,
                    Name = type.Name,
                    Namespace = type.Namespace,
                    File = type.FilePath,
                    Kind = ToCamelCase(type.Kind.ToString()),
                    Stereotype = type.Stereotype,
                    // Variables first, then methods, like a UML class box.
                    Members = type.Members.Where(m => m.Kind != MemberKind.Method)
                        .Concat(type.Members.Where(m => m.Kind == MemberKind.Method))
                        .Select(m => m.Display)
                        .ToList()
                });
            }

            foreach (var edge in graph.Edges.Where(e => !e.IsSelfLoop))
            {
                data.Edges.Add(new GraphEdgeData
                {
                    Id = "e" + data.Edges.Count,
                    Source = edge.SourceId,
                    Target = edge.TargetId,
                    Kind = ToCamelCase(edge.Kind.ToString()),
                    Count = Math.Max(edge.Calls.Count, 1),
                    Label = edge.Label
                });
            }

            foreach (var edge in graph.Edges.Where(e => e.Kind == EdgeKind.Calls))
            {
                foreach (var call in edge.Calls)
                {
                    data.Calls.Add(new GraphCallData
                    {
                        Source = edge.SourceId,
                        Caller = call.Caller,
                        Target = edge.TargetId,
                        Callee = call.Callee
                    });
                }
            }

            return data;
        }

        private static string ToCamelCase(string value) =>
            char.ToLowerInvariant(value[0]) + value.Substring(1);
    }

    public class GraphNodeData
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Namespace { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string? Stereotype { get; set; }
        public List<string> Members { get; set; } = new();
    }

    public class GraphEdgeData
    {
        public string Id { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public int Count { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    public class GraphCallData
    {
        public string Source { get; set; } = string.Empty;
        public string Caller { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Callee { get; set; } = string.Empty;
    }
}
