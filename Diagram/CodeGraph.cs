namespace CSharpCodeGraph.Diagram
{
    public enum NodeKind
    {
        Class,
        Interface,
        Struct,
        Record,
        RecordStruct,
        Enum,
        Delegate,
        // A JavaScript file's top-level code: its functions and variables (JavaScriptAnalyzer).
        Module,
        External
    }

    public enum MemberKind
    {
        Property,
        Field,
        Method,
        EnumValue
    }

    public enum EdgeKind
    {
        Inherits,
        Implements,
        Uses,
        Calls,
        Renders
    }

    /// <summary>
    /// Renderer-independent result of analyzing source code: types and the relationships between them.
    /// </summary>
    public class CodeGraph
    {
        public List<TypeNode> Types { get; } = new();
        public List<GraphEdge> Edges { get; } = new();
    }

    public class TypeNode
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Namespace { get; set; } = string.Empty;
        public NodeKind Kind { get; set; }
        public bool IsStatic { get; set; }
        public bool IsAbstract { get; set; }
        public List<MemberInfo> Members { get; } = new();
        // Where each of its functions is, private ones too, by the name calls use ("Run()", "new Order()", "View.init()").
        public Dictionary<string, SourceLocation> FunctionLocations { get; } = new();

        // Its constructor's name, as a caller and as a callee alike: the way code calls it, in C# and JavaScript.
        public string ConstructorName => $"new {Name}()";

        // Where the (first) declaration is: the source file's path and the 0-based line within that file.
        public string FilePath { get; set; } = string.Empty;
        public int Line { get; set; }

        public bool IsExternal => Kind == NodeKind.External;

        public string? Stereotype => Kind switch
        {
            NodeKind.Interface => "«interface»",
            NodeKind.Struct => "«struct»",
            NodeKind.Record => "«record»",
            NodeKind.RecordStruct => "«record struct»",
            NodeKind.Enum => "«enum»",
            NodeKind.Delegate => "«delegate»",
            NodeKind.Module => "«module»",
            NodeKind.Class when IsStatic => "«static»",
            NodeKind.Class when IsAbstract => "«abstract»",
            _ => null
        };
    }

    public class MemberInfo
    {
        public MemberInfo(MemberKind kind, string display)
        {
            Kind = kind;
            Display = display;
        }

        public MemberKind Kind { get; }
        public string Display { get; }

        // Where it's declared, like TypeNode's FilePath and Line; empty when it isn't known.
        public string FilePath { get; init; } = string.Empty;
        public int Line { get; init; }
    }

    public sealed record SourceLocation(string FilePath, int Line);

    /// <summary>
    /// One edge per (source, target, kind). Method calls between the same two types are
    /// aggregated into a single edge, with the individual calls listed in <see cref="Calls"/>.
    /// </summary>
    public class GraphEdge
    {
        public string SourceId { get; set; } = string.Empty;
        public string TargetId { get; set; } = string.Empty;
        public EdgeKind Kind { get; set; }
        public List<CallInfo> Calls { get; } = new();

        public bool IsSelfLoop => SourceId == TargetId;

        public string Label => Kind switch
        {
            EdgeKind.Calls => Calls.Count == 1 ? Calls[0].ToString() : $"{Calls.Count} calls",
            _ => Kind.ToString().ToLowerInvariant()
        };
    }

    public class CallInfo
    {
        public CallInfo(string caller, string callee)
        {
            Caller = caller;
            Callee = callee;
        }

        public string Caller { get; }
        public string Callee { get; }

        public override string ToString() => $"{Caller} → {Callee}";
    }
}
