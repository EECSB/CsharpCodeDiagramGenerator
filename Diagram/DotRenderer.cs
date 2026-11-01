using System.Text;

namespace CSharpCodeGraph.Diagram
{
    /// <summary>
    /// Renders a <see cref="CodeGraph"/> as Graphviz DOT.
    /// </summary>
    public static class DotRenderer
    {
        private const int MaxCallsInTooltip = 40;

        // Above this many edges, layout speed matters more than label placement (see Render).
        public const int LargeGraphEdgeCount = 100;

        public static string Render(CodeGraph graph)
        {
            var sb = new StringBuilder();

            var edges = graph.Edges.Where(e => !e.IsSelfLoop).ToList();
            bool isLarge = edges.Count > LargeGraphEdgeCount;

            sb.AppendLine("digraph CSharpDiagram {");
            sb.AppendLine("rankdir=LR;");
            sb.AppendLine("nodesep=1.0;");
            sb.AppendLine("ranksep=1.5;");
            if (isLarge)
            {
                // Fewer crossing-minimization passes: measured about 2x faster on dense graphs.
                sb.AppendLine("mclimit=0.1;");
            }
            sb.AppendLine("node [shape=record, fontname=\"Consolas\", fontsize=12, margin=\"0.3,0.2\"];");
            sb.AppendLine("edge [fontname=\"Consolas\", fontsize=10];");

            foreach (var type in graph.Types)
            {
                sb.AppendLine(CreateTypeNode(type));
            }

            // Edge labels take part in the layout and make it about 10x slower on large graphs;
            // external labels (xlabel) are placed afterwards instead. A type calling its own methods
            // isn't a relationship between types, so self loops were left out above.
            var labelAttribute = isLarge ? "xlabel" : "label";
            foreach (var edge in edges)
            {
                sb.AppendLine(CreateEdge(edge, labelAttribute));
            }

            sb.AppendLine("}");

            return sb.ToString();
        }

        // ---------- NODES ----------
        private static string CreateTypeNode(TypeNode type)
        {
            if (type.IsExternal)
                return $"\"{EscapeString(type.Id)}\" [label=\"{EscapeRecordText(type.Name)}\", style=dashed, color=gray50, fontcolor=gray40];";

            var label = new StringBuilder();
            label.Append('{');

            if (type.Stereotype != null)
            {
                label.Append(EscapeRecordText(type.Stereotype));
                label.Append("\\n");
            }
            label.Append(EscapeRecordText(type.Name));

            AppendSection(label, type.Members.Where(m => m.Kind != MemberKind.Method));
            AppendSection(label, type.Members.Where(m => m.Kind == MemberKind.Method));

            label.Append('}');

            return $"\"{EscapeString(type.Id)}\" [label=\"{label}\"];";
        }

        private static void AppendSection(StringBuilder label, IEnumerable<MemberInfo> members)
        {
            var lines = members.Select(m => EscapeRecordText(m.Display)).ToList();
            if (lines.Count == 0)
                return;

            label.Append('|');
            label.Append(string.Join("\\l", lines));
            label.Append("\\l");
        }

        // ---------- EDGES ----------
        // Inheritance arrows point from the derived type to its base, as in UML.
        private static string CreateEdge(GraphEdge edge, string labelAttribute)
        {
            var attributes = edge.Kind switch
            {
                EdgeKind.Inherits => $"arrowhead=empty, {labelAttribute}=\"inherits\"",
                EdgeKind.Implements => $"style=dashed, arrowhead=empty, {labelAttribute}=\"implements\"",
                EdgeKind.Uses => $"{labelAttribute}=\"uses\"",
                EdgeKind.Renders => $"style=bold, {labelAttribute}=\"renders\"",
                EdgeKind.Calls => CreateCallAttributes(edge, labelAttribute),
                _ => string.Empty
            };

            return $"\"{EscapeString(edge.SourceId)}\" -> \"{EscapeString(edge.TargetId)}\" [{attributes}];";
        }

        private static string CreateCallAttributes(GraphEdge edge, string labelAttribute)
        {
            var calls = edge.Calls.Select(c => c.ToString()).ToList();
            var label = edge.Label;

            var tooltipLines = calls.Take(MaxCallsInTooltip).ToList();
            if (calls.Count > MaxCallsInTooltip)
                tooltipLines.Add($"… and {calls.Count - MaxCallsInTooltip} more");

            var tooltip = string.Join("\\n", tooltipLines.Select(EscapeString));

            return $"style=dotted, {labelAttribute}=\"{EscapeString(label)}\", tooltip=\"{tooltip}\"";
        }

        // ---------- ESCAPING ----------
        // Inside a quoted DOT string only backslashes and quotes need escaping.
        private static string EscapeString(string value) =>
            value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        // Record labels additionally treat { } | < > as structure, so generics like List<T> must be escaped.
        private static string EscapeRecordText(string value)
        {
            var sb = new StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                if (c is '\\' or '"' or '{' or '}' or '|' or '<' or '>')
                    sb.Append('\\');
                sb.Append(c);
            }

            return sb.ToString();
        }
    }
}
