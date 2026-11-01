using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpCodeGraph.Diagram
{
    /// <summary>
    /// Builds a <see cref="CodeGraph"/> from C# source files, and JavaScript files with <see cref="JavaScriptAnalyzer"/>.
    /// Every C# file is parsed as its own syntax tree and all trees go into one compilation without
    /// framework references. That is enough for the semantic model to resolve which of the user's
    /// types a call or a type reference points to; framework types simply stay unresolved.
    /// </summary>
    public static class CodeAnalyzer
    {
        private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

        // How long AnalyzeAsync works before giving the browser a turn.
        private const int YieldIntervalMs = 50;

        #region Entry Points

        public static CodeGraph Analyze(IReadOnlyList<SourceFile> files, DiagramSettings? settings = null)
        {
            var builder = new GraphBuilder(files, settings ?? new DiagramSettings());
            foreach (var _ in builder.Run())
            {
            }

            return builder.Graph;
        }

        public static CodeGraph Analyze(string sourceCode, DiagramSettings? settings = null) =>
            Analyze(SourceBundle.Split(sourceCode), settings);

        /// <summary>
        /// Same result as <see cref="Analyze(IReadOnlyList{SourceFile}, DiagramSettings?)"/>, but in
        /// WebAssembly there is only one thread: this version pauses every few milliseconds so the page
        /// can repaint (progress) and handle clicks (cancel) while a large project is analyzed.
        /// </summary>
        public static async Task<CodeGraph> AnalyzeAsync(
            IReadOnlyList<SourceFile> files,
            DiagramSettings? settings = null,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var builder = new GraphBuilder(files, settings ?? new DiagramSettings());
            var stopwatch = Stopwatch.StartNew();

            foreach (var step in builder.Run())
            {
                if (stopwatch.ElapsedMilliseconds < YieldIntervalMs)
                    continue;

                progress?.Report(step);
                // Task.Delay is a browser timer in WebAssembly, so it really hands control back to the page.
                await Task.Delay(1, cancellationToken);
                stopwatch.Restart();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return builder.Graph;
        }

        #endregion


        private class GraphBuilder
        {
            private static readonly SymbolDisplayFormat TypeIdFormat = new(
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

            private static readonly SymbolDisplayFormat TypeNameFormat = new(
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

            private static readonly SymbolDisplayFormat MemberFormat = new(
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                memberOptions: SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeRef,
                parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeParamsRefOut,
                miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

            // Framework types that are never worth a node of their own, even when showing external code.
            private static readonly HashSet<string> PredefinedTypeNames = new(StringComparer.Ordinal)
            {
                "Object", "String", "Boolean", "Char", "Byte", "SByte", "Int16", "UInt16", "Int32", "UInt32",
                "Int64", "UInt64", "Single", "Double", "Decimal", "IntPtr", "UIntPtr", "Void", "Nullable", "ValueTuple"
            };

            private readonly IReadOnlyList<SourceFile> files;
            private readonly DiagramSettings settings;
            private readonly CodeGraph graph = new();
            private CSharpCompilation compilation = null!;

            private readonly List<INamedTypeSymbol> sourceTypes = new();
            private readonly Dictionary<INamedTypeSymbol, TypeNode> sourceNodes = new(SymbolEqualityComparer.Default);
            private readonly Dictionary<INamedTypeSymbol, List<MemberDeclarationSyntax>> declarations = new(SymbolEqualityComparer.Default);
            // Top-level statements (Program.cs without a class): the compiler puts them in a Program class of its own.
            private readonly List<(TypeNode Node, CompilationUnitSyntax Unit)> topLevelCode = new();
            private readonly Dictionary<string, TypeNode> externalNodes = new(StringComparer.Ordinal);
            private readonly HashSet<string> nodeIds = new(StringComparer.Ordinal);
            private readonly Dictionary<(string Source, string Target, EdgeKind Kind), GraphEdge> edges = new();
            private readonly HashSet<(GraphEdge Edge, string Caller, string Callee)> recordedCalls = new();
            private readonly Dictionary<SyntaxTree, SemanticModel> models = new();
            private readonly Dictionary<SyntaxTree, List<int>> lineStarts = new();
            private JavaScriptResult javaScript = new();

            // Used to skip binding calls and creations that can't possibly target the user's code.
            private readonly HashSet<string> sourceMethodNames = new(StringComparer.Ordinal);
            private readonly HashSet<string> sourceTypeNames = new(StringComparer.Ordinal);

            public GraphBuilder(IReadOnlyList<SourceFile> files, DiagramSettings settings)
            {
                this.files = files;
                this.settings = settings;
            }

            public CodeGraph Graph => graph;

            /// <summary>
            /// Does the analysis in small steps, yielding a progress message after each one.
            /// </summary>
            public IEnumerable<string> Run()
            {
                // JavaScript first, so C# calls into it (IJSRuntime) can be matched while C# is analyzed.
                if (files.Any(f => JavaScriptAnalyzer.IsJavaScriptFile(f.Path)))
                {
                    javaScript = JavaScriptAnalyzer.Analyze(files, settings);
                    foreach (var node in javaScript.Types)
                        AddNode(node);
                    yield return "Reading JavaScript files...";
                }

                var csharpFiles = files.Where(f => !JavaScriptAnalyzer.IsJavaScriptFile(f.Path)).ToList();
                var trees = new List<SyntaxTree>(csharpFiles.Count);
                for (int i = 0; i < csharpFiles.Count; i++)
                {
                    trees.Add(CSharpSyntaxTree.ParseText(csharpFiles[i].Content, ParseOptions, path: csharpFiles[i].Path));
                    yield return $"Parsing files ({i + 1}/{csharpFiles.Count})...";
                }

                compilation = CSharpCompilation.Create(
                    "CodeGraph",
                    trees,
                    references: null,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, concurrentBuild: false));

                foreach (var tree in trees)
                {
                    CollectTypes(tree);
                    yield return "Finding types...";
                }
                CollectMethodNames();

                for (int i = 0; i < sourceTypes.Count; i++)
                {
                    var type = sourceTypes[i];
                    var node = sourceNodes[type];
                    AddBaseTypes(type, node);
                    AddDependencies(type, node);
                    AddBodyReferences(type, node);
                    yield return $"Analyzing types ({i + 1}/{sourceTypes.Count})...";
                }

                foreach (var (node, unit) in topLevelCode)
                {
                    AddTopLevelReferences(node, unit);
                    yield return "Analyzing top-level statements...";
                }

                AddJavaScriptReferences();
            }

            // ---------- NODES ----------
            private void CollectTypes(SyntaxTree tree)
            {
                var model = GetModel(tree);
                var root = tree.GetCompilationUnitRoot();
                var typeDeclarations = root
                    .DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax)
                    .OfType<MemberDeclarationSyntax>()
                    .Where(m => m is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);

                foreach (var declaration in typeDeclarations)
                {
                    // Code that doesn't parse can leave declarations without a name; there's nothing to draw for those.
                    var identifier = GetIdentifier(declaration);
                    if (identifier.IsMissing || identifier.ValueText.Length == 0)
                        continue;

                    if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type)
                        continue;

                    if (!settings.ShowInterfaces && type.TypeKind == TypeKind.Interface)
                        continue;

                    // Partial types: every declaration maps to the same symbol, so they share one node.
                    AddSourceType(type, identifier.GetLocation());
                    declarations[type].Add(declaration);
                }

                if (root.Members.OfType<GlobalStatementSyntax>().FirstOrDefault() is { } firstStatement
                    && model.GetDeclaredSymbol(root) is IMethodSymbol { ContainingType: { } program })
                {
                    var node = AddSourceType(program, firstStatement.GetLocation());
                    topLevelCode.Add((node, root));
                }

                // A using alias is another name for a type (using Repo = App.Data.OrderRepository;), so new Repo()
                // can create one of the user's types too.
                foreach (var directive in root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<UsingDirectiveSyntax>())
                {
                    if (directive.Alias != null)
                        sourceTypeNames.Add(directive.Alias.Name.Identifier.ValueText);
                }
            }

            private TypeNode AddSourceType(INamedTypeSymbol type, Location location)
            {
                if (!sourceNodes.TryGetValue(type, out var node))
                {
                    node = CreateTypeNode(type);
                    node.FilePath = location.SourceTree?.FilePath ?? string.Empty;
                    node.Line = GetLine(location);

                    sourceTypes.Add(type);
                    sourceNodes[type] = node;
                    declarations[type] = new List<MemberDeclarationSyntax>();
                    sourceTypeNames.Add(type.Name);
                    AddNode(node);
                }

                return node;
            }

            // Every node needs an id of its own (the diagram can't draw two elements with one id), but two types
            // can print the same: a class and a record both called App.Models.User in different files, a nested
            // type Shop.Orders.Item next to namespace Shop.Orders's Item, or one .js path twice. A repeat gets a
            // number, here, before any edge can refer to the node by its id.
            private void AddNode(TypeNode node)
            {
                var id = node.Id;
                for (int n = 2; !nodeIds.Add(node.Id); n++)
                    node.Id = $"{id}~{n}";

                graph.Types.Add(node);
            }

            private static SyntaxToken GetIdentifier(MemberDeclarationSyntax declaration) => declaration switch
            {
                BaseTypeDeclarationSyntax type => type.Identifier,
                DelegateDeclarationSyntax @delegate => @delegate.Identifier,
                _ => default
            };

            private void CollectMethodNames()
            {
                foreach (var type in sourceTypes)
                {
                    foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                    {
                        if (method.MethodKind == MethodKind.Ordinary)
                            sourceMethodNames.Add(method.Name);
                    }
                }
            }

            private TypeNode CreateTypeNode(INamedTypeSymbol type)
            {
                var node = new TypeNode
                {
                    Id = type.ToDisplayString(TypeIdFormat),
                    Name = type.ToDisplayString(TypeNameFormat),
                    Namespace = type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : string.Empty,
                    Kind = type.TypeKind switch
                    {
                        TypeKind.Interface => NodeKind.Interface,
                        TypeKind.Enum => NodeKind.Enum,
                        TypeKind.Delegate => NodeKind.Delegate,
                        TypeKind.Struct => type.IsRecord ? NodeKind.RecordStruct : NodeKind.Struct,
                        _ => type.IsRecord ? NodeKind.Record : NodeKind.Class
                    },
                    IsStatic = type.IsStatic,
                    IsAbstract = type.IsAbstract && type.TypeKind == TypeKind.Class
                };

                // A delegate's members are all compiler-generated; its signature is what matters.
                if (type.DelegateInvokeMethod is { } invoke)
                {
                    node.Members.Add(new MemberInfo(MemberKind.Method, invoke.ToDisplayString(MemberFormat)));
                    return node;
                }

                foreach (var member in type.GetMembers())
                {
                    if (member.IsImplicitlyDeclared)
                        continue;

                    if (type.TypeKind == TypeKind.Enum)
                    {
                        if (member is IFieldSymbol enumValue)
                            node.Members.Add(CreateMember(MemberKind.EnumValue, enumValue.Name, member));
                        continue;
                    }

                    // Interface members are implicitly public, which the symbol reports correctly.
                    if (member.DeclaredAccessibility != Accessibility.Public)
                        continue;

                    switch (member)
                    {
                        case IPropertySymbol property when settings.ShowVariables:
                            node.Members.Add(CreateMember(MemberKind.Property, property.ToDisplayString(MemberFormat), member));
                            break;
                        case IFieldSymbol field when settings.ShowVariables:
                            node.Members.Add(CreateMember(MemberKind.Field, field.ToDisplayString(MemberFormat), member));
                            break;
                        case IMethodSymbol { MethodKind: MethodKind.Ordinary } method:
                            node.Members.Add(CreateMember(MemberKind.Method, method.ToDisplayString(MemberFormat), member));
                            break;
                    }
                }

                return node;
            }

            private MemberInfo CreateMember(MemberKind kind, string display, ISymbol symbol)
            {
                var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                return location == null
                    ? new MemberInfo(kind, display)
                    : new MemberInfo(kind, display) { FilePath = location.SourceTree!.FilePath, Line = GetLine(location) };
            }

            private TypeNode GetExternalNode(INamedTypeSymbol type) =>
                GetExternalNode(type.Arity > 0 ? $"{type.Name}<{new string(',', type.Arity - 1)}>" : type.Name);

            private TypeNode GetExternalNode(string name)
            {
                var id = "ext:" + name;
                if (!externalNodes.TryGetValue(id, out var node))
                {
                    node = new TypeNode { Id = id, Name = name, Kind = NodeKind.External };
                    externalNodes[id] = node;
                    AddNode(node);
                }

                return node;
            }

            /// <summary>
            /// Maps a type to its node: one of the user's types, or (when external code is shown)
            /// a node for an unresolved framework/library type. Returns null for anything else.
            /// </summary>
            private TypeNode? ResolveNode(ITypeSymbol? type)
            {
                if (type is not INamedTypeSymbol named)
                    return null;

                named = named.OriginalDefinition;

                if (sourceNodes.TryGetValue(named, out var node))
                    return node;

                // Without references every framework type is an error type. Anything else here is a
                // user type that the settings filtered out (e.g. an interface), which stays hidden.
                if (settings.OnlyShowMyCode || named.TypeKind != TypeKind.Error || !IsNameableExternal(named))
                    return null;

                return GetExternalNode(named);
            }

            private static bool IsNameableExternal(INamedTypeSymbol type) =>
                type.Name.Length > 0
                && type.SpecialType == SpecialType.None
                && !type.IsTupleType
                && !PredefinedTypeNames.Contains(type.Name);

            // ---------- BASE TYPES (Inheritance & Interfaces) ----------
            private void AddBaseTypes(INamedTypeSymbol type, TypeNode node)
            {
                // The declared base lists are used rather than the symbol's interface list, which also
                // contains compiler-added interfaces (e.g. IEquatable<T> on records).
                foreach (var declaration in declarations[type].OfType<BaseTypeDeclarationSyntax>())
                {
                    if (declaration.BaseList == null)
                        continue;

                    var model = GetModel(declaration.SyntaxTree);
                    var baseTypes = declaration.BaseList.Types;

                    for (int i = 0; i < baseTypes.Count; i++)
                    {
                        if (model.GetTypeInfo(baseTypes[i].Type).Type is not INamedTypeSymbol baseType)
                            continue;

                        var target = ResolveNode(baseType);
                        if (target == null || target == node)
                            continue;

                        var kind = IsInterface(type, baseType, i) ? EdgeKind.Implements : EdgeKind.Inherits;
                        if (kind == EdgeKind.Implements && !settings.ShowInterfaces)
                            continue;

                        GetOrAddEdge(node, target, kind);
                    }
                }
            }

            private static bool IsInterface(INamedTypeSymbol derived, INamedTypeSymbol baseType, int position)
            {
                if (baseType.TypeKind == TypeKind.Interface)
                    return true;
                if (baseType.TypeKind == TypeKind.Class)
                    return false;

                // Unresolved framework type: only the first base type of a class can be a class.
                if (derived.TypeKind != TypeKind.Class || position > 0)
                    return true;

                var name = baseType.Name;
                return name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]);
            }

            // ---------- DEPENDENCIES ----------
            private void AddDependencies(INamedTypeSymbol type, TypeNode node)
            {
                if (type.TypeKind == TypeKind.Enum)
                    return;

                var targets = new HashSet<TypeNode>();

                if (type.DelegateInvokeMethod is { } invoke)
                {
                    CollectReferencedNodes(invoke.ReturnType, targets);
                    foreach (var parameter in invoke.Parameters)
                        CollectReferencedNodes(parameter.Type, targets);
                }

                foreach (var member in type.GetMembers())
                {
                    if (member.IsImplicitlyDeclared)
                        continue;

                    switch (member)
                    {
                        case IPropertySymbol property when settings.ShowVariables:
                            CollectReferencedNodes(property.Type, targets);
                            foreach (var parameter in property.Parameters)
                                CollectReferencedNodes(parameter.Type, targets);
                            break;
                        case IFieldSymbol field when settings.ShowVariables:
                            CollectReferencedNodes(field.Type, targets);
                            break;
                        case IEventSymbol @event when settings.ShowVariables:
                            CollectReferencedNodes(@event.Type, targets);
                            break;
                        case IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.Constructor } method:
                            CollectReferencedNodes(method.ReturnType, targets);
                            foreach (var parameter in method.Parameters)
                                CollectReferencedNodes(parameter.Type, targets);
                            break;
                    }
                }

                foreach (var target in targets)
                {
                    if (target != node)
                        GetOrAddEdge(node, target, EdgeKind.Uses);
                }
            }

            // Walks into arrays, pointers, tuples and type arguments, so List<Order> or Order[] find Order.
            private void CollectReferencedNodes(ITypeSymbol type, HashSet<TypeNode> into)
            {
                switch (type)
                {
                    case IArrayTypeSymbol array:
                        CollectReferencedNodes(array.ElementType, into);
                        break;
                    case IPointerTypeSymbol pointer:
                        CollectReferencedNodes(pointer.PointedAtType, into);
                        break;
                    case INamedTypeSymbol named:
                        if (ResolveNode(named) is { } node)
                            into.Add(node);

                        if (named.IsTupleType)
                        {
                            foreach (var element in named.TupleElements)
                                CollectReferencedNodes(element.Type, into);
                        }
                        else
                        {
                            foreach (var argument in named.TypeArguments)
                                CollectReferencedNodes(argument, into);
                        }
                        break;
                }
            }

            // ---------- METHOD CALLS & RAZOR CHILD COMPONENTS ----------
            private void AddBodyReferences(INamedTypeSymbol type, TypeNode node)
            {
                foreach (var declaration in declarations[type].OfType<TypeDeclarationSyntax>())
                {
                    var model = GetModel(declaration.SyntaxTree);

                    foreach (var member in declaration.Members)
                    {
                        // Nested types are nodes of their own.
                        if (member is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                            continue;

                        var callerName = GetMemberName(member, node);
                        var location = GetNameLocation(member);
                        node.FunctionLocations.TryAdd(callerName, new SourceLocation(location.SourceTree!.FilePath, GetLine(location)));

                        foreach (var expression in member.DescendantNodes().OfType<ExpressionSyntax>())
                        {
                            if (expression is InvocationExpressionSyntax invocation)
                                AddInvocation(node, callerName, invocation, model);
                            else if (expression is BaseObjectCreationExpressionSyntax creation && settings.ShowMethodCalls)
                                AddObjectCreation(node, callerName, creation, model);
                        }
                    }
                }

                // A constructor that isn't written out (a record's, a primary one or the implicit one) is at the type's declaration.
                node.FunctionLocations.TryAdd(node.ConstructorName, new SourceLocation(node.FilePath, node.Line));
            }

            private void AddInvocation(TypeNode node, string callerName, InvocationExpressionSyntax invocation, SemanticModel model)
            {
                var calledName = GetInvokedName(invocation.Expression);
                if (calledName == null)
                    return;

                // Razor child components, emitted by RazorExtractor (and by the Razor compiler) as __builder.OpenComponent<T>().
                if (calledName == "OpenComponent" && GetGenericName(invocation.Expression) is { TypeArgumentList.Arguments.Count: 1 } generic)
                {
                    var componentType = generic.TypeArgumentList.Arguments[0];
                    var child = ResolveSourceNode(model.GetTypeInfo(componentType).Type)
                        ?? model.GetSymbolInfo(componentType).CandidateSymbols.OfType<INamedTypeSymbol>().Select(ResolveSourceNode).FirstOrDefault(n => n != null)
                        ?? FindComponentByTagName(componentType);

                    if (child != null && child != node)
                        GetOrAddEdge(node, child, EdgeKind.Renders);
                    return;
                }

                if (!settings.ShowMethodCalls)
                    return;

                // Blazor's JS interop: jsRuntime.InvokeVoidAsync("CodeGraphView.render", ...) calls into a JavaScript file.
                if (calledName is "InvokeAsync" or "InvokeVoidAsync" or "Invoke" or "InvokeVoid"
                    && invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal
                    && literal.IsKind(SyntaxKind.StringLiteralExpression)
                    && javaScript.TryResolveInteropCall(literal.Token.ValueText, out var script, out var function)
                    && IsCalledOnJSRuntime(invocation, model))
                {
                    AddCall(node, script!, callerName, function);
                    return;
                }

                if (settings.OnlyShowMyCode && !sourceMethodNames.Contains(calledName))
                    return;

                var symbolInfo = model.GetSymbolInfo(invocation);
                var method = symbolInfo.Symbol as IMethodSymbol
                    ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

                if (method != null)
                {
                    if (method.MethodKind is MethodKind.LocalFunction or MethodKind.DelegateInvoke)
                        return;

                    if (ResolveNode(method.ContainingType) is { } target)
                        AddCall(node, target, callerName, method.Name + "()");
                    return;
                }

                // Unresolved call: when external code is shown, attribute it to the receiver's type
                // (e.g. a List<int> field) or, for static calls like Console.WriteLine, to the type name.
                if (!settings.OnlyShowMyCode && invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    var target = ResolveNode(model.GetTypeInfo(memberAccess.Expression).Type);
                    if (target == null && GetRightmostName(memberAccess.Expression) is { } typeName
                        && char.IsUpper(typeName[0]) && model.GetSymbolInfo(memberAccess.Expression).Symbol == null)
                    {
                        target = GetExternalNode(typeName);
                    }

                    if (target is { IsExternal: true })
                        AddCall(node, target, callerName, calledName + "()");
                }
            }

            // Top-level statements have no signatures to take dependencies from, so the types named in
            // the code count instead: generic arguments (services.AddScoped<IRepo, Repo>()) and typeof(T).
            private void AddTopLevelReferences(TypeNode node, CompilationUnitSyntax unit)
            {
                const string callerName = "Main()";
                var model = GetModel(unit.SyntaxTree);
                var usedTypes = new HashSet<TypeNode>();

                // Main() starts at the first statement; Program's node can be a partial class declared before it.
                var start = unit.Members.OfType<GlobalStatementSyntax>().First().GetLocation();
                node.FunctionLocations.TryAdd(callerName, new SourceLocation(unit.SyntaxTree.FilePath, GetLine(start)));

                foreach (var statement in unit.Members.OfType<GlobalStatementSyntax>())
                {
                    foreach (var expression in statement.DescendantNodes().OfType<ExpressionSyntax>())
                    {
                        switch (expression)
                        {
                            case InvocationExpressionSyntax invocation:
                                AddInvocation(node, callerName, invocation, model);
                                if (GetGenericName(invocation.Expression) is { } generic)
                                {
                                    foreach (var argument in generic.TypeArgumentList.Arguments)
                                    {
                                        if (model.GetTypeInfo(argument).Type is { } argumentType)
                                            CollectReferencedNodes(argumentType, usedTypes);
                                    }
                                }
                                break;
                            case BaseObjectCreationExpressionSyntax creation when settings.ShowMethodCalls:
                                AddObjectCreation(node, callerName, creation, model);
                                break;
                            case TypeOfExpressionSyntax typeOf:
                                if (model.GetTypeInfo(typeOf.Type).Type is { } type)
                                    CollectReferencedNodes(type, usedTypes);
                                break;
                        }
                    }
                }

                foreach (var target in usedTypes)
                {
                    if (target != node)
                        GetOrAddEdge(node, target, EdgeKind.Uses);
                }
            }

            private void AddObjectCreation(TypeNode node, string callerName, BaseObjectCreationExpressionSyntax creation, SemanticModel model)
            {
                if (settings.OnlyShowMyCode && creation is ObjectCreationExpressionSyntax explicitCreation
                    && GetRightmostName(explicitCreation.Type) is { } typeName && !sourceTypeNames.Contains(typeName))
                {
                    return;
                }

                if (ResolveNode(model.GetTypeInfo(creation).Type) is { } target)
                    AddCall(node, target, callerName, target.ConstructorName);
            }

            private TypeNode? ResolveSourceNode(ITypeSymbol? type) =>
                type is INamedTypeSymbol named && sourceNodes.TryGetValue(named.OriginalDefinition, out var node) ? node : null;

            // Uploads are often partial (no _Imports.razor with the right @using), so a component tag
            // that doesn't bind still counts when exactly one class has that name.
            private TypeNode? FindComponentByTagName(TypeSyntax componentType)
            {
                var name = GetRightmostName(componentType);
                var matches = sourceTypes.Where(t => t.Name == name && t.TypeKind == TypeKind.Class).Take(2).ToList();
                return matches.Count == 1 ? sourceNodes[matches[0]] : null;
            }

            // ---------- JAVASCRIPT ----------
            private void AddJavaScriptReferences()
            {
                foreach (var link in javaScript.Links)
                {
                    var target = link.Target ?? (link.ExternalName != null && !settings.OnlyShowMyCode ? GetExternalNode(link.ExternalName) : null);
                    if (target == null)
                        continue;

                    if (link.Kind == EdgeKind.Calls)
                        AddCall(link.Source, target, link.Caller ?? "?", link.Callee ?? "?");
                    else if (target != link.Source)
                        GetOrAddEdge(link.Source, target, link.Kind);
                }

                // dotNetRef.invokeMethodAsync("Name") calls the [JSInvokable] method that JavaScript knows by that name
                // (the attribute's argument, or else the method's own name), when only one type has one.
                if (javaScript.DotNetCalls.Count == 0)
                    return;

                var invokable = new List<(string Identifier, TypeNode Node, string Callee)>();
                foreach (var type in sourceTypes)
                {
                    foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                    {
                        foreach (var attribute in method.GetAttributes())
                        {
                            if (attribute.AttributeClass?.Name is "JSInvokable" or "JSInvokableAttribute"
                                && GetJSInvokableIdentifier(attribute, method) is { } identifier)
                            {
                                invokable.Add((identifier, sourceNodes[type], method.Name + "()"));
                            }
                        }
                    }
                }

                foreach (var call in javaScript.DotNetCalls)
                {
                    var matches = invokable.Where(m => m.Identifier == call.MethodName).Select(m => (m.Node, m.Callee)).Distinct().ToList();
                    if (matches.Count == 1)
                        AddCall(call.Source, matches[0].Node, call.Caller, matches[0].Callee);
                }
            }

            // [JSInvokable("Name")]'s argument, or else the method's name. Without references the attribute's constructor
            // isn't known, so neither are its arguments: the identifier is read from the code.
            private string? GetJSInvokableIdentifier(AttributeData attribute, IMethodSymbol method)
            {
                var syntax = attribute.ApplicationSyntaxReference?.GetSyntax() as AttributeSyntax;
                if (syntax?.ArgumentList?.Arguments.FirstOrDefault() is not { } argument)
                    return method.Name;

                return GetModel(syntax.SyntaxTree).GetConstantValue(argument.Expression).Value as string;
            }

            // Only the JS runtime calls JavaScript's globals; an IJSObjectReference calls its module's exports. Without
            // references a module reference isn't always known as one (var module = await js.InvokeAsync<IJSObjectReference>(...)
            // has no type), so it's the runtime that's looked for.
            private static bool IsCalledOnJSRuntime(InvocationExpressionSyntax invocation, SemanticModel model)
            {
                var receiver = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax memberAccess => memberAccess.Expression,
                    MemberBindingExpressionSyntax => (invocation.Parent as ConditionalAccessExpressionSyntax)?.Expression,
                    _ => null
                };
                if (receiver == null || model.GetTypeInfo(receiver).Type is not INamedTypeSymbol type)
                    return false;

                // Without references IJSRuntime? is taken for Nullable<IJSRuntime>.
                if (type is { Name: "Nullable", TypeArguments.Length: 1 } && type.TypeArguments[0] is INamedTypeSymbol underlying)
                    type = underlying;

                return type.Name is "IJSRuntime" or "IJSInProcessRuntime";
            }

            // ---------- EDGES ----------
            private GraphEdge GetOrAddEdge(TypeNode source, TypeNode target, EdgeKind kind)
            {
                var key = (source.Id, target.Id, kind);
                if (!edges.TryGetValue(key, out var edge))
                {
                    edge = new GraphEdge { SourceId = source.Id, TargetId = target.Id, Kind = kind };
                    edges[key] = edge;
                    graph.Edges.Add(edge);
                }

                return edge;
            }

            private void AddCall(TypeNode source, TypeNode target, string caller, string callee)
            {
                var edge = GetOrAddEdge(source, target, EdgeKind.Calls);
                if (recordedCalls.Add((edge, caller, callee)))
                    edge.Calls.Add(new CallInfo(caller, callee));
            }

            // ---------- HELPERS ----------
            private SemanticModel GetModel(SyntaxTree tree)
            {
                if (!models.TryGetValue(tree, out var model))
                {
                    model = compilation.GetSemanticModel(tree);
                    models[tree] = model;
                }

                return model;
            }

            // The 0-based line as the editor (Monaco) counts lines: at \r\n, \r and \n only. Roslyn's line numbers
            // also count U+0085, U+2028 and U+2029, which would put everything after one of those too far down.
            private int GetLine(Location location)
            {
                var tree = location.SourceTree!;
                if (!lineStarts.TryGetValue(tree, out var starts))
                {
                    var text = tree.GetText();
                    starts = new List<int> { 0 };
                    for (int i = 0; i < text.Length; i++)
                    {
                        if (text[i] == '\n' || (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')))
                            starts.Add(i + 1);
                    }
                    lineStarts[tree] = starts;
                }

                int line = starts.BinarySearch(location.SourceSpan.Start);
                return line >= 0 ? line : ~line - 1;
            }

            // Names are compared with symbols' names, so identifiers are read by their value: @event is event.
            // A static constructor isn't called by any code, so it gets a name of its own.
            private static string GetMemberName(MemberDeclarationSyntax member, TypeNode node) => member switch
            {
                MethodDeclarationSyntax method => method.Identifier.ValueText + "()",
                ConstructorDeclarationSyntax constructor when constructor.Modifiers.Any(SyntaxKind.StaticKeyword) => $"static {node.Name}()",
                ConstructorDeclarationSyntax => node.ConstructorName,
                DestructorDeclarationSyntax => "Finalize()",
                PropertyDeclarationSyntax property => property.Identifier.ValueText,
                IndexerDeclarationSyntax => "this[]",
                EventDeclarationSyntax @event => @event.Identifier.ValueText,
                BaseFieldDeclarationSyntax field => string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.ValueText)),
                OperatorDeclarationSyntax op => "operator " + op.OperatorToken.Text,
                ConversionOperatorDeclarationSyntax conversion => "operator " + conversion.Type,
                _ => "?"
            };

            // Where a member's declaration itself is, not the attributes above it: a method's parameter list (operators
            // have no name), or else its name.
            private static Location GetNameLocation(MemberDeclarationSyntax member) => member switch
            {
                BaseMethodDeclarationSyntax method => method.ParameterList.GetLocation(),
                PropertyDeclarationSyntax property => property.Identifier.GetLocation(),
                IndexerDeclarationSyntax indexer => indexer.ThisKeyword.GetLocation(),
                EventDeclarationSyntax @event => @event.Identifier.GetLocation(),
                BaseFieldDeclarationSyntax field => field.Declaration.GetLocation(),
                _ => member.GetLocation()
            };

            private static string? GetInvokedName(ExpressionSyntax expression) => expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                GenericNameSyntax generic => generic.Identifier.ValueText,
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
                MemberBindingExpressionSyntax memberBinding => memberBinding.Name.Identifier.ValueText,
                _ => null
            };

            private static GenericNameSyntax? GetGenericName(ExpressionSyntax expression) => expression switch
            {
                GenericNameSyntax generic => generic,
                MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } => generic,
                MemberBindingExpressionSyntax { Name: GenericNameSyntax generic } => generic,
                _ => null
            };

            private static string? GetRightmostName(ExpressionSyntax expression) => expression switch
            {
                SimpleNameSyntax name => name.Identifier.ValueText,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name.Identifier.ValueText,
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
                _ => null
            };
        }
    }
}
