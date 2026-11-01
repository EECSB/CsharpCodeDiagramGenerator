using System.Text.RegularExpressions;

namespace CSharpCodeGraph.Diagram
{
    /// <summary>
    /// Finds the structure of JavaScript files without a full parser: a tokenizer that knows strings,
    /// template literals, comments and regular expressions, and a scan of the tokens for the usual shapes
    /// of code. A file becomes a module node with its top-level functions and variables (and the
    /// functions of objects like window.X = { ... }), unless it holds nothing but classes; each class
    /// becomes a node of its own.
    /// Calls between files are found through imports and globals (window.X), and calls between the two
    /// languages through Blazor's JS interop: CodeAnalyzer matches IJSRuntime.InvokeAsync("X.f") in C# with
    /// <see cref="JavaScriptResult.TryResolveInteropCall"/>, and dotNetRef.invokeMethodAsync("Name") in
    /// JavaScript ends up in <see cref="JavaScriptResult.DotNetCalls"/>.
    /// </summary>
    public static class JavaScriptAnalyzer
    {
        private const string TopLevelCaller = "(top level)";

        public static bool IsJavaScriptFile(string path) =>
            path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase);

        public static JavaScriptResult Analyze(IReadOnlyList<SourceFile> files, DiagramSettings? settings = null)
        {
            settings ??= new DiagramSettings();
            var result = new JavaScriptResult();
            var scripts = files.Where(f => IsJavaScriptFile(f.Path)).Select(f => new Script(f, settings)).ToList();

            foreach (var script in scripts)
                script.ScanDefinitions();

            var project = new Project(scripts, settings, result);
            foreach (var script in scripts)
            {
                if (script.Node != null)
                    result.Types.Add(script.Node);
                result.Types.AddRange(script.Classes.Values.Select(c => c.Node));
            }

            foreach (var script in scripts)
                project.ScanReferences(script);

            return result;
        }

        #region Tokens

        private enum TokenKind
        {
            // What looking past the last token (T(i), At(k)) gives: default(Token), which must match nothing.
            // As the first member it's the default; with Identifier first, a file ending in "new" or "let" threw.
            None,
            Identifier,
            Punctuator,
            String,
            Template,
            Number,
            Regex
        }

        // Text is a string's value (without quotes); Line is 0-based.
        private readonly record struct Token(TokenKind Kind, string Text, int Line, int Start, int End)
        {
            public bool Is(string text) => (Kind == TokenKind.Identifier || Kind == TokenKind.Punctuator) && Text == text;
        }

        private static readonly string[] Punctuators =
        {
            ">>>=", "...", "===", "!==", "**=", "<<=", ">>=", ">>>", "&&=", "||=", "??=",
            "=>", "==", "!=", "<=", ">=", "&&", "||", "??", "?.", "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "**", "<<", ">>"
        };

        // After these, a '/' starts a regular expression rather than a division.
        private static readonly HashSet<string> KeywordsBeforeExpression = new(StringComparer.Ordinal)
        {
            "return", "typeof", "instanceof", "in", "of", "new", "delete", "void", "throw", "case", "do", "else", "yield", "await"
        };

