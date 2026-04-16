using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using System.Text.RegularExpressions;

namespace CSharpCodeGraph
{
    public static class DiagramGenerator
    {
        public class DiagramSettings
        {
            public bool ShowInterfaces { get; set; } = true;
            public bool ShowMethodCalls { get; set; } = true;
            public bool ShowVariables { get; set; } = true;
            public bool OnlyShowMyCode { get; set; } = true;
        }

        #region Entry Points

        public static string GenerateCodeDiagram(string sourceCode, DiagramSettings? settings = null)
        {
            SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest));

            return GenerateCodeDiagram(syntaxTree, settings);
        }

        public static string GenerateCodeDiagram(SyntaxTree syntaxTree, DiagramSettings? settings = null)
        {
            settings ??= new DiagramSettings();

            var root = syntaxTree.GetCompilationUnitRoot();

            var typeDeclarations = root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .Where(t => settings.ShowInterfaces || t is not InterfaceDeclarationSyntax)
                .ToList();

            var sb = new StringBuilder();

            sb.AppendLine("digraph CSharpDiagram {");
            sb.AppendLine("rankdir=LR;");
            sb.AppendLine("nodesep=1.0;");
            sb.AppendLine("ranksep=1.5;");
            sb.AppendLine("node [shape=record, fontname=\"Consolas\", fontsize=12, margin=\"0.3,0.2\"];");

            // 1. Nodes
            foreach (var type in typeDeclarations)
            {
                sb.AppendLine(CreateTypeNode(type, settings));
            }

            // 2. Relationships
            var knownTypeNames = typeDeclarations.Select(GetFullName).ToHashSet();
            foreach (var type in typeDeclarations)
            {
                AddBaseTypes(type, sb, knownTypeNames, settings);
                AddDependencies(type, sb, knownTypeNames, settings);

                if (settings.ShowMethodCalls)
                {
                    AddMethodCalls(type, sb, knownTypeNames, settings);
                }
            }

            sb.AppendLine("}");

            return sb.ToString();
        }

        #endregion


        #region Razor Extraction

        /// <summary>
        /// Extracts C# code blocks from a Razor (.razor) file, scans the markup
        /// for referenced variables and method calls, and wraps everything in a class
        /// named after the file. If the class name already exists in
        /// <paramref name="existingClassNames"/>, the class is marked partial.
        /// </summary>
        public static string ExtractRazorCodeBlocks(string razorContent, string fileName, HashSet<string> existingClassNames)
        {
            if (string.IsNullOrWhiteSpace(razorContent))
                return string.Empty;

            // --- Step 1: Extract @code { ... } blocks and track their positions ---
            var codeBlocks = new List<string>();
            var codeBlockRanges = new List<(int Start, int End)>();
            int pos = 0;
            while (pos < razorContent.Length)
            {
                int start = razorContent.IndexOf("@code", pos, StringComparison.OrdinalIgnoreCase);
                if (start == -1)
                    break;

                int braceOpen = razorContent.IndexOf('{', start);
                if (braceOpen == -1)
                    break;

                int depth = 1;
                int i = braceOpen + 1;
                while (i < razorContent.Length && depth > 0)
                {
                    if (razorContent[i] == '{') depth++;
                    else if (razorContent[i] == '}') depth--;
                    i++;
                }

                if (depth == 0)
                {
                    var code = razorContent.Substring(braceOpen + 1, i - braceOpen - 2);
                    codeBlocks.Add(code.Trim());
                    codeBlockRanges.Add((start, i));
                    pos = i;
                }
                else
                {
                    break;
                }
            }

            // --- Step 2: Get the markup portion (everything outside @code blocks) ---
            var markupBuilder = new StringBuilder();
            int lastEnd = 0;
            foreach (var (rangeStart, rangeEnd) in codeBlockRanges)
            {
                if (rangeStart > lastEnd)
                    markupBuilder.Append(razorContent, lastEnd, rangeStart - lastEnd);
                lastEnd = rangeEnd;
            }
            if (lastEnd < razorContent.Length)
                markupBuilder.Append(razorContent, lastEnd, razorContent.Length - lastEnd);
            var markup = markupBuilder.ToString();

            // --- Step 3: Collect known members declared in @code blocks ---
            var declaredMembers = new HashSet<string>(StringComparer.Ordinal);
            var codeBlockSource = string.Join("\n", codeBlocks);
            if (!string.IsNullOrWhiteSpace(codeBlockSource))
            {
                var tempClass = $"class _Temp {{ {codeBlockSource} }}";
                var tempTree = CSharpSyntaxTree.ParseText(tempClass, new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest));
                var tempRoot = tempTree.GetCompilationUnitRoot();

                foreach (var method in tempRoot.DescendantNodes().OfType<MethodDeclarationSyntax>())
                    declaredMembers.Add(method.Identifier.Text);
                foreach (var prop in tempRoot.DescendantNodes().OfType<PropertyDeclarationSyntax>())
                    declaredMembers.Add(prop.Identifier.Text);
                foreach (var field in tempRoot.DescendantNodes().OfType<FieldDeclarationSyntax>())
                    foreach (var variable in field.Declaration.Variables)
                        declaredMembers.Add(variable.Identifier.Text);
            }

            // --- Step 4: Scan markup for C# references ---
            var referencedMethods = new HashSet<string>(StringComparer.Ordinal);
            var referencedVariables = new HashSet<string>(StringComparer.Ordinal);

            // Event handlers: @onclick="MethodName", @onchange="MethodName", etc.
            foreach (Match match in EventHandlerRegex().Matches(markup))
            {
                var name = match.Groups[1].Value.Trim();
                if (IsValidIdentifier(name) && !declaredMembers.Contains(name))
                    referencedMethods.Add(name);
            }

            // @bind="PropertyName"
            foreach (Match match in BindRegex().Matches(markup))
            {
                var name = match.Groups[1].Value.Trim();
                if (IsValidIdentifier(name) && !declaredMembers.Contains(name))
                    referencedVariables.Add(name);
            }

            // Inline expressions: @identifier or @MethodCall(...)
            foreach (Match match in InlineExpressionRegex().Matches(markup))
            {
                var name = match.Groups[1].Value;

                // Skip Razor directives and known keywords
                if (IsRazorDirective(name))
                    continue;

                if (!IsValidIdentifier(name) || declaredMembers.Contains(name))
                    continue;

                // Check if it's followed by '(' — method call
                int afterPos = match.Index + match.Length;
                if (afterPos < markup.Length && markup[afterPos] == '(')
                    referencedMethods.Add(name);
                else
                    referencedVariables.Add(name);
            }

            // --- Step 5: Build class ---
            if (codeBlocks.Count == 0 && referencedMethods.Count == 0 && referencedVariables.Count == 0)
                return string.Empty;

            var className = Path.GetFileNameWithoutExtension(fileName);
            bool isPartial = existingClassNames.Contains(className);
            var modifier = isPartial ? "partial " : "";

            var sb = new StringBuilder();
            sb.AppendLine($"public {modifier}class {className}");
            sb.AppendLine("{");

            // Emit stub members for markup references
            foreach (var method in referencedMethods.OrderBy(m => m))
                sb.AppendLine($"    private void {method}() {{ }} // referenced in markup");

            foreach (var variable in referencedVariables.OrderBy(v => v))
                sb.AppendLine($"    private object {variable}; // referenced in markup");

            if ((referencedMethods.Count > 0 || referencedVariables.Count > 0) && codeBlocks.Count > 0)
                sb.AppendLine();

            // Emit actual @code block contents
            sb.AppendLine(string.Join("\n", codeBlocks));

            sb.AppendLine("}");

            existingClassNames.Add(className);

            return sb.ToString();
        }

        /// <summary>
        /// Collects all class/struct/record/interface names from C# source code.
        /// </summary>
        public static HashSet<string> CollectClassNames(string sourceCode)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceCode, new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest));
            var root = tree.GetCompilationUnitRoot();

            return root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .Select(t => t.Identifier.Text)
                .ToHashSet();
        }

        private static bool IsValidIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (!char.IsLetter(name[0]) && name[0] != '_')
                return false;
            return name.All(c => char.IsLetterOrDigit(c) || c == '_');
        }

        private static bool IsRazorDirective(string name)
        {
            return name is "page" or "inject" or "inherits" or "layout" or "namespace"
                or "using" or "attribute" or "implements" or "typeparam" or "code"
                or "if" or "else" or "for" or "foreach" or "while" or "switch"
                or "try" or "catch" or "finally" or "lock" or "using"
                or "rendermode" or "preservewhitespace" or "key";
        }

        // Matches @on{event}="identifier" or @on{event}:preventDefault="identifier"
        private static Regex EventHandlerRegex() =>
            new(@"@on\w+(?::\w+)?\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);

        // Matches @bind="identifier" or @bind-Value="identifier"
        private static Regex BindRegex() =>
            new(@"@bind(?:-\w+)?\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);

        // Matches @identifier in markup (not inside quotes for attributes already handled)
        private static Regex InlineExpressionRegex() =>
            new(@"(?<![""\w])@([A-Za-z_]\w*)");

        #endregion


        // ---------- FULL NAME ----------
        private static string GetFullName(TypeDeclarationSyntax type)
        {
            var parts = new List<string> { type.Identifier.Text };

            SyntaxNode current = type.Parent;
            while (current != null)
            {
                if (current is NamespaceDeclarationSyntax ns)
                    parts.Insert(0, ns.Name.ToString());
                else if (current is FileScopedNamespaceDeclarationSyntax fileNs)
                    parts.Insert(0, fileNs.Name.ToString());
                else if (current is TypeDeclarationSyntax parentType)
                    parts.Insert(0, parentType.Identifier.Text);

                current = current.Parent;
            }

            return string.Join(".", parts);
        }

        // ---------- NODE CREATION ----------
        private static string CreateTypeNode(TypeDeclarationSyntax type, DiagramSettings settings)
        {
            var fullName = GetFullName(type);

            var methods = type.Members
                .OfType<MethodDeclarationSyntax>()
                .Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword))
                .Select(m =>
                {
                    var parameters = string.Join(", ", m.ParameterList.Parameters
                        .Select(p => $"{p.Type} {p.Identifier}"));
                    return $"{m.ReturnType} {m.Identifier.Text}({parameters})";
                });

            var properties = settings.ShowVariables
                ? type.Members
                    .OfType<PropertyDeclarationSyntax>()
                    .Where(p => p.Modifiers.Any(SyntaxKind.PublicKeyword))
                    .Select(p => $"{p.Type} {p.Identifier.Text}")
                : Enumerable.Empty<string>();

            var label = new StringBuilder();
            label.Append("{");
            label.Append(type.Identifier.Text);

            if (properties.Any())
            {
                label.Append("|");
                label.Append(string.Join("\\l", properties));
                label.Append("\\l");
            }

            if (methods.Any())
            {
                label.Append("|");
                label.Append(string.Join("\\l", methods));
                label.Append("\\l");
            }

            label.Append("}");

            return $"\"{fullName}\" [label=\"{label}\"];";
        }

        // ---------- BASE TYPES (Inheritance & Interfaces) ----------
        private static void AddBaseTypes(TypeDeclarationSyntax type, StringBuilder sb, HashSet<string> knownTypeNames, DiagramSettings settings)
        {
            var fullName = GetFullName(type);

            if (type.BaseList == null)
                return;

            foreach (var baseType in type.BaseList.Types)
            {
                var baseTypeName = baseType.Type.ToString();

                // Check if it's a known type in the diagram
                var matchingKnown = knownTypeNames.FirstOrDefault(k => k.EndsWith("." + baseTypeName) || k == baseTypeName);

                if (settings.OnlyShowMyCode && matchingKnown == null)
                    continue;

                var targetName = matchingKnown ?? baseTypeName;

                if (type is InterfaceDeclarationSyntax || baseType.Type is GenericNameSyntax)
                {
                    if (!settings.ShowInterfaces)
                        continue;

                    sb.AppendLine(
                        $"\"{targetName}\" -> \"{fullName}\" [style=dashed, label=\"implements\"];"
                    );
                }
                else
                {
                    // First base type on a class is typically inheritance
                    sb.AppendLine(
                        $"\"{targetName}\" -> \"{fullName}\" [label=\"inherits\"];"
                    );
                }
            }
        }

        // ---------- DEPENDENCIES ----------
        private static void AddDependencies(TypeDeclarationSyntax type, StringBuilder sb, HashSet<string> knownTypeNames, DiagramSettings settings)
        {
            var fullName = GetFullName(type);
            var dependencies = new HashSet<string>();

            if (settings.ShowVariables)
            {
                // Property types
                foreach (var prop in type.Members.OfType<PropertyDeclarationSyntax>())
                {
                    dependencies.Add(prop.Type.ToString());
                }

                // Field types
                foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
                {
                    dependencies.Add(field.Declaration.Type.ToString());
                }
            }

            // Method parameter types and return types
            foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
            {
                dependencies.Add(method.ReturnType.ToString());
                foreach (var param in method.ParameterList.Parameters)
                {
                    if (param.Type != null)
                        dependencies.Add(param.Type.ToString());
                }
            }

            // Match dependencies against known types
            foreach (var dep in dependencies)
            {
                var matchingKnown = knownTypeNames.FirstOrDefault(k => k.EndsWith("." + dep) || k == dep);
                if (matchingKnown != null && matchingKnown != fullName)
                {
                    sb.AppendLine(
                        $"\"{fullName}\" -> \"{matchingKnown}\" [label=\"uses\"];"
                    );
                }
            }
        }

        // ---------- METHOD CALLS ----------
        private static void AddMethodCalls(TypeDeclarationSyntax type, StringBuilder sb, HashSet<string> knownTypeNames, DiagramSettings settings)
        {
            var fullName = GetFullName(type);

            // Build a set of method names that exist in known types
            var knownMethods = new Dictionary<string, List<string>>();
            if (settings.OnlyShowMyCode)
            {
                var root = type.SyntaxTree.GetCompilationUnitRoot();
                foreach (var t in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var tFullName = GetFullName(t);
                    if (!knownTypeNames.Contains(tFullName)) continue;
                    foreach (var m in t.Members.OfType<MethodDeclarationSyntax>())
                    {
                        if (!knownMethods.ContainsKey(m.Identifier.Text))
                            knownMethods[m.Identifier.Text] = new List<string>();
                        knownMethods[m.Identifier.Text].Add(tFullName);
                    }
                }
            }

            foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
            {
                var callerName = method.Identifier.Text;

                var invocations = method.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>();

                var visitedCalls = new HashSet<string>();

                foreach (var invocation in invocations)
                {
                    string? calledMethodName = invocation.Expression switch
                    {
                        IdentifierNameSyntax id => id.Identifier.Text,
                        MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
                        _ => null
                    };

                    if (calledMethodName == null)
                        continue;

                    if (settings.OnlyShowMyCode)
                    {
                        // Only show calls to methods defined in known types
                        if (knownMethods.TryGetValue(calledMethodName, out var owners))
                        {
                            foreach (var ownerFullName in owners)
                            {
                                var edgeKey = $"{fullName}.{callerName}->{ownerFullName}.{calledMethodName}";
                                if (!visitedCalls.Add(edgeKey)) continue;

                                sb.AppendLine(
                                    $"\"{fullName}\" -> \"{ownerFullName}\" [style=dotted, label=\"{callerName}() -> {calledMethodName}()\"];"
                                );
                            }
                        }
                    }
                    else
                    {
                        // Look for the called method in any known type
                        foreach (var knownTypeName in knownTypeNames)
                        {
                            var edgeKey = $"{fullName}.{callerName}->{knownTypeName}.{calledMethodName}";
                            if (visitedCalls.Contains(edgeKey))
                                continue;

                            visitedCalls.Add(edgeKey);

                            sb.AppendLine(
                                $"\"{fullName}\" -> \"{knownTypeName}\" [style=dotted, label=\"{callerName}() -> {calledMethodName}()\"];"
                            );
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Generates a Cytoscape.js-compatible JSON string with compound (parent-child) nodes.
        /// Each type is a parent node, and its members are child nodes that can be expanded.
        /// Edges connect members to other types/members that reference them.
        /// </summary>
        public static string GenerateCytoscapeJson(string sourceCode, DiagramSettings? settings = null)
        {
            settings ??= new DiagramSettings();

            var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest));
            var root = syntaxTree.GetCompilationUnitRoot();

            var typeDeclarations = root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .Where(t => settings.ShowInterfaces || t is not InterfaceDeclarationSyntax)
                .ToList();

            var knownTypeNames = typeDeclarations.Select(GetFullName).ToHashSet();

            var nodes = new List<string>();
            var edges = new List<string>();

            // Build a lookup: method name -> list of (ownerFullName, methodName)
            // Used to resolve cross-type method call edges at the member level
            var methodOwnerLookup = new Dictionary<string, List<string>>();
            foreach (var type in typeDeclarations)
            {
                var fullName = GetFullName(type);
                foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
                {
                    var methodName = method.Identifier.Text;
                    if (!methodOwnerLookup.ContainsKey(methodName))
                        methodOwnerLookup[methodName] = new List<string>();
                    methodOwnerLookup[methodName].Add(fullName);
                }
            }

            // --- Nodes ---
            foreach (var type in typeDeclarations)
            {
                var fullName = GetFullName(type);
                var shortName = type.Identifier.Text;

                var kind = type switch
                {
                    InterfaceDeclarationSyntax => "interface",
                    ClassDeclarationSyntax => "class",
                    StructDeclarationSyntax => "struct",
                    RecordDeclarationSyntax => "record",
                    _ => "type"
                };

                // Parent node (the type itself) — compound node
                nodes.Add($"{{ \"data\": {{ \"id\": \"{fullName}\", \"label\": \"{shortName}\", \"kind\": \"{kind}\", \"nodeType\": \"type\" }} }}");

                // Child nodes: properties
                if (settings.ShowVariables)
                {
                    foreach (var prop in type.Members.OfType<PropertyDeclarationSyntax>())
                    {
                        var memberId = $"{fullName}.{prop.Identifier.Text}";
                        var memberLabel = $"{prop.Type} {prop.Identifier.Text}";
                        nodes.Add($"{{ \"data\": {{ \"id\": \"{Escape(memberId)}\", \"label\": \"{Escape(memberLabel)}\", \"parent\": \"{fullName}\", \"nodeType\": \"property\", \"memberKind`: \"property\" }} }}");
                    }

                    foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
                    {
                        foreach (var variable in field.Declaration.Variables)
                        {
                            var memberId = $"{fullName}.{variable.Identifier.Text}";
                            var memberLabel = $"{field.Declaration.Type} {variable.Identifier.Text}";
                            nodes.Add($"{{ \"data\": {{ \"id\": \"{Escape(memberId)}\", \"label\": \"{Escape(memberLabel)}\", \"parent\": \"{fullName}\", \"nodeType\": \"field\", \"memberKind\": \"field\" }} }}");
                        }
                    }
                }

                // Child nodes: methods
                foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
                {
                    var parameters = string.Join(", ", method.ParameterList.Parameters
                        .Select(p => $"{p.Type} {p.Identifier}"));
                    var memberId = $"{fullName}.{method.Identifier.Text}";
                    var memberLabel = $"{method.ReturnType} {method.Identifier.Text}({parameters})";
                    nodes.Add($"{{ \"data\": {{ \"id\": \"{Escape(memberId)}\", \"label\": \"{Escape(memberLabel)}\", \"parent\": \"{fullName}\", \"nodeType\": \"method\", \"memberKind\": \"method\" }} }}");
                }
            }

            // --- Edges: inheritance & interfaces ---
            foreach (var type in typeDeclarations)
            {
                var fullName = GetFullName(type);
                if (type.BaseList == null) continue;

                foreach (var baseType in type.BaseList.Types)
                {
                    var baseTypeName = baseType.Type.ToString();
                    var matchingKnown = knownTypeNames.FirstOrDefault(k => k.EndsWith("." + baseTypeName) || k == baseTypeName);

                    if (settings.OnlyShowMyCode && matchingKnown == null)
                        continue;

                    var targetName = matchingKnown ?? baseTypeName;

                    if (type is InterfaceDeclarationSyntax || baseType.Type is GenericNameSyntax)
                    {
                        if (!settings.ShowInterfaces) continue;
                        edges.Add($"{{ \"data\": {{ \"source\": \"{targetName}\", \"target\": \"{fullName}\", \"label\": \"implements\", \"edgeType\": \"implements\" }} }}");
                    }
                    else
                    {
                        edges.Add($"{{ \"data\": {{ \"source\": \"{targetName}\", \"target\": \"{fullName}\", \"label\": \"inherits\", \"edgeType\": \"inherits\" }} }}");
                    }
                }
            }

            // --- Edges: dependencies (type-level) ---
            foreach (var type in typeDeclarations)
            {
                var fullName = GetFullName(type);
                var dependencies = new HashSet<string>();

                if (settings.ShowVariables)
                {
                    foreach (var prop in type.Members.OfType<PropertyDeclarationSyntax>())
                        dependencies.Add(prop.Type.ToString());
                    foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
                        dependencies.Add(field.Declaration.Type.ToString());
                }

                foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
                {
                    dependencies.Add(method.ReturnType.ToString());
                    foreach (var param in method.ParameterList.Parameters)
                        if (param.Type != null)
                            dependencies.Add(param.Type.ToString());
                }

                foreach (var dep in dependencies)
                {
                    var matchingKnown = knownTypeNames.FirstOrDefault(k => k.EndsWith("." + dep) || k == dep);
                    if (matchingKnown != null && matchingKnown != fullName)
                    {
                        edges.Add($"{{ \"data\": {{ \"source\": \"{fullName}\", \"target\": \"{matchingKnown}\", \"label\": \"uses\", \"edgeType\": \"uses\" }} }}");
                    }
                }
            }

            // --- Edges: method calls (member-to-member level) ---
            if (settings.ShowMethodCalls)
            {
                foreach (var type in typeDeclarations)
                {
                    var fullName = GetFullName(type);
                    foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
                    {
                        var callerName = method.Identifier.Text;
                        var callerId = $"{fullName}.{callerName}";
                        var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>();
                        var visitedCalls = new HashSet<string>();

                        foreach (var invocation in invocations)
                        {
                            string? calledMethodName = invocation.Expression switch
                            {
                                IdentifierNameSyntax id => id.Identifier.Text,
                                MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
                                _ => null
                            };

                            if (calledMethodName == null) continue;

                            // Find which types own this method
                            if (methodOwnerLookup.TryGetValue(calledMethodName, out var owners))
                            {
                                foreach (var ownerFullName in owners)
                                {
                                    var targetId = $"{ownerFullName}.{calledMethodName}";
                                    var edgeKey = $"{callerId}->{targetId}";
                                    if (!visitedCalls.Add(edgeKey)) continue;

                                    // Skip self-references to the exact same member
                                    if (callerId == targetId) continue;

                                    edges.Add($"{{ \"data\": {{ \"source\": \"{Escape(callerId)}\", \"target\": \"{Escape(targetId)}\", \"label\": \"{callerName}() → {calledMethodName}()\", \"edgeType\": \"calls\" }} }}");
                                }
                            }
                        }
                    }
                }
            }

            return $"[{string.Join(",\n", nodes.Concat(edges))}]";
        }

        private static string Escape(string value) => value.Replace("\"", "\\\"");
    }
}