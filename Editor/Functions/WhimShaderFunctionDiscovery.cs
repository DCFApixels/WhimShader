using System;
using System.Collections.Generic;

namespace DCFApixels.WhimShader
{
    // Adapted from WhimTex's ShaderGraphFunctionParser, without its graph/metadata model.
    // Lexical discovery only: no include loading, macro expansion or branch evaluation.
    internal static class WhimShaderFunctionDiscovery
    {
        internal sealed class Definition
        {
            internal string Name;
            internal string ReturnType;
            internal string Body;
            internal List<Argument> Arguments;

            // Half-open UTF-16 offsets into the original source. Source includes the
            // signature and braces; Body excludes braces. Leading comments are excluded.
            internal int SourceStart;
            internal int SourceEnd;
            internal int BodyStart;
            internal int BodyEnd;
        }

        internal sealed class Argument
        {
            internal string Name;
            internal string Type;
            internal string Direction; // "in" (also the default), "out", or "inout".
        }

        sealed class Token
        {
            internal string Text;
            internal int Start, End, Line;
            internal int Closing = -1;
            internal bool Directive;
        }

        /// <summary>
        /// Returns supported definitions in source order, preserving names, precision
        /// suffixes, type spellings and body text. Overloads and conditional variants
        /// remain separate entries. Prototypes and unsupported signatures are skipped.
        /// Throws ArgumentNullException for null and FormatException for unbalanced
        /// delimiters, unclosed comments/literals or excessive namespace nesting.
        /// The source must be lexically balanced without preprocessing; a directive
        /// inside a signature makes that signature unsupported, rather than combining
        /// tokens from potentially different preprocessor branches.
        /// </summary>
        internal static List<Definition> Parse(string source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var tokens = Lex(source);
            MatchDelimiters(tokens);
            var result = new List<Definition>();
            ReadScope(source, tokens, 0, tokens.Count, "", 0, result);
            return result;
        }

        static void ReadScope(string source, List<Token> tokens, int start, int end,
            string scope, int depth, List<Definition> result)
        {
            if (depth > 64) throw Error(tokens[start - 1].Line, "Namespace nesting exceeds 64 levels.");
            int index = start;
            while (index < end)
            {
                if (tokens[index].Directive || tokens[index].Text == ";") { index++; continue; }
                int cursor = index;
                if (tokens[cursor].Text == "namespace")
                {
                    cursor++;
                    if (ReadQualifiedName(tokens, ref cursor, end, out string name) &&
                        cursor < end && tokens[cursor].Text == "{")
                    {
                        int close = tokens[cursor].Closing;
                        ReadScope(source, tokens, cursor + 1, close, scope + name + "::", depth + 1, result);
                        index = close + 1;
                        continue;
                    }
                }
                else
                {
                    while (cursor < end && IsFunctionQualifier(tokens[cursor].Text)) cursor++;
                    if (cursor < end && IsIdentifier(tokens[cursor].Text))
                    {
                        string returnType = tokens[cursor++].Text;
                        if (ReadQualifiedName(tokens, ref cursor, end, out string name) &&
                            cursor < end && tokens[cursor].Text == "(")
                        {
                            int open = cursor, close = tokens[open].Closing;
                            int body = close + 1;
                            // A simple return semantic is not a graph port. Argument
                            // semantics, annotations and other trailing syntax are unsupported.
                            if (body + 1 < end && tokens[body].Text == ":" && IsIdentifier(tokens[body + 1].Text)) body += 2;
                            if (body < end && tokens[body].Text == "{")
                            {
                                int bodyClose = tokens[body].Closing;
                                if ((returnType == "void" || IsSupportedType(returnType)) &&
                                    TryReadArguments(tokens, open + 1, close, out var arguments))
                                {
                                    int bodyStart = tokens[body].End, bodyEnd = tokens[bodyClose].Start;
                                    result.Add(new Definition
                                    {
                                        Name = scope + name,
                                        ReturnType = returnType,
                                        Arguments = arguments,
                                        Body = source.Substring(bodyStart, bodyEnd - bodyStart),
                                        SourceStart = tokens[index].Start,
                                        SourceEnd = tokens[bodyClose].End,
                                        BodyStart = bodyStart,
                                        BodyEnd = bodyEnd
                                    });
                                }
                                index = bodyClose + 1;
                                continue;
                            }
                        }
                    }
                }
                // Skip a whole declaration, not individual tokens: a supported-looking
                // suffix of a template, attribute, macro or struct method is not a function.
                index = SkipDeclaration(tokens, index, end);
            }
        }

