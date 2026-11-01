using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpCodeGraph.Diagram
{
    /// <summary>
    /// Turns a Razor component (.razor) into a C# class the analyzer can read, roughly the way the
    /// Razor compiler does: @code/@functions blocks become the class body, directives (@inherits,
    /// @implements, @inject, @typeparam, @using, @namespace) are applied, and the C# used in the markup
    /// goes into a BuildRenderTree method, with child components as __builder.OpenComponent&lt;T&gt;() calls.
    /// </summary>
    public static class RazorExtractor
    {
        private static readonly Regex RazorCommentRegex = new(@"@\*.*?\*@", RegexOptions.Singleline | RegexOptions.Compiled);

        public static bool IsRazorFile(string path) =>
            path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);

        public static bool IsImportsFile(string path) =>
            GetFileName(path).Equals("_Imports.razor", StringComparison.OrdinalIgnoreCase);

        public static string Extract(string razorContent, string filePath, RazorProjectContext? context = null)
        {
            if (IsImportsFile(filePath))
                return string.Empty;

            var parts = Scan(razorContent);
            var imports = context?.GetImports(filePath) ?? Array.Empty<RazorParts>();

            var className = SanitizeIdentifier(Path.GetFileNameWithoutExtension(GetFileName(filePath)));
            var ns = parts.Namespace ?? context?.GetNamespace(filePath);
            var usings = imports.SelectMany(i => i.Usings)
                .Concat(parts.Usings)
                .Distinct();

            var sb = new StringBuilder();

            foreach (var u in usings)
                sb.AppendLine($"using {u};");

            if (!string.IsNullOrEmpty(ns))
            {
                sb.AppendLine($"namespace {ns};");
                sb.AppendLine();
            }

            // Class header
            var header = new StringBuilder($"public partial class {className}");
            var typeParameters = parts.TypeParameters.Select(SplitTypeParameter).ToList();
            if (typeParameters.Count > 0)
                header.Append($"<{string.Join(", ", typeParameters.Select(t => t.Name))}>");

            // As in Razor, the component's own @inherits wins, then the nearest _Imports.razor's; every @implements applies.
            var baseTypes = new List<string>();
            var inherits = parts.Inherits ?? imports.LastOrDefault(i => i.Inherits != null)?.Inherits;
            if (inherits != null)
                baseTypes.Add(inherits);
            baseTypes.AddRange(imports.SelectMany(i => i.Implements).Concat(parts.Implements).Distinct());
            if (baseTypes.Count > 0)
                header.Append($" : {string.Join(", ", baseTypes)}");

            foreach (var constraint in typeParameters.Where(t => t.Constraint.Length > 0))
                header.Append($" {constraint.Constraint}");

            sb.AppendLine(header.ToString());
            sb.AppendLine("{");

            // Every _Imports.razor injects into the component too; a property injected twice keeps the nearest @inject.
            var injects = imports.SelectMany(i => i.Injects)
                .Concat(parts.Injects)
                .GroupBy(inject => inject.Name)
                .Select(group => group.Last());
            foreach (var (type, name) in injects)
                sb.AppendLine($"    [Inject] private {type} {name} {{ get; set; }} = default!;");

            // Markup
            sb.AppendLine("    protected override void BuildRenderTree(RenderTreeBuilder __builder)");
            sb.AppendLine("    {");
            foreach (var component in parts.Components)
                sb.AppendLine($"        __builder.OpenComponent<{component}>(0);");
            foreach (var statement in parts.Statements)
                sb.AppendLine($"        {statement}");
            sb.AppendLine("    }");

            // @code blocks
            foreach (var code in parts.CodeBlocks)
            {
                sb.AppendLine();
                sb.AppendLine(code.Trim('\r', '\n'));
            }

            sb.AppendLine("}");

            return sb.ToString();
        }

        internal static RazorParts Scan(string razorContent)
        {
            var parts = new RazorParts();
            new RazorScanner(RazorCommentRegex.Replace(razorContent, string.Empty), parts).Scan();
            return parts;
        }

        private static (string Name, string Constraint) SplitTypeParameter(string typeParameter)
        {
            var whereIndex = typeParameter.IndexOf(" where ", StringComparison.Ordinal);
            return whereIndex < 0
                ? (typeParameter.Trim(), string.Empty)
                : (typeParameter.Substring(0, whereIndex).Trim(), typeParameter.Substring(whereIndex + 1).Trim());
        }

        internal static string SanitizeIdentifier(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');

            if (sb.Length == 0 || char.IsDigit(sb[0]))
                sb.Insert(0, '_');

            return sb.ToString();
        }

        private static string GetFileName(string path)
        {
            var normalized = path.Replace('\\', '/');
            return normalized.Substring(normalized.LastIndexOf('/') + 1);
        }


        internal class RazorParts
        {
            public List<string> Usings { get; } = new();
            public List<(string Type, string Name)> Injects { get; } = new();
            public string? Inherits { get; set; }
            public List<string> Implements { get; } = new();
            public List<string> TypeParameters { get; } = new();
            public string? Namespace { get; set; }
            public List<string> CodeBlocks { get; } = new();
            public List<string> Statements { get; } = new();
            public List<string> Components { get; } = new();
        }


        /// <summary>
        /// A small, forgiving scanner over Razor markup. It doesn't try to be a full Razor parser;
        /// it finds the C# the markup refers to so the diagram can show those relationships.
        /// </summary>
        private class RazorScanner
        {
            private readonly string text;
            private readonly RazorParts parts;
            private int pos;

            public RazorScanner(string text, RazorParts parts)
            {
                this.text = text;
                this.parts = parts;
            }

            public void Scan()
            {
                while (pos < text.Length)
                {
                    switch (text[pos])
                    {
                        case '@':
                            ScanTransition();
                            break;
                        case '<':
                            ScanTag();
                            break;
                        default:
                            pos++;
                            break;
                    }
                }
            }

            // ---------- @ TRANSITIONS ----------
            private void ScanTransition()
            {
                if (pos + 1 >= text.Length)
                {
                    pos++;
                    return;
                }

                char next = text[pos + 1];

                // "@@" is an escaped @, and an @ right after a letter is an e-mail address.
                if (next == '@' || (pos > 0 && IsIdentifierChar(text[pos - 1])))
                {
                    pos += 2;
                    return;
                }

                if (next == '{')
                {
                    // The block goes before what its markup adds, as in the source.
                    int index = parts.Statements.Count;
                    var (block, end) = ReadCodeBlock(pos + 1);
                    // A block of only markup leaves no C# to add.
                    if (!string.IsNullOrWhiteSpace(block))
                        AddStatement("{" + block + "}", index);
                    pos = end;
                    return;
                }

                if (next == '(')
                {
                    var (expression, end) = ReadBalanced(pos + 1);
                    AddExpression($"({expression})");
                    pos = end;
                    return;
                }

                if (!IsIdentifierStart(next))
                {
                    pos++;
                    return;
                }

                int wordEnd = ReadIdentifierEnd(pos + 1);
                var word = text.Substring(pos + 1, wordEnd - pos - 1);

                if (IsControlFlowStart(pos + 1))
                {
                    // Like a block, the statement goes before what its markup adds.
                    int index = parts.Statements.Count;
                    var (statement, end) = ReadControlFlow(pos + 1);
                    AddStatement(statement, index);
                    pos = end;
                    return;
                }

                switch (word)
                {
                    case "code":
                    case "functions":
                        int braceIndex = SkipWhitespace(wordEnd);
                        if (braceIndex < text.Length && text[braceIndex] == '{')
                        {
                            var (code, end) = ReadCodeBlock(braceIndex);
                            parts.CodeBlocks.Add(code);
                            pos = end;
                        }
                        else
                        {
                            pos = wordEnd;
                        }
                        return;

                    case "using":
                        parts.Usings.Add(ReadDirectiveValue(wordEnd)); // a using statement is control flow, read above
                        return;

                    case "inject":
                        var inject = ReadDirectiveValue(wordEnd);
                        int split = inject.LastIndexOfAny(new[] { ' ', '\t' });
                        if (split > 0)
                            parts.Injects.Add((inject.Substring(0, split).Trim(), inject.Substring(split + 1).Trim()));
                        return;

                    case "inherits":
                        parts.Inherits = ReadDirectiveValue(wordEnd);
                        return;

                    case "implements":
                        parts.Implements.Add(ReadDirectiveValue(wordEnd));
                        return;

                    case "namespace":
                        parts.Namespace = ReadDirectiveValue(wordEnd);
                        return;

                    case "typeparam":
                        parts.TypeParameters.Add(ReadDirectiveValue(wordEnd));
                        return;

                    case "page":
                    case "layout":
                    case "attribute":
                    case "rendermode":
                    case "preservewhitespace":
                    case "model":
                        ReadDirectiveValue(wordEnd);
                        return;

                    case "catch":
                        pos = PeekAfterWhitespace(wordEnd) == '(' ? ReadBalanced(SkipWhitespace(wordEnd)).End : wordEnd;
                        return;

                    case "else":
                    case "finally":
                        pos = wordEnd;
                        return;

                    case "await":
                        var (awaited, awaitEnd) = ReadImplicitExpression(SkipWhitespace(wordEnd));
                        AddExpression(awaited);
                        pos = Math.Max(awaitEnd, wordEnd);
                        return;

                    default:
                        var (expression, expressionEnd) = ReadImplicitExpression(pos + 1);
                        AddExpression(expression);
                        pos = expressionEnd;
                        return;
                }
            }

            // @Name, @Name.Member, @Name(args), @Name[index], @Name?.Member and chains of those.
            private (string Expression, int End) ReadImplicitExpression(int start)
            {
                if (start >= text.Length || !IsIdentifierStart(text[start]))
                    return (string.Empty, start);

                int i = ReadIdentifierEnd(start);
                while (i < text.Length)
                {
                    char c = text[i];
                    if (c is '(' or '[')
                        i = ReadBalanced(i).End;
                    else if (c == '.' && i + 1 < text.Length && IsIdentifierStart(text[i + 1]))
                        i = ReadIdentifierEnd(i + 1);
                    else if (c == '?' && i + 2 < text.Length && text[i + 1] == '.' && IsIdentifierStart(text[i + 2]))
                        i = ReadIdentifierEnd(i + 2);
                    else
                        break;
                }

                return (text.Substring(start, i - start), i);
            }

            // ---------- CONTROL FLOW ----------
            private static readonly HashSet<string> ControlFlowKeywords = new(StringComparer.Ordinal)
            {
                "if", "for", "foreach", "while", "do", "switch", "lock", "using", "try"
            };

            // Whether an @if, @foreach and so on starts at i. @using is a statement only with a "(", otherwise it's the directive.
            private bool IsControlFlowStart(int i)
            {
                var word = ReadWord(i);
                return ControlFlowKeywords.Contains(word) && (word != "using" || PeekAfterWhitespace(i + word.Length) == '(');
            }

            /// <summary>
            /// Reads a control-flow statement (@if, @foreach, @switch, @try, ...) from its keyword to the end of its last
            /// block, with the clauses that continue it: an if's else and else if, a try's catch and finally, a do's while.
            /// Its blocks are code blocks, read like @{ }. Returns the statement's C# and the index after it.
            /// </summary>
            private (string Statement, int End) ReadControlFlow(int start)
            {
                var statement = new StringBuilder();
                var clause = ReadWord(start);
                int i = start;
                while (true)
                {
                    // The clause's keywords and the C# in its parentheses, as in "else if (x)" or "catch (Exception ex) when (Log(ex))".
                    int headerEnd = ReadIdentifierEnd(SkipWhitespace(i));
                    while (true)
                    {
                        int j = SkipWhitespace(headerEnd);
                        if (j < text.Length && text[j] == '(')
                            headerEnd = ReadBalanced(j).End;
                        else if (ReadWord(j) is "if" or "when")
                            headerEnd = ReadIdentifierEnd(j);
                        else
                            break;
                    }

                    int blockStart = SkipWhitespace(headerEnd);
                    if (blockStart >= text.Length || text[blockStart] != '{')
                    {
                        // Razor allows no block only for a single C# statement; an empty block keeps the clause's condition.
                        statement.Append(text, i, headerEnd - i).Append(" { }");
                        return (statement.ToString(), headerEnd);
                    }

                    var (block, blockEnd) = ReadCodeBlock(blockStart);
                    statement.Append(text, i, blockStart - i).Append('{').Append(block).Append('}');
                    i = blockEnd;

                    int nextStart = SkipWhitespace(i);
                    var next = ReadWord(nextStart);
                    if (clause == "do" && next == "while")
                    {
                        int end = SkipWhitespace(nextStart + next.Length);
                        if (end < text.Length && text[end] == '(')
                            end = ReadBalanced(end).End;
                        if (end < text.Length && text[end] == ';')
                            end++;

                        statement.Append(text, i, end - i);
                        return (statement.ToString(), end);
                    }

                    bool continues = (clause, next) switch
                    {
                        ("if", "else") => true,
                        ("try" or "catch", "catch" or "finally") => true,
                        _ => false
                    };
                    if (!continues)
                        return (statement.ToString(), i);

                    // An else if can be followed by another else; a plain else, like a finally, is the last clause.
                    clause = next == "else" && ReadWord(SkipWhitespace(nextStart + next.Length)) == "if" ? "if" : next;
                }
            }

            // ---------- MARKUP IN C# ----------
            /// <summary>
            /// Reads a code block (@{ }, @code { } or a block of @if and the like) from its opening brace to its closing one.
            /// A code block is C#: markup can only start where a statement can (an element, &lt;text&gt;, an @: line or an
            /// expression to write out, like @item.Name), and a template (RenderFragment x = @&lt;div&gt;...&lt;/div&gt;;) only
            /// in code, not in a string or a comment. The markup is scanned like the rest of the markup and left out of the C#,
            /// and each template is replaced with "default!", so the code stays valid C#. Returns that code and the index
            /// after the closing brace.
            /// </summary>
            private (string Code, int End) ReadCodeBlock(int openIndex)
            {
                var code = new StringBuilder();
                int depth = 1;
                int parentheses = 0;
                char previous = '{';

                int i = openIndex + 1;
                int copied = i;
                while (i < text.Length)
                {
                    char c = text[i];

                    int skipped = SkipLiteralOrComment(i);
                    if (skipped > i)
                    {
                        // A comment doesn't change where a statement can start, a literal does.
                        if (c != '/')
                            previous = c;
                        i = skipped;
                        continue;
                    }
                    if (char.IsWhiteSpace(c))
                    {
                        i++;
                        continue;
                    }

                    // A statement starts after a {, } or ; or a case label's colon, outside brackets.
                    // Anywhere else a "<" is a generic or a comparison.
                    bool statementStart = parentheses == 0 && previous is '{' or '}' or ';' or ':';
                    if (statementStart && c == '@' && IsControlFlowStart(i + 1))
                    {
                        // In code, @if and the like are C# like the rest: only the @ is left out.
                        code.Append(text, copied, i - copied);
                        copied = ++i;
                        continue;
                    }

                    char next = i + 1 < text.Length ? text[i + 1] : '\0';
                    bool template = c == '@' && IsElementStart(i + 1);
                    int markupEnd = -1;
                    if (template)
                    {
                        markupEnd = ScanElement(i + 1);
                    }
                    else if (statementStart && IsElementStart(i))
                    {
                        markupEnd = ScanElement(i);
                    }
                    else if (statementStart && c == '@' && next == ':')
                    {
                        int lineEnd = text.IndexOf('\n', i);
                        markupEnd = lineEnd < 0 ? text.Length : lineEnd;
                        new RazorScanner(text.Substring(i, markupEnd - i), parts).Scan();
                    }
                    else if (statementStart && c == '@' && (IsIdentifierStart(next) || next == '('))
                    {
                        // An expression the block writes out, as @item.Name does in markup.
                        markupEnd = ScanTransitionAt(i);
                    }

                    if (markupEnd >= 0)
                    {
                        code.Append(text, copied, i - copied).Append(template ? "default!" : string.Empty);
                        i = copied = markupEnd;
                        continue;
                    }

                    if (c == '{')
                    {
                        depth++;
                    }
                    else if (c == '}' && --depth == 0)
                    {
                        code.Append(text, copied, i - copied);
                        return (code.ToString(), i + 1);
                    }
                    else if (c is '(' or '[')
                    {
                        parentheses++;
                    }
                    else if (c is ')' or ']' && parentheses > 0)
                    {
                        parentheses--;
                    }

                    previous = c;
                    i++;
                }

                code.Append(text, copied, text.Length - copied);
                return (code.ToString(), text.Length);
            }

            private bool IsElementStart(int i) =>
                i + 1 < text.Length && text[i] == '<' && IsIdentifierStart(text[i + 1]);

            // Scans the @ transition at the given index like markup does, and returns the index after it.
            private int ScanTransitionAt(int at)
            {
                var scanner = new RazorScanner(text, parts) { pos = at };
                scanner.ScanTransition();
                return scanner.pos;
            }

            /// <summary>
            /// Scans the element that starts at the given index like the rest of the markup, up to the end of its matching
            /// closing tag, and returns the index after it. The C# in it is read as C#, so the "&lt;" in @(a &lt; b) or
            /// @if (a &lt; b) isn't taken for a tag.
            /// </summary>
            private int ScanElement(int start)
            {
                var scanner = new RazorScanner(text, parts) { pos = start };
                int depth = 0;
                while (scanner.pos < text.Length)
                {
                    int at = scanner.pos;
                    if (text[at] == '@')
                    {
                        scanner.ScanTransition();
                    }
                    else if (text[at] == '<' && at + 1 < text.Length && text[at + 1] == '/')
                    {
                        int tagEnd = text.IndexOf('>', at);
                        scanner.pos = tagEnd < 0 ? text.Length : tagEnd + 1;
                        if (--depth <= 0)
                            return scanner.pos;
                    }
                    else if (text[at] == '<')
                    {
                        if (scanner.ScanTag())
                            depth++;
                        else if (depth == 0)
                            return scanner.pos; // a self-closing or void element
                    }
                    else
                    {
                        scanner.pos++;
                    }
                }

                return text.Length;
            }

            // ---------- TAGS ----------
            private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
            {
                "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"
            };

            // Scans the tag at pos. Returns whether it opens an element that a closing tag ends, so not a self-closing or void one.
            private bool ScanTag()
            {
                if (string.CompareOrdinal(text, pos, "<!--", 0, 4) == 0)
                {
                    int commentEnd = text.IndexOf("-->", pos + 4, StringComparison.Ordinal);
                    pos = commentEnd < 0 ? text.Length : commentEnd + 3;
                    return false;
                }

                if (pos + 1 >= text.Length || !IsIdentifierStart(text[pos + 1]))
                {
                    // Closing tag or a plain "<" in text.
                    pos++;
                    return false;
                }

                int nameEnd = pos + 1;
                while (nameEnd < text.Length && (IsIdentifierChar(text[nameEnd]) || text[nameEnd] is '.' or '-' or ':'))
                    nameEnd++;

                var tagName = text.Substring(pos + 1, nameEnd - pos - 1);

                // Components start with an upper case letter; HTML elements don't.
                bool isComponent = char.IsUpper(tagName[0]) && !tagName.Contains('-') && !tagName.Contains(':');
                if (isComponent && !parts.Components.Contains(tagName))
                    parts.Components.Add(tagName);

                int i = nameEnd;
                bool selfClosing = false;
                while (i < text.Length)
                {
                    i = SkipWhitespace(i);
                    if (i >= text.Length)
                        break;

                    if (text[i] == '>')
                    {
                        i++;
                        break;
                    }
                    if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '>')
                    {
                        i += 2;
                        selfClosing = true;
                        break;
                    }

                    int attributeStart = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('=' or '>') && !(text[i] == '/' && i + 1 < text.Length && text[i + 1] == '>'))
                        i++;
                    var attributeName = text.Substring(attributeStart, i - attributeStart);

                    if (attributeName.Length == 0)
                    {
                        i++;
                        continue;
                    }

                    string? value = null;
                    int valueIndex = SkipWhitespace(i);
                    if (valueIndex < text.Length && text[valueIndex] == '=')
                    {
                        (value, i) = ReadAttributeValue(SkipWhitespace(valueIndex + 1));
                    }

                    if (value != null)
                        HandleAttribute(attributeName, value, isComponent);
                }

                pos = i;
                return !selfClosing && !VoidElements.Contains(tagName);
            }

            private (string Value, int End) ReadAttributeValue(int start)
            {
                if (start >= text.Length)
                    return (string.Empty, start);

                char quote = text[start];
                if (quote is not ('"' or '\''))
                {
                    int end = start;
                    while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '>')
                        end++;
                    return (text.Substring(start, end - start), end);
                }

                // A quoted value can contain @(...) with quotes of its own, e.g. class="@(x ? "a" : "b")".
                int i = start + 1;
                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] == '@' && i + 1 < text.Length && text[i + 1] == '(')
                        i = ReadBalanced(i + 1).End;
                    else
                        i++;
                }

                return (text.Substring(start + 1, Math.Min(i, text.Length) - start - 1), Math.Min(i + 1, text.Length));
            }

            private void HandleAttribute(string name, string value, bool isComponent)
            {
                if (name.StartsWith("@on", StringComparison.Ordinal))
                {
                    // @onclick="Save", but not @onclick:preventDefault="true"
                    if (!name.Contains(':'))
                        AddHandler(value);
                    return;
                }

                if (name.StartsWith("@bind", StringComparison.Ordinal))
                {
                    if (name.EndsWith(":after", StringComparison.Ordinal) || name.EndsWith(":set", StringComparison.Ordinal))
                        AddHandler(value);
                    else if (!name.Contains(':') || name.EndsWith(":get", StringComparison.Ordinal))
                        AddExpression(StripTransition(value));
                    return;
                }

                if (name == "@attributes")
                {
                    AddExpression(StripTransition(value));
                    return;
                }

                if (name.StartsWith('@'))
                    return; // @ref, @key, @rendermode, @formname: nothing to diagram

                // EventCallback parameters on components are named On* by convention: OnClick="Save".
                if (isComponent && name.Length > 2 && name.StartsWith("On", StringComparison.Ordinal) && char.IsUpper(name[2]))
                {
                    AddHandler(value);
                    return;
                }

                if (value.Contains('@'))
                    new RazorScanner(value, parts).Scan();
            }

            // ---------- EMITTING C# ----------
            private void AddHandler(string value)
            {
                var handler = StripTransition(value);
                if (handler.Length == 0)
                    return;

                var expression = SyntaxFactory.ParseExpression(handler);
                if (expression.ContainsDiagnostics)
                    return;

                // A method group is turned into a call, so the analyzer sees which method handles the event.
                AddStatement(expression is IdentifierNameSyntax or MemberAccessExpressionSyntax or GenericNameSyntax
                    ? $"{handler}();"
                    : $"_ = {handler};");
            }

            private void AddExpression(string expression)
            {
                if (!string.IsNullOrWhiteSpace(expression))
                    AddStatement($"_ = {expression.Trim()};");
            }

            private void AddStatement(string statement, int? index = null)
            {
                statement = statement.Trim();

                // Anything that doesn't parse is dropped, so the generated class always stays valid C#.
                if (SyntaxFactory.ParseStatement(statement).ContainsDiagnostics)
                    return;

                if (!parts.Statements.Contains(statement))
                    parts.Statements.Insert(index ?? parts.Statements.Count, statement);
            }

            private static string StripTransition(string value)
            {
                value = value.Trim();
                if (value.StartsWith("@(", StringComparison.Ordinal) && value.EndsWith(')'))
                    return value.Substring(2, value.Length - 3).Trim();
                if (value.StartsWith('@'))
                    return value.Substring(1).Trim();
                return value;
            }

            // ---------- LOW-LEVEL READING ----------
            private string ReadDirectiveValue(int start)
            {
                int end = text.IndexOf('\n', start);
                if (end < 0)
                    end = text.Length;

                pos = end;
                return text.Substring(start, end - start).Trim().TrimEnd(';').Trim();
            }

            /// <summary>
            /// Reads from an opening (, [ or { to its matching closing bracket, skipping over C# strings,
            /// character literals and comments. Returns the text between the brackets and the index after the closing one.
            /// </summary>
            private (string Inner, int End) ReadBalanced(int openIndex)
            {
                char open = text[openIndex];
                char close = open switch { '(' => ')', '[' => ']', _ => '}' };

                int depth = 0;
                int i = openIndex;
                while (i < text.Length)
                {
                    int skipped = SkipLiteralOrComment(i);
                    if (skipped > i)
                    {
                        i = skipped;
                        continue;
                    }

                    char c = text[i];
                    if (c == open)
                    {
                        depth++;
                    }
                    else if (c == close)
                    {
                        depth--;
                        if (depth == 0)
                            return (text.Substring(openIndex + 1, i - openIndex - 1), i + 1);
                    }

                    i++;
                }

                return (text.Substring(Math.Min(openIndex + 1, text.Length)), text.Length);
            }

            // Returns the index after the C# string, character literal or comment that starts at i, or i if none does.
            private int SkipLiteralOrComment(int i)
            {
                char c = text[i];
                if (c == '"')
                    return SkipString(i);
                if (c == '\'')
                    return SkipCharLiteral(i);

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    int lineEnd = text.IndexOf('\n', i);
                    return lineEnd < 0 ? text.Length : lineEnd;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int commentEnd = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    return commentEnd < 0 ? text.Length : commentEnd + 2;
                }

                return i;
            }

            private int SkipString(int quoteIndex)
            {
                // Raw string literal: """ ... """
                if (string.CompareOrdinal(text, quoteIndex, "\"\"\"", 0, 3) == 0)
                {
                    int rawEnd = text.IndexOf("\"\"\"", quoteIndex + 3, StringComparison.Ordinal);
                    return rawEnd < 0 ? text.Length : rawEnd + 3;
                }

                bool verbatim = (quoteIndex > 0 && text[quoteIndex - 1] == '@')
                    || (quoteIndex > 1 && text[quoteIndex - 1] == '$' && text[quoteIndex - 2] == '@');

                int i = quoteIndex + 1;
                while (i < text.Length)
                {
                    char c = text[i];
                    if (verbatim)
                    {
                        if (c == '"')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '"')
                            {
                                i += 2;
                                continue;
                            }
                            return i + 1;
                        }
                    }
                    else
                    {
                        if (c == '\\')
                        {
                            i += 2;
                            continue;
                        }
                        if (c == '"')
                            return i + 1;
                        if (c == '\n')
                            return i; // unterminated: stop at the end of the line
                    }
                    i++;
                }

                return text.Length;
            }

            // Only skips things that really look like char literals, so apostrophes in markup text are left alone.
            private int SkipCharLiteral(int quoteIndex)
            {
                if (quoteIndex + 2 < text.Length && text[quoteIndex + 1] != '\\' && text[quoteIndex + 2] == '\'')
                    return quoteIndex + 3;

                if (quoteIndex + 1 < text.Length && text[quoteIndex + 1] == '\\')
                {
                    int end = text.IndexOf('\'', quoteIndex + 2);
                    if (end > 0 && end - quoteIndex <= 10)
                        return end + 1;
                }

                return quoteIndex + 1;
            }

            private int SkipWhitespace(int i)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;
                return i;
            }

            private char PeekAfterWhitespace(int i)
            {
                i = SkipWhitespace(i);
                return i < text.Length ? text[i] : '\0';
            }

            private int ReadIdentifierEnd(int start)
            {
                int i = start;
                while (i < text.Length && IsIdentifierChar(text[i]))
                    i++;
                return i;
            }

            private string ReadWord(int start) => text.Substring(start, ReadIdentifierEnd(start) - start);

            private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

            private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
        }
    }


    /// <summary>
    /// What a Razor file needs from the rest of the project to get the right namespace and directives:
    /// project files (root namespace), _Imports.razor files and code-behind (.razor.cs) files.
    /// </summary>
    public class RazorProjectContext
    {
        private static readonly Regex RootNamespaceRegex = new(@"<RootNamespace>\s*([^<]+?)\s*</RootNamespace>", RegexOptions.Compiled);

        private readonly List<(string Directory, string RootNamespace)> projects = new();
        private readonly Dictionary<string, RazorExtractor.RazorParts> imports = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> codeBehindNamespaces = new(StringComparer.OrdinalIgnoreCase);

        public static RazorProjectContext Build(IEnumerable<SourceFile> files)
        {
            var context = new RazorProjectContext();

            foreach (var file in files)
            {
                var path = Normalize(file.Path);

                if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    var match = RootNamespaceRegex.Match(file.Content);
                    var rootNamespace = match.Success && !match.Groups[1].Value.Contains("$(")
                        ? match.Groups[1].Value
                        : Path.GetFileNameWithoutExtension(path);
                    context.projects.Add((GetDirectory(path), SanitizeNamespace(rootNamespace)));
                }
                else if (RazorExtractor.IsImportsFile(path))
                {
                    context.imports[GetDirectory(path)] = RazorExtractor.Scan(file.Content);
                }
                else if (path.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase))
                {
                    var razorPath = path.Substring(0, path.Length - 3);
                    var className = Path.GetFileNameWithoutExtension(razorPath);
                    var ns = FindNamespaceOfClass(file.Content, className);
                    if (ns != null)
                        context.codeBehindNamespaces[razorPath] = ns;
                }
            }

            return context;
        }

        public string? GetNamespace(string razorPath)
        {
            var path = Normalize(razorPath);

            // A code-behind class has to be in the same namespace, so it's the most reliable source.
            if (codeBehindNamespaces.TryGetValue(path, out var codeBehindNamespace))
                return codeBehindNamespace;

            var directory = GetDirectory(path);

            // @namespace in an _Imports.razor applies to its folder, with sub folders appended.
            for (var dir = directory; dir != null; dir = GetParent(dir))
            {
                if (imports.TryGetValue(dir, out var import) && import.Namespace != null)
                    return Combine(import.Namespace, GetRelativeSegments(dir, directory));
            }

            // Otherwise: the project's root namespace plus the folder path.
            var project = projects
                .Where(p => IsAncestorOrSelf(p.Directory, directory))
                .OrderByDescending(p => p.Directory.Length)
                .FirstOrDefault();

            return project.RootNamespace != null
                ? Combine(project.RootNamespace, GetRelativeSegments(project.Directory, directory))
                : null;
        }

        public IEnumerable<string> GetImportedUsings(string razorPath) =>
            GetImports(razorPath).SelectMany(i => i.Usings);

        // The _Imports.razor files in the Razor file's folder and the folders above it, the farthest first.
        internal IReadOnlyList<RazorExtractor.RazorParts> GetImports(string razorPath)
        {
            var applicable = new List<RazorExtractor.RazorParts>();
            for (var dir = GetDirectory(Normalize(razorPath)); dir != null; dir = GetParent(dir))
            {
                if (imports.TryGetValue(dir, out var import))
                    applicable.Add(import);
            }

            applicable.Reverse();
            return applicable;
        }

        private static string? FindNamespaceOfClass(string source, string className)
        {
            var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
            var type = root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault(t => t.Identifier.Text == className);

            if (type == null)
                return null;

            var names = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).Reverse();
            return string.Join(".", names);
        }

        private static string Combine(string ns, IEnumerable<string> segments) =>
            string.Join(".", new[] { ns }.Concat(segments.Select(RazorExtractor.SanitizeIdentifier)));

        private static IEnumerable<string> GetRelativeSegments(string ancestor, string directory) =>
            directory.Substring(ancestor.Length).Split('/', StringSplitOptions.RemoveEmptyEntries);

        private static bool IsAncestorOrSelf(string ancestor, string directory) =>
            ancestor.Length == 0
            || directory.Equals(ancestor, StringComparison.OrdinalIgnoreCase)
            || directory.StartsWith(ancestor + "/", StringComparison.OrdinalIgnoreCase);

        private static string SanitizeNamespace(string ns) =>
            string.Join(".", ns.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(RazorExtractor.SanitizeIdentifier));

        private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

        private static string GetDirectory(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash < 0 ? string.Empty : path.Substring(0, slash);
        }

        private static string? GetParent(string directory)
        {
            if (directory.Length == 0)
                return null;

            int slash = directory.LastIndexOf('/');
            return slash < 0 ? string.Empty : directory.Substring(0, slash);
        }
    }
}