        private static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            int line = 0;
            ReadTokens(text, 0, ref line, tokens, inTemplate: false);
            return tokens;
        }

        // Reads the tokens from i on, or with inTemplate (the code in a template's ${ ... }) up to the } that ends it.
        // Returns the index after what it read.
        private static int ReadTokens(string text, int i, ref int line, List<Token> tokens, bool inTemplate)
        {
            int n = text.Length, depth = 0;

            while (i < n)
            {
                char c = text[i];
                if (c == '\n')
                {
                    line++;
                    i++;
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                int start = i, startLine = line;
                char next = i + 1 < n ? text[i + 1] : '\0';

                if ((c == '/' && next == '/') || (c == '#' && next == '!'))
                {
                    while (i < n && text[i] != '\n')
                        i++;
                }
                else if (c == '/' && next == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 2;
                    line += CountLines(text, start, i);
                }
                else if (IsIdentifierStart(c))
                {
                    i++;
                    while (i < n && IsIdentifierPart(text[i]))
                        i++;
                    tokens.Add(new Token(TokenKind.Identifier, text[start..i], startLine, start, i));
                }
                else if (char.IsDigit(c) || (c == '.' && char.IsDigit(next)))
                {
                    bool isHex = c == '0' && (next == 'x' || next == 'X');
                    i++;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.'
                        || (!isHex && text[i] is '+' or '-' && text[i - 1] is 'e' or 'E')))
                    {
                        i++;
                    }
                    tokens.Add(new Token(TokenKind.Number, text[start..i], startLine, start, i));
                }
                else if (c is '"' or '\'')
                {
                    i = SkipString(text, i, ref line);
                    int valueEnd = i > start + 1 && text[i - 1] == c ? i - 1 : i;
                    tokens.Add(new Token(TokenKind.String, text[(start + 1)..valueEnd], startLine, start, i));
                }
                else if (c == '`')
                {
                    i = SkipTemplate(text, i, ref line);
                    tokens.Add(new Token(TokenKind.Template, string.Empty, startLine, start, i));
                }
                else if (c == '/' && IsRegexAllowed(tokens) && ScanRegex(text, i) is int regexEnd and > 0)
                {
                    i = regexEnd;
                    tokens.Add(new Token(TokenKind.Regex, text[start..i], startLine, start, i));
                }
                else
                {
                    var punctuator = Punctuators.FirstOrDefault(p => string.CompareOrdinal(text, i, p, 0, p.Length) == 0) ?? c.ToString();
                    i += punctuator.Length;
                    if (inTemplate && punctuator == "{")
                        depth++;
                    else if (inTemplate && punctuator == "}" && depth-- == 0)
                        return i;
                    tokens.Add(new Token(TokenKind.Punctuator, punctuator, startLine, start, i));
                }
            }

            return i;
        }

        private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c is '_' or '$' or '#' || c > 127;

        private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' || c > 127;

        private static int CountLines(string text, int start, int end)
        {
            int count = 0;
            for (int i = start; i < end; i++)
            {
                if (text[i] == '\n')
                    count++;
            }
            return count;
        }

        // Returns the index after the closing quote (or at the end of the line, for an unclosed string).
        private static int SkipString(string text, int i, ref int line)
        {
            char quote = text[i++];
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\\')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        line++;
                    i += 2;
                    continue;
                }
                if (c == quote)
                    return i + 1;
                if (c == '\n')
                    return i;
                i++;
            }
            return text.Length;
        }

        private static int SkipTemplate(string text, int i, ref int line)
        {
            i++;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\\')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        line++;
                    i += 2;
                    continue;
                }
                if (c == '`')
                    return i + 1;
                if (c == '$' && i + 1 < text.Length && text[i + 1] == '{')
                {
                    // The code inside ${ ... }, read as tokens: its strings, templates, comments and regular
                    // expressions can hold braces of their own.
                    i = ReadTokens(text, i + 2, ref line, new List<Token>(), inTemplate: true);
                    continue;
                }
                if (c == '\n')
                    line++;
                i++;
            }
            return text.Length;
        }

        private static bool IsRegexAllowed(List<Token> tokens)
        {
            if (tokens.Count == 0)
                return true;

            var previous = tokens[^1];
            return previous.Kind switch
            {
                TokenKind.Punctuator => previous.Text is not (")" or "]" or "}" or "++" or "--"),
                TokenKind.Identifier => KeywordsBeforeExpression.Contains(previous.Text),
                _ => false
            };
        }

        // Returns the index after the regular expression's flags, or -1 when the line ends first.
        private static int ScanRegex(string text, int i)
        {
            bool inClass = false;
            for (int j = i + 1; j < text.Length; j++)
            {
                char c = text[j];
                if (c == '\n')
                    return -1;
                if (c == '\\')
                    j++;
                else if (c == '[')
                    inClass = true;
                else if (c == ']')
                    inClass = false;
                else if (c == '/' && !inClass)
                {
                    j++;
                    while (j < text.Length && IsIdentifierPart(text[j]))
                        j++;
                    return j;
                }
            }
            return -1;
        }

        // For each bracket, the index of its partner; -1 for everything else and for unclosed brackets.
        private static int[] MatchBrackets(List<Token> tokens)
        {
            var match = new int[tokens.Count];
            Array.Fill(match, -1);
            var open = new Stack<int>();

            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind != TokenKind.Punctuator)
                    continue;

                switch (tokens[i].Text)
                {
                    case "(" or "[" or "{":
                        open.Push(i);
                        break;
                    case ")" or "]" or "}":
                        var opener = tokens[i].Text switch { ")" => "(", "]" => "[", _ => "{" };
                        // Code that doesn't parse: close up to the matching opener, if there is one.
                        if (!open.Any(o => tokens[o].Text == opener))
                            break;
                        int o;
                        while (tokens[o = open.Pop()].Text != opener)
                        {
                        }
                        match[o] = i;
                        match[i] = o;
                        break;
                }
            }

            return match;
        }

        #endregion


        #region Definitions

        private sealed class ClassInfo
        {
            public ClassInfo(TypeNode node) => Node = node;

            public TypeNode Node { get; }
            public string? BaseName { get; set; }
        }

        // Something calls can go to: a function of a module, a function of an object (X.f), or a class.
        private sealed record Target(Script Script, ClassInfo? Class, string Callee)
        {
            public TypeNode Node => Class?.Node ?? Script.FileNode;
        }

        // A function's code, so calls in it are attributed to it.
        private sealed record Body(int Start, int End, ClassInfo? Class, string Caller);

        private sealed record Import(string Specifier, string Name);

        private sealed class Script
        {
            private readonly DiagramSettings settings;
            private readonly List<MemberInfo> members = new();

            public Script(SourceFile file, DiagramSettings settings)
            {
                File = file;
                this.settings = settings;
                Tokens = Tokenize(file.Content);
                Match = MatchBrackets(Tokens);

                int slash = file.Path.LastIndexOf('/');
                Directory = slash < 0 ? string.Empty : file.Path[..slash];
            }

            public SourceFile File { get; }
            public string Directory { get; }
            public List<Token> Tokens { get; }
            public int[] Match { get; }

            // Uses import or export, so its top-level names aren't globals.
            public bool IsModule { get; private set; }
            public TypeNode? Node { get; private set; }

            // A file with only classes has no node of its own; its top-level code counts as its first class's.
            public TypeNode FileNode => Node ?? Classes.Values.First().Node;

            public Dictionary<string, Target> Functions { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, Dictionary<string, Target>> Objects { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, ClassInfo> Classes { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, Import> Imports { get; } = new(StringComparer.Ordinal);
            // Exported names that aren't the local ones: draw for export { render as draw }, default for export default function render.
            public Dictionary<string, string> Exports { get; } = new(StringComparer.Ordinal);
            public List<string> ImportedFiles { get; } = new();
            public List<Body> Bodies { get; } = new();
            // Assigned to window (or, in a script without imports and exports, declared at the top level).
            public HashSet<string> GlobalNames { get; } = new(StringComparer.Ordinal);

            // The file's own name for what it exports as exported.
            public string LocalName(string exported) => Exports.GetValueOrDefault(exported, exported);

            private Token T(int i) => i < Tokens.Count ? Tokens[i] : default;

            private int End(int i) => Match[i] >= 0 ? Match[i] : Tokens.Count - 1;

            private int[]? depths;

            private int Depth(int i)
            {
                if (depths == null)
                {
                    depths = new int[Tokens.Count];
                    int depth = 0;
                    for (int j = 0; j < Tokens.Count; j++)
                    {
                        if (Tokens[j].Kind == TokenKind.Punctuator && Tokens[j].Text is ")" or "]" or "}" && Match[j] >= 0)
                            depth--;
                        depths[j] = depth;
                        if (Tokens[j].Kind == TokenKind.Punctuator && Tokens[j].Text is "(" or "[" or "{" && Match[j] >= 0)
                            depth++;
                    }
                }
                return depths[i];
            }

            public void ScanDefinitions()
            {
                // A file with a self of its own (var self = this) sets that one in its functions, not the global object.
                bool selfIsLocal = Enumerable.Range(0, Tokens.Count).Any(k => T(k).Is("self") && T(k + 1).Is("=") && (k == 0 || !T(k - 1).Is(".")));

                int i = 0;
                while (i < Tokens.Count)
                {
                    int next = ScanStatement(i);
                    // A definition is read as a whole, but the code in it (a function's body, say) can set globals too.
                    for (int j = i + 1; j < next; j++)
                    {
                        if (!(selfIsLocal && T(j).Is("self")))
                            ScanGlobalAssignment(j);
                    }
                    i = next > i ? next : i + 1;
                }

                if (!IsModule)
                {
                    GlobalNames.UnionWith(Functions.Keys);
                    GlobalNames.UnionWith(Objects.Keys);
                    GlobalNames.UnionWith(Classes.Keys);
                }

                if (members.Count > 0 || Classes.Count == 0)
                {
                    Node = new TypeNode
                    {
                        Id = "js:" + File.Path,
                        Name = File.Path[(File.Path.LastIndexOf('/') + 1)..],
                        Namespace = Directory,
                        Kind = NodeKind.Module,
                        FilePath = File.Path
                    };
                    Node.Members.AddRange(members);
                }
            }

            // Looks for a definition at token i; returns the index after it, or i when there's none.
            private int ScanStatement(int i)
            {
                var token = T(i);
                bool topLevel = Depth(i) == 0;

                if (topLevel && token.Is("export"))
                {
                    IsModule = true;
                    // export { a, b } [from "./x"] and export * from "./x"
                    if (T(i + 1).Is("{") || T(i + 1).Is("*"))
                    {
                        int j = T(i + 1).Is("{") ? End(i + 1) + 1 : i + 2;
                        while (j < Tokens.Count && !T(j).Is("from") && !T(j).Is(";") && T(j).Line == T(i).Line)
                            j++;
                        if (T(j).Is("from") && T(j + 1).Kind == TokenKind.String)
                        {
                            ImportedFiles.Add(T(j + 1).Text);
                            return j + 2;
                        }
                        // export { render as draw } and export { render as default }
                        if (T(i + 1).Is("{"))
                        {
                            for (int k = i + 2; k < End(i + 1); k++)
                            {
                                if (T(k).Kind == TokenKind.Identifier && T(k + 1).Is("as") && T(k + 2).Kind is TokenKind.Identifier or TokenKind.String)
                                    Exports[T(k + 2).Text] = T(k).Text;
                            }
                        }
                        return j;
                    }
                    if (T(i + 1).Is("default") && DefaultExportName(i + 2) is { } name)
                        Exports["default"] = name;
                    return T(i + 1).Is("default") ? i + 2 : i + 1;
                }

                if (topLevel && token.Is("import") && !T(i + 1).Is("(") && !T(i + 1).Is("."))
                    return ScanImport(i);

                if (topLevel && (token.Is("function") || (token.Is("async") && T(i + 1).Is("function"))))
                {
                    int name = token.Is("async") ? i + 2 : i + 1;
                    if (T(name).Is("*"))
                        name++;
                    if (T(name).Kind == TokenKind.Identifier && T(name + 1).Is("("))
                        return AddFunction(T(name).Text, name, name + 1, null);
                }

                if (topLevel && token.Is("class") && T(i + 1).Kind == TokenKind.Identifier && !T(i + 1).Is("extends"))
                    return ScanClass(T(i + 1).Text, i + 1, i);

                if (topLevel && token.Kind == TokenKind.Identifier && token.Text is "const" or "let" or "var")
                    return ScanDeclarations(i + 1, global: false);

                return ScanGlobalAssignment(i);
            }

            // window.X = ..., at any depth: scripts often set their globals inside a function.
            private int ScanGlobalAssignment(int i)
            {
                if (T(i).Text is "window" or "globalThis" or "self" && T(i + 1).Is(".") && T(i + 2).Kind == TokenKind.Identifier && T(i + 3).Is("=")
                    && (i == 0 || !T(i - 1).Is(".")))
                {
                    GlobalNames.Add(T(i + 2).Text);
                    return ScanValue(T(i + 2).Text, i + 2, i + 4);
                }

                return i;
            }

            // The name export default exports at v: function render() { }, class Shape { } or render; null when it has none.
            private string? DefaultExportName(int v)
            {
                int j = T(v).Is("async") ? v + 1 : v;
                if (T(j).Is("function") || T(j).Is("class"))
                {
                    j += T(j + 1).Is("*") ? 2 : 1;
                    return T(j).Kind == TokenKind.Identifier && !T(j).Is("extends") ? T(j).Text : null;
                }
                return T(v).Kind == TokenKind.Identifier && ExpressionEnd(v) == v ? T(v).Text : null;
            }

            private int ScanImport(int i)
            {
                IsModule = true;
                int j = i + 1;
                if (T(j).Kind == TokenKind.String)
                {
                    ImportedFiles.Add(T(j).Text);
                    return j + 1;
                }

                var names = new List<(string Local, string Imported)>();
                while (j < Tokens.Count && !T(j).Is("from") && !T(j).Is(";"))
                {
                    if (T(j).Is("*") && T(j + 1).Is("as"))
                    {
                        names.Add((T(j + 2).Text, "*"));
                        j += 3;
                    }
                    else if (T(j).Is("{"))
                    {
                        int close = End(j);
                        for (int k = j + 1; k < close; k++)
                        {
                            if (T(k).Kind != TokenKind.Identifier && T(k).Kind != TokenKind.String)
                                continue;
                            if (T(k + 1).Is("as"))
                            {
                                names.Add((T(k + 2).Text, T(k).Text));
                                k += 2;
                            }
                            else
                            {
                                names.Add((T(k).Text, T(k).Text));
                            }
                        }
                        j = close + 1;
                    }
                    else if (T(j).Kind == TokenKind.Identifier)
                    {
                        names.Add((T(j).Text, "default"));
                        j++;
                    }
                    else
                    {
                        j++;
                    }
                }

                if (T(j).Is("from") && T(j + 1).Kind == TokenKind.String)
                {
                    var specifier = T(j + 1).Text;
                    ImportedFiles.Add(specifier);
                    foreach (var (local, imported) in names)
                        Imports[local] = new Import(specifier, imported);
                    return j + 2;
                }
                return j;
            }

            // const a = ..., b = ...; (destructuring patterns are skipped)
            private int ScanDeclarations(int i, bool global)
            {
                while (true)
                {
                    var name = T(i);
                    if (name.Is("{") || name.Is("["))
                        return ExpressionEnd(End(i) + 1) + 1;
                    if (name.Kind != TokenKind.Identifier)
                        return i;

                    int end;
                    if (T(i + 1).Is("="))
                    {
                        end = ScanValue(name.Text, i, i + 2);
                        // Values that aren't functions, classes or objects are only skipped by ScanValue to their start.
                        end = Math.Max(end, ExpressionEnd(i + 2) + 1);
                    }
                    else
                    {
                        AddVariable(name.Text, i);
                        end = i + 1;
                    }

                    if (global)
                        GlobalNames.Add(name.Text);
                    if (!T(end).Is(","))
                        return end;
                    i = end + 1;
                }
            }

            // name = value, where value starts at token v; returns the index after what it consumed.
            private int ScanValue(string name, int nameIndex, int v)
            {
                if (IsFunctionValue(v, out int parameters, out int bodyStart, out int bodyEnd))
                {
                    AddFunction(name, nameIndex, parameters, null, bodyStart, bodyEnd);
                    return bodyEnd + 1;
                }

                if (T(v).Is("class"))
                {
                    int afterName = T(v + 1).Kind == TokenKind.Identifier && !T(v + 1).Is("extends") ? v + 2 : v + 1;
                    return ScanClass(name, nameIndex, afterName - 1);
                }

                if (T(v).Is("{") && ScanObject(name, v))
                    return End(v) + 1;

                AddVariable(name, nameIndex);
                return v;
            }

            // function [name](...) { }, async (...) => ..., x => ...
            private bool IsFunctionValue(int v, out int parameters, out int bodyStart, out int bodyEnd)
            {
                parameters = bodyStart = bodyEnd = -1;
                int j = T(v).Is("async") && !T(v + 1).Is("=>") ? v + 1 : v;

                if (T(j).Is("function"))
                {
                    j++;
                    if (T(j).Is("*"))
                        j++;
                    if (T(j).Kind == TokenKind.Identifier)
                        j++;
                    if (!T(j).Is("(") || !T(End(j) + 1).Is("{"))
                        return false;
                    parameters = j;
                    bodyStart = End(j) + 1;
                    bodyEnd = End(bodyStart);
                    return true;
                }

                int arrow;
                if (T(j).Is("(") && T(End(j) + 1).Is("=>"))
                {
                    parameters = j;
                    arrow = End(j) + 1;
                }
                else if (T(j).Kind == TokenKind.Identifier && T(j + 1).Is("=>"))
                {
                    parameters = j;
                    arrow = j + 1;
                }
                else
                {
                    return false;
                }

                bodyStart = arrow + 1;
                bodyEnd = T(bodyStart).Is("{") ? End(bodyStart) : ExpressionEnd(bodyStart);
                return true;
            }

            // The last token of the expression starting at i: before a ';' or ',' or closing bracket at its
            // level, or at a line break where the next line starts something new.
            private int ExpressionEnd(int i)
            {
                int j = i;
                while (j < Tokens.Count)
                {
                    var token = T(j);
                    if (token.Kind == TokenKind.Punctuator)
                    {
                        if (token.Text is ";" or ",")
                            return j - 1;
                        if (token.Text is ")" or "]" or "}")
                            return j - 1;
                        if (token.Text is "(" or "[" or "{")
                        {
                            j = End(j) + 1;
                            continue;
                        }
                    }
                    else if (j > i && token.Line > T(j - 1).Line && EndsExpression(T(j - 1)))
                    {
                        return j - 1;
                    }
                    j++;
                }
                return Tokens.Count - 1;
            }

            private static bool EndsExpression(Token token) => token.Kind switch
            {
                TokenKind.Punctuator => token.Text is ")" or "]" or "}" or "++" or "--",
                TokenKind.Identifier => !KeywordsBeforeExpression.Contains(token.Text),
                _ => true
            };

            // nameIndex is the name's token, for its line; parameters is the '(' (or the single parameter).
            private int AddFunction(string name, int nameIndex, int parameters, ClassInfo? owner, int bodyStart = -1, int bodyEnd = -1)
            {
                if (bodyStart < 0)
                {
                    bodyStart = End(parameters) + 1;
                    if (!T(bodyStart).Is("{"))
                        return End(parameters) + 1;
                    bodyEnd = End(bodyStart);
                }

                members.Add(new MemberInfo(MemberKind.Method, $"{name}({ParameterText(parameters)})") { FilePath = File.Path, Line = T(nameIndex).Line });
                Functions[name] = new Target(this, owner, name + "()");
                Bodies.Add(new Body(bodyStart, bodyEnd, owner, name + "()"));
                return bodyEnd + 1;
            }

            private void AddVariable(string name, int nameIndex)
            {
                if (settings.ShowVariables)
                    members.Add(new MemberInfo(MemberKind.Field, name) { FilePath = File.Path, Line = T(nameIndex).Line });
            }

            private string ParameterText(int parameters)
            {
                if (!T(parameters).Is("("))
                    return T(parameters).Text;

                int close = End(parameters);
                if (close <= parameters + 1)
                    return string.Empty;
                var text = File.Content[T(parameters).End..T(close).Start];
                return Regex.Replace(text, @"\s+", " ").Trim();
            }

            // class [Name] [extends Base] { ... }; keyword is the "class" token, or the name before it.
            private int ScanClass(string name, int nameIndex, int keyword)
            {
                int j = keyword + 1;
                if (T(j).Kind == TokenKind.Identifier && !T(j).Is("extends"))
                    j++;

                string? baseName = null;
                if (T(j).Is("extends"))
                {
                    int start = ++j;
                    while (j < Tokens.Count && !T(j).Is("{"))
                        j = T(j).Is("(") || T(j).Is("[") ? End(j) + 1 : j + 1;
                    var parts = Tokens.GetRange(start, Math.Max(j - start, 0));
                    if (parts.Count > 0 && parts.All(p => p.Kind == TokenKind.Identifier || p.Is(".")))
                        baseName = string.Concat(parts.Select(p => p.Text));
                }

                if (!T(j).Is("{"))
                    return j;

                var info = new ClassInfo(new TypeNode
                {
                    Id = $"js:{File.Path}#{name}",
                    Name = name,
                    Namespace = Directory,
                    Kind = NodeKind.Class,
                    FilePath = File.Path,
                    Line = T(nameIndex).Line
                })
                { BaseName = baseName };
                Classes[name] = info;
                ScanClassBody(info, j, End(j));
                return End(j) + 1;
            }

            private void ScanClassBody(ClassInfo owner, int open, int close)
            {
                int j = open + 1;
                while (j < close)
                {
                    if (T(j).Is(";"))
                    {
                        j++;
                        continue;
                    }

                    bool isGetter = false, isSetter = false;
                    while (T(j).Text is "static" or "async" or "get" or "set" or "*" && !T(j + 1).Is("(") && !T(j + 1).Is("=") && !T(j + 1).Is(";"))
                    {
                        isGetter |= T(j).Is("get");
                        isSetter |= T(j).Is("set");
                        if (T(j).Is("static") && T(j + 1).Is("{"))
                            break;
                        j++;
                    }

                    if (T(j).Is("static") && T(j + 1).Is("{"))
                    {
                        j = End(j + 1) + 1;
                        continue;
                    }

                    string? name = null;
                    int nameIndex = j;
                    if (T(j).Kind is TokenKind.Identifier or TokenKind.String)
                    {
                        name = T(j).Text;
                        j++;
                    }
                    else if (T(j).Is("["))
                    {
                        j = End(j) + 1;
                    }
                    else
                    {
                        j++;
                        continue;
                    }

                    // Private members (#name) aren't listed, like C#'s non-public ones; their code still counts.
                    bool listed = name != null && !name.StartsWith('#') && name != "constructor";
                    var caller = name == "constructor" ? owner.Node.ConstructorName : (name ?? "[computed]") + "()";

                    if (T(j).Is("("))
                    {
                        int bodyStart = End(j) + 1;
                        int bodyEnd = T(bodyStart).Is("{") ? End(bodyStart) : bodyStart - 1;
                        if (listed && isGetter)
                        {
                            if (settings.ShowVariables)
                                owner.Node.Members.Add(new MemberInfo(MemberKind.Property, name!) { FilePath = File.Path, Line = T(nameIndex).Line });
                        }
                        else if (listed && !isSetter)
                        {
                            owner.Node.Members.Add(new MemberInfo(MemberKind.Method, $"{name}({ParameterText(j)})") { FilePath = File.Path, Line = T(nameIndex).Line });
                        }
                        Bodies.Add(new Body(bodyStart, bodyEnd, owner, caller));
                        j = bodyEnd + 1;
                        continue;
                    }

                    // A field, maybe with an arrow function as its value: handle = () => { ... }
                    int end = T(j).Is("=") ? ExpressionEnd(j + 1) : j - 1;
                    if (T(j).Is("=") && IsFunctionValue(j + 1, out int parameters, out int fieldBodyStart, out int fieldBodyEnd))
                    {
                        if (listed)
                            owner.Node.Members.Add(new MemberInfo(MemberKind.Method, $"{name}({ParameterText(parameters)})") { FilePath = File.Path, Line = T(nameIndex).Line });
                        Bodies.Add(new Body(fieldBodyStart, fieldBodyEnd, owner, caller));
                        end = Math.Max(end, fieldBodyEnd);
                    }
                    else if (listed && settings.ShowVariables)
                    {
                        owner.Node.Members.Add(new MemberInfo(MemberKind.Field, name!) { FilePath = File.Path, Line = T(nameIndex).Line });
                    }
                    j = Math.Max(end + 1, nameIndex + 1);
                }
            }

            // { f() { }, g: () => ..., value: 1 }: an object with functions is listed by its functions (X.f),
            // like a static class. Returns false for plain data, which is a variable.
            private bool ScanObject(string name, int open)
            {
                int close = End(open);
                var functions = new List<(string Name, int NameIndex, int Parameters, int BodyStart, int BodyEnd)>();

                int j = open + 1;
                while (j < close)
                {
                    int entryEnd = j;
                    while (entryEnd < close && !T(entryEnd).Is(","))
                        entryEnd = T(entryEnd).Kind == TokenKind.Punctuator && T(entryEnd).Text is "(" or "[" or "{" ? End(entryEnd) + 1 : entryEnd + 1;

                    int k = j;
                    while (T(k).Text is "async" or "get" or "set" or "*" && !(T(k + 1).Is("(") || T(k + 1).Is(":") || T(k + 1).Is(",") || T(k + 1).Is("}")))
                        k++;

                    if (k < entryEnd && T(k).Kind is TokenKind.Identifier or TokenKind.String)
                    {
                        if (T(k + 1).Is("(") && T(End(k + 1) + 1).Is("{"))
                            functions.Add((T(k).Text, k, k + 1, End(k + 1) + 1, End(End(k + 1) + 1)));
                        else if (T(k + 1).Is(":") && IsFunctionValue(k + 2, out int parameters, out int bodyStart, out int bodyEnd))
                            functions.Add((T(k).Text, k, parameters, bodyStart, Math.Min(bodyEnd, entryEnd - 1)));
                    }

                    j = entryEnd + 1;
                }

                if (functions.Count == 0)
                    return false;

                var methods = new Dictionary<string, Target>(StringComparer.Ordinal);
                foreach (var function in functions)
                {
                    var qualified = $"{name}.{function.Name}";
                    members.Add(new MemberInfo(MemberKind.Method, $"{qualified}({ParameterText(function.Parameters)})") { FilePath = File.Path, Line = T(function.NameIndex).Line });
                    methods[function.Name] = new Target(this, null, qualified + "()");
                    Bodies.Add(new Body(function.BodyStart, function.BodyEnd, null, qualified + "()"));
                }
                Objects[name] = methods;
                return true;
            }
        }

        #endregion


        #region References

        private sealed class Project
        {
            // Built-in constructors, never worth a node of their own (like CodeAnalyzer's PredefinedTypeNames).
            private static readonly HashSet<string> BuiltInTypes = new(StringComparer.Ordinal)
            {
                "Object", "Array", "String", "Number", "Boolean", "Symbol", "Function", "BigInt"
            };

            // Calls to these aren't calls to the user's functions, whatever is defined with the same name.
            private static readonly HashSet<string> NotCalls = new(StringComparer.Ordinal)
            {
                "if", "for", "while", "switch", "catch", "function", "return", "typeof", "super", "with", "await", "yield", "void", "delete", "in", "of"
            };

            private readonly List<Script> scripts;
            private readonly DiagramSettings settings;
            private readonly JavaScriptResult result;
            private readonly Dictionary<string, Script> byPath = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, Target> globalFunctions = new(StringComparer.Ordinal);
            private readonly Dictionary<string, Dictionary<string, Target>> globalObjects = new(StringComparer.Ordinal);
            private readonly Dictionary<string, ClassInfo> globalClasses = new(StringComparer.Ordinal);

            public Project(List<Script> scripts, DiagramSettings settings, JavaScriptResult result)
            {
                this.scripts = scripts;
                this.settings = settings;
                this.result = result;

                foreach (var script in scripts)
                {
                    byPath[script.File.Path] = script;
                    foreach (var name in script.GlobalNames)
                    {
                        if (script.Functions.TryGetValue(name, out var function))
                        {
                            globalFunctions.TryAdd(name, function);
                            result.Globals.TryAdd(name, (function.Node, function.Callee));
                        }
                        if (script.Objects.TryGetValue(name, out var methods))
                        {
                            globalObjects.TryAdd(name, methods);
                            foreach (var (method, target) in methods)
                                result.Globals.TryAdd($"{name}.{method}", (target.Node, target.Callee));
                        }
                        if (script.Classes.TryGetValue(name, out var info))
                            globalClasses.TryAdd(name, info);
                    }
                }
            }

            public void ScanReferences(Script script)
            {
                var tokens = script.Tokens;
                var fileNode = script.FileNode;

                foreach (var specifier in script.ImportedFiles.Distinct())
                    AddModuleReference(fileNode, script, specifier);

                // Where each function starts (its body's first line), private ones too, for the "Functions" option.
                foreach (var body in script.Bodies.Where(b => b.Start >= 0 && b.Start < tokens.Count))
                    (body.Class?.Node ?? fileNode).FunctionLocations.TryAdd(body.Caller, new SourceLocation(script.File.Path, tokens[body.Start].Line));

                foreach (var info in script.Classes.Values)
                {
                    if (info.BaseName != null && ResolveClass(script, info.BaseName) is { } baseNode && baseNode != info.Node)
                        result.Links.Add(new JavaScriptLink(info.Node, baseNode, null, EdgeKind.Inherits));
                    else if (info.BaseName != null && !settings.OnlyShowMyCode)
                        result.Links.Add(new JavaScriptLink(info.Node, null, info.BaseName, EdgeKind.Inherits));
                }

                // Which function each token is in: the innermost body.
                var bodyOf = new int[tokens.Count];
                Array.Fill(bodyOf, -1);
                var bodies = script.Bodies.Select((b, i) => (Body: b, Index: i)).OrderByDescending(b => b.Body.End - b.Body.Start);
                foreach (var (body, index) in bodies)
                {
                    for (int i = Math.Max(body.Start, 0); i <= body.End && i < tokens.Count; i++)
                        bodyOf[i] = index;
                }

                for (int i = 0; i < tokens.Count; i++)
                {
                    var token = tokens[i];
                    if (token.Kind != TokenKind.Identifier)
                        continue;

                    var body = bodyOf[i] >= 0 ? script.Bodies[bodyOf[i]] : null;
                    var source = body?.Class?.Node ?? fileNode;
                    var caller = body?.Caller ?? TopLevelCaller;
                    var previous = i > 0 ? tokens[i - 1] : default;
                    bool isMemberName = previous.Is(".") || previous.Is("?.");
                    Token At(int k) => k < tokens.Count ? tokens[k] : default;

                    // dotNetRef.invokeMethodAsync("Method", ...) and DotNet.invokeMethodAsync("Assembly", "Method", ...)
                    if (isMemberName && token.Text is "invokeMethodAsync" or "invokeMethod" && At(i + 1).Is("(") && At(i + 2).Kind == TokenKind.String)
                    {
                        bool isStatic = i >= 2 && tokens[i - 2].Is("DotNet") && At(i + 3).Is(",") && At(i + 4).Kind == TokenKind.String;
                        if (settings.ShowMethodCalls)
                            result.DotNetCalls.Add(new JavaScriptDotNetCall(source, caller, isStatic ? At(i + 4).Text : At(i + 2).Text));
                        continue;
                    }

                    if (isMemberName)
                        continue;

                    // import("./x") and require("./x")
                    if (token.Text is "import" or "require" && At(i + 1).Is("(") && At(i + 2).Kind == TokenKind.String && At(i + 3).Is(")"))
                    {
                        AddModuleReference(fileNode, script, At(i + 2).Text);
                        continue;
                    }

                    if (!settings.ShowMethodCalls || NotCalls.Contains(token.Text))
                        continue;

                    // new X(), new ns.X()
                    if (token.Is("new") && At(i + 1).Kind == TokenKind.Identifier)
                    {
                        int k = i + 1;
                        var name = tokens[k].Text;
                        while (At(k + 1).Is(".") && At(k + 2).Kind == TokenKind.Identifier)
                        {
                            k += 2;
                            name += "." + tokens[k].Text;
                        }

                        var target = ResolveClass(script, name);
                        var shortName = name[(name.LastIndexOf('.') + 1)..];
                        if (target != null)
                            result.Links.Add(new JavaScriptLink(source, target, null, EdgeKind.Calls, caller, target.ConstructorName));
                        else if (!settings.OnlyShowMyCode && char.IsUpper(shortName[0]) && !BuiltInTypes.Contains(shortName))
                            result.Links.Add(new JavaScriptLink(source, null, shortName, EdgeKind.Calls, caller, $"new {shortName}()"));
                        continue;
                    }

                    if (previous.Is("function") || previous.Is("new"))
                        continue;

                    // f(...), but not a method definition f(...) { ... }
                    if (At(i + 1).Is("("))
                    {
                        int close = script.Match[i + 1];
                        if (close >= 0 && At(close + 1).Is("{"))
                            continue;
                        AddCall(source, caller, ResolveFunction(script, token.Text));
                        continue;
                    }

                    // X.f(...), window.f(...)
                    if (At(i + 1).Is(".") && At(i + 2).Kind == TokenKind.Identifier && At(i + 3).Is("("))
                        AddCall(source, caller, ResolveMethod(script, token.Text, tokens[i + 2].Text));
                    // window.X.f(...)
                    else if (token.Text is "window" or "globalThis" or "self" && At(i + 1).Is(".") && At(i + 2).Kind == TokenKind.Identifier
                        && At(i + 3).Is(".") && At(i + 4).Kind == TokenKind.Identifier && At(i + 5).Is("("))
                        AddCall(source, caller, globalObjects.GetValueOrDefault(tokens[i + 2].Text)?.GetValueOrDefault(tokens[i + 4].Text));
                }
            }

            private void AddCall(TypeNode source, string caller, Target? target)
            {
                if (target != null)
                    result.Links.Add(new JavaScriptLink(source, target.Node, null, EdgeKind.Calls, caller, target.Callee));
            }

            private void AddModuleReference(TypeNode source, Script script, string specifier)
            {
                if (Resolve(script, specifier) is { } target)
                {
                    if (target.FileNode != source)
                        result.Links.Add(new JavaScriptLink(source, target.FileNode, null, EdgeKind.Uses));
                }
                else if (!settings.OnlyShowMyCode && PackageName(specifier) is { } package)
                {
                    result.Links.Add(new JavaScriptLink(source, null, package, EdgeKind.Uses));
                }
            }

            private Target? ResolveFunction(Script script, string name)
            {
                if (script.Functions.TryGetValue(name, out var local))
                    return local;

                if (script.Imports.TryGetValue(name, out var import) && Resolve(script, import.Specifier) is { } imported)
                {
                    if (imported.Functions.TryGetValue(imported.LocalName(import.Name), out var function))
                        return function;
                    if (import.Name == "default")
                        return new Target(imported, null, "default()");
                    return null;
                }

                return globalFunctions.GetValueOrDefault(name);
            }

            private Target? ResolveMethod(Script script, string objectName, string method)
            {
                if (objectName is "window" or "globalThis" or "self")
                    return globalFunctions.GetValueOrDefault(method);

                if (script.Objects.TryGetValue(objectName, out var local))
                    return local.GetValueOrDefault(method);

                if (script.Imports.TryGetValue(objectName, out var import))
                {
                    if (Resolve(script, import.Specifier) is not { } imported)
                        return null;
                    if (import.Name == "*")
                        return imported.Functions.GetValueOrDefault(imported.LocalName(method));
                    return imported.Objects.GetValueOrDefault(imported.LocalName(import.Name))?.GetValueOrDefault(method);
                }

                if (script.Functions.ContainsKey(objectName) || script.Classes.ContainsKey(objectName))
                    return null;

                return globalObjects.GetValueOrDefault(objectName)?.GetValueOrDefault(method);
            }

            private TypeNode? ResolveClass(Script script, string name)
            {
                var parts = name.Split('.');
                if (parts.Length == 1 && script.Classes.TryGetValue(name, out var local))
                    return local.Node;

                if (script.Imports.TryGetValue(parts[0], out var import) && Resolve(script, import.Specifier) is { } imported)
                {
                    var className = imported.LocalName(parts.Length > 1 && import.Name == "*" ? parts[1] : import.Name);
                    if (imported.Classes.TryGetValue(className, out var info))
                        return info.Node;
                    // A default export with no name to go by (module.exports = Shape) that is the file's only class.
                    return className == "default" && imported.Classes.Count == 1 ? imported.Classes.Values.First().Node : null;
                }

                return parts.Length == 1 ? globalClasses.GetValueOrDefault(name)?.Node : null;
            }

            // A relative import ("./x", "../lib/y.js") to one of the files, or null.
            private Script? Resolve(Script from, string specifier)
            {
                if (!specifier.StartsWith("./", StringComparison.Ordinal) && !specifier.StartsWith("../", StringComparison.Ordinal) && !specifier.StartsWith('/'))
                    return null;

                var segments = new List<string>(specifier.StartsWith('/') ? Array.Empty<string>() : from.Directory.Split('/', StringSplitOptions.RemoveEmptyEntries));
                foreach (var segment in specifier.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (segment == "..")
                    {
                        if (segments.Count > 0)
                            segments.RemoveAt(segments.Count - 1);
                    }
                    else if (segment != ".")
                    {
                        segments.Add(segment);
                    }
                }

                var path = string.Join("/", segments);
                foreach (var candidate in new[] { path, path + ".js", path + ".mjs", path + ".cjs", path + "/index.js" })
                {
                    if (byPath.TryGetValue(candidate, out var script))
                        return script;
                }

                // An absolute path ("/js/x.js") is from the site's root, which can be any folder of the project.
                if (specifier.StartsWith('/'))
                    return scripts.FirstOrDefault(s => s.File.Path.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase));

                return null;
            }

            // "cytoscape" -> cytoscape, "monaco-editor/esm/vs/editor/editor.api" -> monaco-editor, "@viz-js/viz" -> @viz-js/viz
            private static string? PackageName(string specifier)
            {
                if (specifier.Length == 0 || specifier.StartsWith('.') || specifier.StartsWith('/') || specifier.Contains(':'))
                    return null;

                var parts = specifier.Split('/');
                return specifier.StartsWith('@') && parts.Length > 1 ? $"{parts[0]}/{parts[1]}" : parts[0];
            }
        }

        #endregion
    }

    public sealed class JavaScriptResult
    {
        public List<TypeNode> Types { get; } = new();
        public List<JavaScriptLink> Links { get; } = new();
        public List<JavaScriptDotNetCall> DotNetCalls { get; } = new();

        // What C# can call through IJSRuntime: "f" for window.f, "X.f" for window.X = { f() { } }.
        internal Dictionary<string, (TypeNode Node, string Callee)> Globals { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Finds the function an IJSRuntime call names, like "CodeGraphView.render" or "window.InitializeMonaco".
        /// </summary>
        public bool TryResolveInteropCall(string identifier, out TypeNode? node, out string callee)
        {
            if (identifier.StartsWith("window.", StringComparison.Ordinal))
                identifier = identifier["window.".Length..];

            node = null;
            callee = string.Empty;
            if (!Globals.TryGetValue(identifier, out var target))
                return false;

            (node, callee) = target;
            return true;
        }
    }

    /// <summary>A relationship found in JavaScript: to a node, or (Target null) to a library by name.</summary>
    public sealed record JavaScriptLink(TypeNode Source, TypeNode? Target, string? ExternalName, EdgeKind Kind, string? Caller = null, string? Callee = null);

    /// <summary>A call from JavaScript to a [JSInvokable] C# method.</summary>
    public sealed record JavaScriptDotNetCall(TypeNode Source, string Caller, string MethodName);
}