        static int SkipDeclaration(List<Token> tokens, int index, int end)
        {
            while (index < end)
            {
                Token token = tokens[index++];
                if (token.Text == ";") break;
                if (token.Closing < 0) continue;
                index = token.Closing + 1;
                if (token.Text == "{") break;
            }
            return index;
        }

        static bool TryReadArguments(List<Token> tokens, int start, int end, out List<Argument> arguments)
        {
            arguments = new List<Argument>();
            if (start + 1 == end && tokens[start].Text == "void") return true;
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (start < end)
            {
                int stop = start;
                while (stop < end && tokens[stop].Text != ",") stop++;
                int cursor = start;
                string direction = "in";
                bool hasDirection = false;
                while (cursor < stop)
                {
                    string text = tokens[cursor].Text;
                    if (text == "in" || text == "out" || text == "inout")
                    {
                        if (hasDirection) return false;
                        direction = text;
                        hasDirection = true;
                    }
                    else if (!IsArgumentQualifier(text)) break;
                    cursor++;
                }
                // This also excludes arrays, defaults, generics, semantics, unnamed
                // parameters, directives and unexpanded signature macros.
                if (cursor + 2 != stop || !IsSupportedType(tokens[cursor].Text) ||
                    !IsIdentifier(tokens[cursor + 1].Text) || !names.Add(tokens[cursor + 1].Text)) return false;
                arguments.Add(new Argument
                {
                    Name = tokens[cursor + 1].Text,
                    Type = tokens[cursor].Text,
                    Direction = direction
                });
                if (stop == end) return true;
                start = stop + 1;
                if (start == end) return false;
            }
            return true;
        }

        static bool IsSupportedType(string type)
        {
            switch (type)
            {
                case "float": case "float1": case "float2": case "float3": case "float4":
                case "half": case "half1": case "half2": case "half3": case "half4":
                case "bool":
                case "float2x2": case "float3x3": case "float4x4":
                case "half2x2": case "half3x3": case "half4x4":
                case "UnityTexture2D": case "UnityTexture3D": case "UnityTexture2DArray":
                case "UnityTextureCube": case "UnitySamplerState":
                case "Texture2D": case "Texture3D": case "Texture2DArray":
                case "TextureCube": case "SamplerState":
                    return true;
                default:
                    return false;
            }
        }

        static bool IsFunctionQualifier(string text) => text == "static" || text == "inline" || text == "const" || text == "precise";
        static bool IsArgumentQualifier(string text) => text == "const" || text == "precise" || text == "uniform";

        static bool ReadQualifiedName(List<Token> tokens, ref int index, int end, out string name)
        {
            name = null;
            if (index >= end || !IsIdentifier(tokens[index].Text)) return false;
            name = tokens[index++].Text;
            while (index < end && tokens[index].Text == "::")
            {
                if (index + 1 >= end || !IsIdentifier(tokens[index + 1].Text)) return false;
                name += "::" + tokens[index + 1].Text;
                index += 2;
            }
            return true;
        }

        static bool IsIdentifierStart(char c) => c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        static bool IsIdentifierPart(char c) => IsIdentifierStart(c) || (c >= '0' && c <= '9');
        static bool IsIdentifier(string text)
        {
            if (text.Length == 0 || !IsIdentifierStart(text[0])) return false;
            for (int i = 1; i < text.Length; i++) if (!IsIdentifierPart(text[i])) return false;
            return true;
        }

        static void MatchDelimiters(List<Token> tokens)
        {
            var openings = new Stack<int>();
            for (int i = 0; i < tokens.Count; i++)
            {
                Token token = tokens[i];
                if (token.Directive) continue;
                string text = token.Text;
                if (text == "{" || text == "(" || text == "[") openings.Push(i);
                else if (text == "}" || text == ")" || text == "]")
                {
                    if (openings.Count == 0) throw Error(token.Line, "Unexpected closing " + text + ".");
                    Token open = tokens[openings.Pop()];
                    string expected = open.Text == "{" ? "}" : open.Text == "(" ? ")" : "]";
                    if (text != expected) throw Error(token.Line, "Expected " + expected + ", found " + text + ".");
                    open.Closing = i;
                }
            }
            if (openings.Count > 0)
            {
                Token open = tokens[openings.Pop()];
                throw Error(open.Line, "Unclosed " + open.Text + ".");
            }
        }

