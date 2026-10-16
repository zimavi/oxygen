using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Parsing;

public sealed class Parser
{
    private static readonly IReadOnlyDictionary<TokenKind, PrimitiveType> PrimitiveTypes =
        new Dictionary<TokenKind, PrimitiveType>
        {
            [TokenKind.I8] = PrimitiveType.I8,
            [TokenKind.I16] = PrimitiveType.I16,
            [TokenKind.I32] = PrimitiveType.I32,
            [TokenKind.I64] = PrimitiveType.I64,
            [TokenKind.U8] = PrimitiveType.U8,
            [TokenKind.U16] = PrimitiveType.U16,
            [TokenKind.U32] = PrimitiveType.U32,
            [TokenKind.U64] = PrimitiveType.U64,
            [TokenKind.F32] = PrimitiveType.F32,
            [TokenKind.F64] = PrimitiveType.F64,
            [TokenKind.Bool] = PrimitiveType.Bool,
            [TokenKind.Void] = PrimitiveType.Void
        };

    private readonly IReadOnlyList<Token> _tokens;
    private readonly ILogger _logger;
    private int _position;

    public Parser(IReadOnlyList<Token> tokens, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Count == 0 || tokens[^1].Kind != TokenKind.EndOfFile)
        {
            throw new ArgumentException("The token stream must end with an EOF token.", nameof(tokens));
        }

