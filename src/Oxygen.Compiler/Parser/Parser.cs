using Oxygen.Compiler.Ast;
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
            var functions = new List<FunctionDeclaration>();
            while (!Check(TokenKind.EndOfFile))
            {
                functions.Add(ParseFunction());
            }

            _logger.Information("Parsed {FunctionCount} function(s).", functions.Count);
            return new CompilationUnit(functions, location);
        }
        catch (ParseException exception)
        {
            _logger.Error(exception, "Syntax error at {Line}:{Column}", exception.Token.Location.Line,
                exception.Token.Location.Column);
            throw;
        }
    }

    private FunctionDeclaration ParseFunction()
    {
        var location = Current.Location;
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
        var body = ParseBlock();
        _logger.Debug("Parsed function {FunctionName}.", name);
        return new FunctionDeclaration(returnType, name, parameters, body, location);
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

        if (Check(TokenKind.Const) || Check(TokenKind.Var) || IsType(Current.Kind))
        {
            return ParseVariableDeclaration();
        }

        if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Equals)
        {
            var location = Current.Location;
            var name = Advance().Lexeme;
            Advance();
            var value = ParseExpression();
            Consume(TokenKind.Semicolon, "Expected ';' after assignment.");
            return new AssignmentStatement(name, value, location);
        }

        var expressionLocation = Current.Location;
        var expression = ParseExpression();
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
        if (Check(TokenKind.Bang) || Check(TokenKind.Minus) || Check(TokenKind.Plus))
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
            case TokenKind.Identifier when Match(TokenKind.OpenParenthesis):
                var arguments = new List<Expression>();
                if (!Check(TokenKind.CloseParenthesis))
                {
                    do
                    {
                        arguments.Add(ParseExpression());
                    } while (Match(TokenKind.Comma));
                }

                Consume(TokenKind.CloseParenthesis, "Expected ')' after arguments.");
                return new CallExpression(token.Lexeme, arguments, token.Location);
            case TokenKind.Identifier:
                return new IdentifierExpression(token.Lexeme, token.Location);
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
                throw Error("Expected an expression.", token);
        }
    }

    private TypeSyntax ParseType(bool allowInferred)
    {
        if (allowInferred && Match(TokenKind.Var))
        {
            return new TypeSyntax(null, true, Previous.Location);
        }

        if (PrimitiveTypes.TryGetValue(Current.Kind, out var primitive))
        {
            var token = Advance();
            return new TypeSyntax(primitive, false, token.Location);
        }

        throw Error(allowInferred
            ? "Expected a primitive type or 'var'."
            : "Expected a primitive type.", Current);
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

    private bool IsType(TokenKind kind) => PrimitiveTypes.ContainsKey(kind);

    private Token Consume(TokenKind kind, string message)
    {
        if (Check(kind))
        {
            return Advance();
        }

        throw Error(message, Current);
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
}