        static List<Token> Lex(string source)
        {
            var tokens = new List<Token>();
            int index = 0, line = 1;
            bool lineStart = true;
            while (index < source.Length)
            {
                char c = source[index];
                if (IsNewline(c)) { ReadNewline(source, ref index, ref line); lineStart = true; continue; }
                if (char.IsWhiteSpace(c) || c == '\uFEFF') { index++; continue; }
                if (StartsWith(source, index, '/', '/')) { SkipLineComment(source, ref index, ref line); continue; }
                if (StartsWith(source, index, '/', '*')) { SkipBlockComment(source, ref index, ref line, ref lineStart); continue; }
                int start = index, startLine = line;
                if (c == '#' && lineStart)
                {
                    SkipDirective(source, ref index, ref line, ref lineStart);
                    tokens.Add(new Token { Text = "#", Start = start, End = index, Line = startLine, Directive = true });
                    continue;
                }
                lineStart = false;
                if (c == '"' || c == '\'') SkipLiteral(source, ref index, ref line);
                else if (IsIdentifierStart(c))
                {
                    index++;
                    while (index < source.Length && IsIdentifierPart(source[index])) index++;
                }
                else if (StartsWith(source, index, ':', ':')) index += 2;
                else index++;
                tokens.Add(new Token { Text = source.Substring(start, index - start), Start = start, End = index, Line = startLine });
            }
            return tokens;
        }

        static void SkipDirective(string source, ref int index, ref int line, ref bool lineStart)
        {
            while (index < source.Length)
            {
                char c = source[index];
                if (IsNewline(c))
                {
                    bool continued = index > 0 && source[index - 1] == '\\';
                    ReadNewline(source, ref index, ref line);
                    if (!continued) break;
                }
                else if (StartsWith(source, index, '/', '*')) SkipBlockComment(source, ref index, ref line, ref lineStart);
                else if (StartsWith(source, index, '/', '/')) SkipLineComment(source, ref index, ref line);
                // Directive payloads (for example #error text) need not contain
                // balanced quotes. Quotes only shield comment markers here.
                else if (c == '"' || c == '\'') SkipLiteral(source, ref index, ref line, true);
                else index++;
            }
            lineStart = true;
        }

        static void SkipLineComment(string source, ref int index, ref int line)
        {
            index += 2;
            while (index < source.Length)
            {
                if (IsNewline(source[index]))
                {
                    if (source[index - 1] != '\\') return;
                    ReadNewline(source, ref index, ref line);
                }
                else index++;
            }
        }

        static void SkipBlockComment(string source, ref int index, ref int line, ref bool lineStart)
        {
            int startLine = line;
            index += 2;
            while (index < source.Length)
            {
                if (StartsWith(source, index, '*', '/')) { index += 2; return; }
                if (IsNewline(source[index])) { ReadNewline(source, ref index, ref line); lineStart = true; }
                else index++;
            }
            throw Error(startLine, "Unclosed block comment.");
        }

        static void SkipLiteral(string source, ref int index, ref int line, bool directive = false)
        {
            int startLine = line;
            char quote = source[index++];
            while (index < source.Length)
            {
                char c = source[index++];
                if (c == quote) return;
                if (c == '\\' && index < source.Length)
                {
                    if (IsNewline(source[index])) ReadNewline(source, ref index, ref line);
                    else index++;
                }
                else if (IsNewline(c))
                {
                    if (directive) { index--; return; }
                    throw Error(startLine, "Unclosed literal.");
                }
            }
            if (!directive) throw Error(startLine, "Unclosed literal.");
        }

        static bool StartsWith(string source, int index, char first, char second) =>
            index + 1 < source.Length && source[index] == first && source[index + 1] == second;

        static bool IsNewline(char c) => c == '\r' || c == '\n';

        static void ReadNewline(string source, ref int index, ref int line)
        {
            if (source[index++] == '\r' && index < source.Length && source[index] == '\n') index++;
            line++;
        }

        static FormatException Error(int line, string message) => new FormatException("HLSL:" + line + ": " + message);
    }
}