        _tokens = tokens;
        _logger = logger ?? new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger();
    }

    public CompilationUnit Parse()
    {
        try
        {
            var location = Current.Location;
            var module = ParseModuleDeclaration();
            var imports = ParseImports();
            var structs = new List<StructDeclaration>();
            var functions = new List<FunctionDeclaration>();
            while (!Check(TokenKind.EndOfFile))
            {
                if (IsStructDeclarationStart())
                    structs.Add(ParseStruct());
                else
                    functions.Add(ParseFunction());
            }

            _logger.Information("Parsed {FunctionCount} function(s).", functions.Count);
            return new CompilationUnit(module.Name, module.Visibility, imports, functions, location) { Structs = structs };
        }
        catch (ParseException exception)
        {
            _logger.Error(exception, "Syntax error at {Line}:{Column}", exception.Token.Location.Line,
                exception.Token.Location.Column);
            throw;
        }
    }

    public ParseResult ParseWithDiagnostics(string? sourceName = null)
    {
        var diagnostics = new DiagnosticBag();
        var module = (Name: (string?)null, Visibility: Visibility.Private);
        var imports = new List<ImportDeclaration>();
        var structs = new List<StructDeclaration>();
        try
        {
            module = ParseModuleDeclaration();
            imports.AddRange(ParseImports());
        }
        catch (ParseException exception)
        {
            diagnostics.Add(
                DiagnosticPhase.Parsing,
                exception.Message,
                exception.Token.Location,
                Math.Max(1, exception.Token.Lexeme.Length),
                sourceName);
            Synchronize();
        }

        var functions = new List<FunctionDeclaration>();
        var location = Current.Location;

        while (!Check(TokenKind.EndOfFile))
        {
            try
            {
                if (IsStructDeclarationStart())
                    structs.Add(ParseStruct());
                else
                    functions.Add(ParseFunction());
            }
            catch (ParseException exception)
            {
                diagnostics.Add(
                    DiagnosticPhase.Parsing,
                    exception.Message,
                    exception.Token.Location,
                    Math.Max(1, exception.Token.Lexeme.Length),
                    sourceName);
                Synchronize();
            }
        }

        return new ParseResult(new CompilationUnit(module.Name, module.Visibility, imports, functions, location)
        {
            Structs = structs
        }, diagnostics.Diagnostics);
    }

    private StructDeclaration ParseStruct()
    {
        var location = Current.Location;
        var annotations = ParseAnnotations();
        var visibility = Visibility.Private;
        if (Match(TokenKind.Public))
            visibility = Visibility.Public;
        else
            Match(TokenKind.Private);
        Consume(TokenKind.Struct, "Expected 'struct'.");
        var name = Consume(TokenKind.Identifier, "Expected a struct name.").Lexeme;
        Consume(TokenKind.OpenBrace, "Expected '{' after struct name.");
        var fields = new List<StructFieldDeclaration>();
        while (!Check(TokenKind.CloseBrace) && !Check(TokenKind.EndOfFile))
        {
            var fieldLocation = Current.Location;
            var type = ParseType(allowInferred: false);
            var fieldName = Consume(TokenKind.Identifier, "Expected a field name.").Lexeme;
            Consume(TokenKind.Semicolon, "Expected ';' after struct field.");
            fields.Add(new StructFieldDeclaration(type, fieldName, fieldLocation));
        }
        Consume(TokenKind.CloseBrace, "Expected '}' after struct declaration.");
        Match(TokenKind.Semicolon);
        return new StructDeclaration(name, fields, visibility, location, annotations);
    }

    private (string? Name, Visibility Visibility) ParseModuleDeclaration()
    {
        var visibility = Visibility.Private;
        if (Check(TokenKind.Public) && Peek(1).Kind == TokenKind.Module)
        {
            Advance();
            visibility = Visibility.Public;
        }
        else if (Check(TokenKind.Private) && Peek(1).Kind == TokenKind.Module)
        {
            Advance();
        }

        if (!Match(TokenKind.Module))
            return (null, Visibility.Private);

        var name = ParseQualifiedName("Expected a module name.");
        Consume(TokenKind.Semicolon, "Expected ';' after module declaration.");
        return (name, visibility);
    }

    private IReadOnlyList<ImportDeclaration> ParseImports()
    {
        var imports = new List<ImportDeclaration>();
        while (Match(TokenKind.Import))
        {
            var token = Previous;
            var name = ParseQualifiedName("Expected an imported module name.");
            Consume(TokenKind.Semicolon, "Expected ';' after import declaration.");
            imports.Add(new ImportDeclaration(name, token.Location));
        }

        return imports;
    }

    private FunctionDeclaration ParseFunction()
    {
        var location = Current.Location;
        var annotations = ParseAnnotations();
        var visibility = Visibility.Private;
        if (Match(TokenKind.Public))
            visibility = Visibility.Public;
        else
            Match(TokenKind.Private);
        var isExternal = Match(TokenKind.Extern);
        var returnType = ParseType(allowInferred: false);
        var name = Consume(TokenKind.Identifier, "Expected a function name.").Lexeme;
        Consume(TokenKind.OpenParenthesis, "Expected '(' after function name.");

        var parameters = new List<ParameterDeclaration>();
        if (!Check(TokenKind.CloseParenthesis))
        {
            do
            {
                var parameterLocation = Current.Location;
                var type = ParseType(allowInferred: false);
                var parameterName = Consume(TokenKind.Identifier, "Expected a parameter name.").Lexeme;
                parameters.Add(new ParameterDeclaration(type, parameterName, parameterLocation));
            } while (Match(TokenKind.Comma));
        }

        Consume(TokenKind.CloseParenthesis, "Expected ')' after parameters.");
        var body = isExternal
            ? new BlockStatement([], location)
            : ParseBlock();
        if (isExternal)
            Consume(TokenKind.Semicolon, "Expected ';' after extern function declaration.");
        _logger.Debug("Parsed function {FunctionName}.", name);
        return new FunctionDeclaration(returnType, name, parameters, body, visibility, location, annotations, isExternal);
    }

    private IReadOnlyList<AnnotationSyntax> ParseAnnotations()
    {
        var annotations = new List<AnnotationSyntax>();
        while (Match(TokenKind.At))
        {
            var token = Current.Kind is TokenKind.Identifier or TokenKind.Extern
                ? Advance()
                : throw Error("Expected an annotation name after '@'.", Current);
            var arguments = new List<string>();
            if (Match(TokenKind.OpenParenthesis))
            {
                if (!Check(TokenKind.CloseParenthesis))
                {
                    do
                    {
                        var argument = Advance();
                        if (argument.Kind is not (TokenKind.StringLiteral or TokenKind.IntegerLiteral or TokenKind.Identifier))
                            throw Error("Expected a string, integer, or identifier annotation argument.", argument);
                        arguments.Add(argument.Lexeme);
                    } while (Match(TokenKind.Comma));
                }

                Consume(TokenKind.CloseParenthesis, "Expected ')' after annotation arguments.");
            }

            annotations.Add(new AnnotationSyntax(token.Lexeme, arguments, token.Location));
        }

        return annotations;
    }

    private BlockStatement ParseBlock()
    {
        var location = Consume(TokenKind.OpenBrace, "Expected '{' to start a block.").Location;
        var statements = new List<Statement>();
        while (!Check(TokenKind.CloseBrace) && !Check(TokenKind.EndOfFile))
        {
            statements.Add(ParseStatement());
        }

        Consume(TokenKind.CloseBrace, "Expected '}' to close the block.");
        return new BlockStatement(statements, location);
    }

    private Statement ParseStatement()
    {
        if (Check(TokenKind.OpenBrace))
        {
            return ParseBlock();
        }

        if (Check(TokenKind.Return))
        {
            return ParseReturn();
        }

        if (Check(TokenKind.Const) || Check(TokenKind.Var) || IsType(Current.Kind)
            || (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Identifier))
        {
            return ParseVariableDeclaration();
        }

        if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Equals)
        {
            var location = Current.Location;
            var target = new IdentifierExpression(Advance().Lexeme, location);
            Advance();
            var value = ParseExpression();
            Consume(TokenKind.Semicolon, "Expected ';' after assignment.");
            return new AssignmentStatement(target, value, location);
        }

        var expressionLocation = Current.Location;
        var expression = ParseExpression();
        if (Match(TokenKind.Equals))
        {
            var value = ParseExpression();
            Consume(TokenKind.Semicolon, "Expected ';' after assignment.");
            return new AssignmentStatement(expression, value, expressionLocation);
        }
        Consume(TokenKind.Semicolon, "Expected ';' after expression.");
        return new ExpressionStatement(expression, expressionLocation);
    }

    private VariableDeclarationStatement ParseVariableDeclaration()
    {
        var location = Current.Location;
        var isConst = Match(TokenKind.Const);
        var type = ParseType(allowInferred: true);
        var name = Consume(TokenKind.Identifier, "Expected a variable name.").Lexeme;
        Expression? initializer = null;
        if (Match(TokenKind.Equals))
        {
            initializer = ParseExpression();
        }

        Consume(TokenKind.Semicolon, "Expected ';' after variable declaration.");
        return new VariableDeclarationStatement(type, name, isConst, initializer, location);
    }

    private ReturnStatement ParseReturn()
    {
        var token = Advance();
        Expression? value = null;
        if (!Check(TokenKind.Semicolon))
        {
            value = ParseExpression();
        }

        Consume(TokenKind.Semicolon, "Expected ';' after return statement.");
        return new ReturnStatement(value, token.Location);
    }

    private Expression ParseExpression() => ParseBinaryExpression(0);

    private Expression ParseBinaryExpression(int minimumPrecedence)
    {
        var left = ParseUnaryExpression();
        while (GetPrecedence(Current.Kind) >= minimumPrecedence)
        {
            var operatorToken = Advance();
            var precedence = GetPrecedence(operatorToken.Kind);
            var right = ParseBinaryExpression(precedence + 1);
            left = new BinaryExpression(left, operatorToken.Kind, right, left.Location);
        }

        return left;
    }

    private Expression ParseUnaryExpression()
    {
        if (Check(TokenKind.Bang) || Check(TokenKind.Minus) || Check(TokenKind.Plus)
            || Check(TokenKind.Ampersand) || Check(TokenKind.Star))
        {
            var token = Advance();
            return new UnaryExpression(token.Kind, ParseUnaryExpression(), token.Location);
        }

        return ParsePrimaryExpression();
    }

    private Expression ParsePrimaryExpression()
    {
        var token = Advance();
        switch (token.Kind)
        {
            case TokenKind.Identifier:
                var name = token.Lexeme;
                var qualifiedParts = new List<string> { name };
                while (Match(TokenKind.Dot))
                    qualifiedParts.Add(Consume(TokenKind.Identifier, "Expected an identifier after '.'.").Lexeme);
                name = string.Join(".", qualifiedParts);

                if (Match(TokenKind.OpenParenthesis))
                {
                    var arguments = new List<Expression>();
                    if (!Check(TokenKind.CloseParenthesis))
                    {
                        do
                        {
                            arguments.Add(ParseExpression());
                        } while (Match(TokenKind.Comma));
                    }

                    Consume(TokenKind.CloseParenthesis, "Expected ')' after arguments.");
                    return new CallExpression(name, arguments, token.Location);
                }

                if (Check(TokenKind.OpenBrace))
                {
                    Advance();
                    var fields = new List<StructLiteralField>();
                    if (!Check(TokenKind.CloseBrace))
                    {
                        do
                        {
                            var field = Consume(TokenKind.Identifier, "Expected a struct field name.");
                            Consume(TokenKind.Colon, "Expected ':' after struct field name.");
                            fields.Add(new StructLiteralField(field.Lexeme, ParseExpression(), field.Location));
                        } while (Match(TokenKind.Comma));
                    }

                    Consume(TokenKind.CloseBrace, "Expected '}' after struct literal.");
                    Expression literal = new StructLiteralExpression(name, fields, token.Location);
                    while (Match(TokenKind.Dot))
                    {
                        var member = Consume(TokenKind.Identifier, "Expected an identifier after '.'.");
                        literal = new MemberAccessExpression(literal, member.Lexeme, literal.Location);
                    }
                    return literal;
                }

                if (qualifiedParts.Count > 1)
                {
                    Expression member = new IdentifierExpression(qualifiedParts[0], token.Location);
                    foreach (var part in qualifiedParts.Skip(1))
                        member = new MemberAccessExpression(member, part, token.Location);
                    return member;
                }

                return new IdentifierExpression(name, token.Location);
            case TokenKind.IntegerLiteral:
                return new IntegerLiteralExpression(token.Lexeme, token.Location);
            case TokenKind.FloatingPointLiteral:
                return new FloatingPointLiteralExpression(token.Lexeme, token.Location);
            case TokenKind.StringLiteral:
                return new StringLiteralExpression(token.Lexeme, token.Location);
            case TokenKind.True:
                return new BooleanLiteralExpression(true, token.Location);
            case TokenKind.False:
                return new BooleanLiteralExpression(false, token.Location);
            case TokenKind.OpenParenthesis:
                var expression = ParseExpression();
                Consume(TokenKind.CloseParenthesis, "Expected ')' after expression.");
                return expression;
            default:
                throw Error($"Expected an expression, but found {Describe(token)}.", token);
        }
    }

    private TypeSyntax ParseType(bool allowInferred)
    {
        if (allowInferred && Match(TokenKind.Var))
        {
            return new TypeSyntax(null, true, Previous.Location);
        }

        if (Match(TokenKind.String))
            return ParsePointerSuffix(new TypeSyntax(null, false, Previous.Location, true));

        if (PrimitiveTypes.TryGetValue(Current.Kind, out var primitive))
        {
            var token = Advance();
            var type = new TypeSyntax(primitive, false, token.Location);
            return ParsePointerSuffix(type);
        }

        if (Check(TokenKind.Identifier))
        {
            var token = Advance();
            return ParsePointerSuffix(new TypeSyntax(null, false, token.Location, NamedType: token.Lexeme));
        }

        throw Error(
            $"{(allowInferred ? "Expected a primitive type, string, or 'var'" : "Expected a primitive type or string")}, but found {Describe(Current)}.",
            Current);
    }

    private TypeSyntax ParsePointerSuffix(TypeSyntax type)
    {
        while (Match(TokenKind.Star))
            type = new TypeSyntax(null, false, type.Location, Pointee: type);
        return type;
    }

    private string ParseQualifiedName(string message)
    {
        var first = Consume(TokenKind.Identifier, message);
        var parts = new List<string> { first.Lexeme };
        while (Match(TokenKind.Dot))
            parts.Add(Consume(TokenKind.Identifier, "Expected an identifier after '.'.").Lexeme);
        return string.Join(".", parts);
    }

    private static int GetPrecedence(TokenKind kind) => kind switch
    {
        TokenKind.PipePipe => 1,
        TokenKind.AmpersandAmpersand => 2,
        TokenKind.EqualsEquals or TokenKind.BangEquals => 3,
        TokenKind.LessThan or TokenKind.LessThanOrEquals or TokenKind.GreaterThan
            or TokenKind.GreaterThanOrEquals => 4,
        TokenKind.Plus or TokenKind.Minus => 5,
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => 6,
        _ => -1
    };

    private bool IsType(TokenKind kind) => PrimitiveTypes.ContainsKey(kind) || kind == TokenKind.String;

    private bool IsStructDeclarationStart()
    {
        var index = _position;
        while (index < _tokens.Count && _tokens[index].Kind == TokenKind.At)
        {
            index++;
            if (index >= _tokens.Count || _tokens[index].Kind is not (TokenKind.Identifier or TokenKind.Extern))
                return false;
            index++;
            if (index < _tokens.Count && _tokens[index].Kind == TokenKind.OpenParenthesis)
            {
                var depth = 1;
                index++;
                while (index < _tokens.Count && depth > 0)
                {
                    if (_tokens[index].Kind == TokenKind.OpenParenthesis) depth++;
                    if (_tokens[index].Kind == TokenKind.CloseParenthesis) depth--;
                    index++;
                }
            }
        }
        if (index < _tokens.Count && _tokens[index].Kind is TokenKind.Public or TokenKind.Private)
            index++;
        return index < _tokens.Count && _tokens[index].Kind == TokenKind.Struct;
    }

    private Token Consume(TokenKind kind, string message)
    {
        if (Check(kind))
        {
            return Advance();
        }

        throw Error($"{message} Found {Describe(Current)} instead.", Current);
    }

    private bool Match(TokenKind kind)
    {
        if (!Check(kind))
        {
            return false;
        }

        Advance();
        return true;
    }

    private bool Check(TokenKind kind) => Current.Kind == kind;

    private Token Advance()
    {
        var token = Current;
        if (_position < _tokens.Count - 1)
        {
            _position++;
        }

        return token;
    }

    private Token Peek(int offset) =>
        _position + offset < _tokens.Count ? _tokens[_position + offset] : _tokens[^1];

    private Token Current => _tokens[_position];

    private Token Previous => _tokens[Math.Max(0, _position - 1)];

    private static ParseException Error(string message, Token token) => new(message, token);

    private static string Describe(Token token) =>
        token.Kind == TokenKind.EndOfFile
            ? "the end of the file"
            : $"'{token.Lexeme}' ({token.Kind})";

    private void Synchronize()
    {
        while (!Check(TokenKind.EndOfFile))
        {
            if (Match(TokenKind.CloseBrace))
            {
                return;
            }

            if (IsType(Current.Kind) || Check(TokenKind.Void))
            {
                return;
            }

            Advance();
        }
    }
}
