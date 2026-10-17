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
            [TokenKind.USize] = PrimitiveType.USize,
            [TokenKind.ISize] = PrimitiveType.ISize,
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
            var enums = new List<EnumDeclaration>();
            var unions = new List<UnionDeclaration>();
            var classes = new List<ClassDeclaration>();
            var interfaces = new List<InterfaceDeclaration>();
            var functions = new List<FunctionDeclaration>();
            while (!Check(TokenKind.EndOfFile))
            {
                if (IsEnumDeclarationStart())
                    enums.Add(ParseEnum());
                else if (IsUnionDeclarationStart())
                    unions.Add(ParseUnion());
                else if (IsStructDeclarationStart())
                    structs.Add(ParseStruct());
                else if (IsClassDeclarationStart())
                    classes.Add(ParseClass());
                else if (IsInterfaceDeclarationStart())
                    interfaces.Add(ParseInterface());
                else
                    functions.Add(ParseFunction());
            }

            _logger.Information("Parsed {FunctionCount} function(s).", functions.Count);
            return new CompilationUnit(module.Name, module.Visibility, imports, functions, location)
            {
                Structs = structs, Enums = enums, Unions = unions, Classes = classes, Interfaces = interfaces
            };
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
        var enums = new List<EnumDeclaration>();
        var unions = new List<UnionDeclaration>();
        var classes = new List<ClassDeclaration>();
        var interfaces = new List<InterfaceDeclaration>();
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
                if (IsEnumDeclarationStart())
                    enums.Add(ParseEnum());
                else if (IsUnionDeclarationStart())
                    unions.Add(ParseUnion());
                else if (IsStructDeclarationStart())
                    structs.Add(ParseStruct());
                else if (IsClassDeclarationStart())
                    classes.Add(ParseClass());
                else if (IsInterfaceDeclarationStart())
                    interfaces.Add(ParseInterface());
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
            Structs = structs,
            Enums = enums
            ,Unions = unions
            ,Classes = classes
            ,Interfaces = interfaces
        }, diagnostics.Diagnostics);
    }

    private ClassDeclaration ParseClass()
    {
        var location = Current.Location;
        var annotations = ParseAnnotations();
        var visibility = ParseVisibility();
        Consume(TokenKind.Class, "Expected 'class'.");
        var name = Consume(TokenKind.Identifier, "Expected a class name.").Lexeme;
        var typeParameters = ParseTypeParameters();
        string? baseType = null;
        var interfaces = new List<string>();
        if (Match(TokenKind.Colon))
        {
            baseType = Consume(TokenKind.Identifier, "Expected a base type.").Lexeme;
            while (Match(TokenKind.Comma))
                interfaces.Add(Consume(TokenKind.Identifier, "Expected an interface name.").Lexeme);
        }

        Consume(TokenKind.OpenBrace, "Expected '{' after class declaration.");
        var fields = new List<ClassFieldDeclaration>();
        var methods = new List<FunctionDeclaration>();
        var constructors = new List<ConstructorDeclaration>();
        DestructorDeclaration? destructor = null;
        while (!Check(TokenKind.CloseBrace) && !Check(TokenKind.EndOfFile))
        {
            var memberVisibility = ParseVisibility();
            if (Check(TokenKind.Tilde))
            {
                var destructorLocation = Advance().Location;
                var destructorName = Consume(TokenKind.Identifier, "Expected a destructor name.").Lexeme;
                Consume(TokenKind.OpenParenthesis, "Expected '(' after destructor name.");
                Consume(TokenKind.CloseParenthesis, "Expected ')' after destructor name.");
                var body = Check(TokenKind.Semicolon)
                    ? new BlockStatement([], destructorLocation)
                    : ParseBlock();
                Match(TokenKind.Semicolon);
                destructor = new DestructorDeclaration(destructorName, body, memberVisibility, destructorLocation);
                continue;
            }

            if (Check(TokenKind.Identifier) && Current.Lexeme == name && Peek(1).Kind == TokenKind.OpenParenthesis)
            {
                var constructorLocation = Current.Location;
                Advance();
                var parameters = ParseParameters();
                constructors.Add(new ConstructorDeclaration(name, parameters, ParseBlock(), memberVisibility,
                    constructorLocation));
                continue;
            }

            var memberLocation = Current.Location;
            var type = ParseType(allowInferred: false);
            var memberName = Consume(TokenKind.Identifier, "Expected a class member name.").Lexeme;
            if (Check(TokenKind.OpenParenthesis))
            {
                var parameters = ParseParameters();
                methods.Add(new FunctionDeclaration(type, memberName, parameters, ParseBlock(), memberVisibility,
                    memberLocation));
            }
            else
            {
                Consume(TokenKind.Semicolon, "Expected ';' after class field.");
                fields.Add(new ClassFieldDeclaration(type, memberName, memberVisibility, memberLocation));
            }
        }
        Consume(TokenKind.CloseBrace, "Expected '}' after class declaration.");
        Match(TokenKind.Semicolon);
        return new ClassDeclaration(name, baseType, interfaces, fields, methods, constructors, destructor, visibility,
            location, annotations, typeParameters);
    }

    private InterfaceDeclaration ParseInterface()
    {
        var location = Current.Location;
        var annotations = ParseAnnotations();
        var visibility = ParseVisibility();
        Consume(TokenKind.Interface, "Expected 'interface'.");
        var name = Consume(TokenKind.Identifier, "Expected an interface name.").Lexeme;
        var typeParameters = ParseTypeParameters();
        Consume(TokenKind.OpenBrace, "Expected '{' after interface name.");
        var methods = new List<FunctionDeclaration>();
        while (!Check(TokenKind.CloseBrace) && !Check(TokenKind.EndOfFile))
        {
            var memberVisibility = ParseVisibility();
            var methodLocation = Current.Location;
            var returnType = ParseType(allowInferred: false);
            var methodName = Consume(TokenKind.Identifier, "Expected an interface method name.").Lexeme;
            var parameters = ParseParameters();
            Consume(TokenKind.Semicolon, "Expected ';' after interface method.");
            methods.Add(new FunctionDeclaration(returnType, methodName, parameters,
                new BlockStatement([], methodLocation), memberVisibility, methodLocation, IsExternalDeclaration: true));
        }
        Consume(TokenKind.CloseBrace, "Expected '}' after interface declaration.");
        Match(TokenKind.Semicolon);
        return new InterfaceDeclaration(name, methods, visibility, location, annotations, typeParameters);
    }

    private IReadOnlyList<ParameterDeclaration> ParseParameters()
    {
        Consume(TokenKind.OpenParenthesis, "Expected '(' before parameters.");
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
        return parameters;
    }

    private Visibility ParseVisibility()
    {
        if (Match(TokenKind.Public))
            return Visibility.Public;
        Match(TokenKind.Private);
        return Visibility.Private;
    }

    private UnionDeclaration ParseUnion()
    {
        var location = Current.Location;
        var annotations = ParseAnnotations();
        var visibility = Match(TokenKind.Public) ? Visibility.Public : Visibility.Private;
        Match(TokenKind.Private);
        Consume(TokenKind.Union, "Expected 'union'.");
        var name = Consume(TokenKind.Identifier, "Expected a union name.").Lexeme;
        var typeParameters = ParseTypeParameters();
        Consume(TokenKind.OpenBrace, "Expected '{' after union name.");
        var cases = new List<UnionCaseDeclaration>();
        while (!Check(TokenKind.CloseBrace) && !Check(TokenKind.EndOfFile))
        {
            var caseToken = Consume(TokenKind.Identifier, "Expected a union case name.");
            TypeSyntax? payload = null;
            if (Match(TokenKind.OpenParenthesis))
            {
                payload = ParseType(allowInferred: false);
                Consume(TokenKind.CloseParenthesis, "Expected ')' after union case payload type.");
            }
            Consume(TokenKind.Semicolon, "Expected ';' after union case.");
            cases.Add(new UnionCaseDeclaration(caseToken.Lexeme, payload, caseToken.Location));
        }
        Consume(TokenKind.CloseBrace, "Expected '}' after union declaration.");
        Match(TokenKind.Semicolon);
        return new UnionDeclaration(name, cases, visibility, location, annotations, typeParameters);
    }

    private EnumDeclaration ParseEnum()
    {
        var location = Current.Location;
        var annotations = ParseAnnotations();
        var visibility = Visibility.Private;
        if (Match(TokenKind.Public))
            visibility = Visibility.Public;
        else
            Match(TokenKind.Private);
        Consume(TokenKind.Enum, "Expected 'enum'.");
        var name = Consume(TokenKind.Identifier, "Expected an enum name.").Lexeme;
        var underlying = PrimitiveType.I32;
        if (Match(TokenKind.Colon))
        {
            if (!PrimitiveTypes.TryGetValue(Current.Kind, out underlying)
                || underlying is PrimitiveType.F32 or PrimitiveType.F64 or PrimitiveType.Bool or PrimitiveType.Void)
                throw Error("Enum underlying type must be a fixed-width integer type.", Current);
            Advance();
        }
        Consume(TokenKind.OpenBrace, "Expected '{' after enum declaration.");
        var members = new List<EnumMemberDeclaration>();
        while (!Check(TokenKind.CloseBrace) && !Check(TokenKind.EndOfFile))
        {
            var member = Consume(TokenKind.Identifier, "Expected an enum member name.");
            int? value = null;
            if (Match(TokenKind.Equals))
            {
                var number = Consume(TokenKind.IntegerLiteral, "Expected an integer enum member value.");
                if (!int.TryParse(number.Lexeme, out var parsed))
                    throw Error("Enum member values must fit in a signed 32-bit integer.", number);
                value = parsed;
            }
            members.Add(new EnumMemberDeclaration(member.Lexeme, value, member.Location));
            if (!Match(TokenKind.Comma))
                break;
        }
        Consume(TokenKind.CloseBrace, "Expected '}' after enum declaration.");
        Match(TokenKind.Semicolon);
        return new EnumDeclaration(name, underlying, members, visibility, location, annotations);
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
        var typeParameters = ParseTypeParameters();
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
        return new StructDeclaration(name, fields, visibility, location, annotations, typeParameters);
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
        var nameToken = Consume(TokenKind.Identifier, "Expected a function name.");
        var name = nameToken.Lexeme;
        var typeParameters = ParseTypeParameters();
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
        return new FunctionDeclaration(returnType, name, parameters, body, visibility, location, annotations, isExternal,
            typeParameters);
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

        if (Match(TokenKind.If))
            return ParseIf(Previous.Location);
        if (Match(TokenKind.While))
            return ParseWhile(Previous.Location);
        if (Match(TokenKind.For))
            return ParseFor(Previous.Location);
        if (Match(TokenKind.Break))
        {
            var location = Previous.Location;
            Consume(TokenKind.Semicolon, "Expected ';' after break.");
            return new BreakStatement(location);
        }
        if (Match(TokenKind.Continue))
        {
            var location = Previous.Location;
            Consume(TokenKind.Semicolon, "Expected ';' after continue.");
            return new ContinueStatement(location);
        }
        if (Match(TokenKind.Switch))
            return ParseSwitch(Previous.Location);

        if (Check(TokenKind.Const) || Check(TokenKind.Var) || IsType(Current.Kind)
            || (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Identifier)
            || IsGenericVariableDeclarationStart())
        {
            return ParseVariableDeclaration();
        }

        var expressionLocation = Current.Location;
        var expression = ParseExpression();
        if (TryConsumeAssignmentOperator(out var assignmentOperator))
        {
            var value = ParseExpression();
            Consume(TokenKind.Semicolon, "Expected ';' after assignment.");
            return new AssignmentStatement(expression,
                assignmentOperator == TokenKind.Equals
                    ? value
                    : new BinaryExpression(expression, CompoundOperator(assignmentOperator), value, expressionLocation),
                expressionLocation);
        }
        Consume(TokenKind.Semicolon, "Expected ';' after expression.");
        return new ExpressionStatement(expression, expressionLocation);
    }

    private SwitchStatement ParseSwitch(SourceLocation location)
    {
        Consume(TokenKind.OpenParenthesis, "Expected '(' after 'switch'.");
        var value = ParseExpression();
        Consume(TokenKind.CloseParenthesis, "Expected ')' after switch value.");
        Consume(TokenKind.OpenBrace, "Expected '{' after switch value.");
        var cases = new List<SwitchCase>();
        while (Match(TokenKind.Case))
        {
            var caseToken = Previous;
            if (Match(TokenKind.Is))
            {
                var patternType = ParseType(allowInferred: false);
                var binding = Match(TokenKind.Identifier) ? Previous.Lexeme : null;
                Consume(TokenKind.Colon, "Expected ':' after switch type pattern.");
                cases.Add(new SwitchCase(null, null, false, binding, ParseStatement(), caseToken.Location, patternType));
                continue;
            }
            if (Check(TokenKind.Identifier)
                && (Peek(1).Kind == TokenKind.Colon
                    || (Peek(1).Kind == TokenKind.OpenParenthesis && Peek(4).Kind == TokenKind.Colon)))
            {
                var name = Advance().Lexeme;
                string? binding = null;
                if (Match(TokenKind.OpenParenthesis))
                {
                    binding = Consume(TokenKind.Identifier, "Expected a case binding name.").Lexeme;
                    Consume(TokenKind.CloseParenthesis, "Expected ')' after case binding.");
                }
                Consume(TokenKind.Colon, "Expected ':' after switch case.");
                cases.Add(new SwitchCase(name, null, false, binding, ParseStatement(), caseToken.Location));
                continue;
            }

            var caseValue = ParseExpression();
            Consume(TokenKind.Colon, "Expected ':' after switch case.");
            cases.Add(new SwitchCase(null, caseValue, false, null, ParseStatement(), caseToken.Location));
        }
        if (Match(TokenKind.Default))
        {
            var defaultToken = Previous;
            Consume(TokenKind.Colon, "Expected ':' after 'default'.");
            cases.Add(new SwitchCase(null, null, true, null, ParseStatement(), defaultToken.Location));
        }
        Consume(TokenKind.CloseBrace, "Expected '}' after switch.");
        return new SwitchStatement(value, cases, location);
    }

    private IfStatement ParseIf(SourceLocation location)
    {
        Consume(TokenKind.OpenParenthesis, "Expected '(' after 'if'.");
        var condition = ParseExpression();
        Consume(TokenKind.CloseParenthesis, "Expected ')' after if condition.");
        var then = ParseStatement();
        Statement? otherwise = null;
        if (Match(TokenKind.Else))
            otherwise = ParseStatement();
        return new IfStatement(condition, then, otherwise, location);
    }

    private WhileStatement ParseWhile(SourceLocation location)
    {
        Consume(TokenKind.OpenParenthesis, "Expected '(' after 'while'.");
        var condition = ParseExpression();
        Consume(TokenKind.CloseParenthesis, "Expected ')' after while condition.");
        return new WhileStatement(condition, ParseStatement(), location);
    }

    private ForStatement ParseFor(SourceLocation location)
    {
        Consume(TokenKind.OpenParenthesis, "Expected '(' after 'for'.");
        Statement? initializer = null;
        if (!Check(TokenKind.Semicolon))
            initializer = ParseForClause(isInitializer: true);
        else
            Advance();
        Expression? condition = null;
        if (!Check(TokenKind.Semicolon))
            condition = ParseExpression();
        Consume(TokenKind.Semicolon, "Expected ';' after for condition.");
        Statement? increment = null;
        if (!Check(TokenKind.CloseParenthesis))
            increment = ParseForClause(isInitializer: false);
        Consume(TokenKind.CloseParenthesis, "Expected ')' after for clauses.");
        return new ForStatement(initializer, condition, increment, ParseStatement(), location);
    }

    private Statement ParseForClause(bool isInitializer)
    {
        if (Check(TokenKind.Const) || Check(TokenKind.Var) || IsType(Current.Kind)
            || (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Identifier))
            return ParseVariableDeclaration();

        var expressionLocation = Current.Location;
        var expression = ParseExpression();
        if (!isInitializer && expression is UnaryExpression increment
            && increment.Operator is TokenKind.PlusPlus or TokenKind.MinusMinus
            && increment.IsPostfix)
        {
            var operatorKind = increment.Operator == TokenKind.PlusPlus ? TokenKind.Plus : TokenKind.Minus;
            return new AssignmentStatement(
                increment.Operand,
                new BinaryExpression(increment.Operand, operatorKind,
                    new IntegerLiteralExpression("1", increment.Location), increment.Location),
                expressionLocation);
        }
        if (TryConsumeAssignmentOperator(out var assignmentOperator))
        {
            var value = ParseExpression();
            if (isInitializer)
                Consume(TokenKind.Semicolon, "Expected ';' after for initializer.");
            return new AssignmentStatement(expression,
                assignmentOperator == TokenKind.Equals
                    ? value
                    : new BinaryExpression(expression, CompoundOperator(assignmentOperator), value, expressionLocation),
                expressionLocation);
        }
        if (isInitializer)
            Consume(TokenKind.Semicolon, "Expected ';' after for initializer.");
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

    private Expression ParseExpression() => ParseConditionalExpression();

    private Expression ParseConditionalExpression()
    {
        var condition = ParseBinaryExpression(0);
        if (!Match(TokenKind.QuestionMark))
            return condition;

        var whenTrue = ParseExpression();
        Consume(TokenKind.Colon, "Expected ':' after the true branch of a conditional expression.");
        var whenFalse = ParseConditionalExpression();
        return new ConditionalExpression(condition, whenTrue, whenFalse, condition.Location);
    }

    private Expression ParseBinaryExpression(int minimumPrecedence)
    {
        var left = ParseUnaryExpression();
        while (true)
        {
            if (Match(TokenKind.Is))
            {
                var targetType = ParseType(allowInferred: false);
                var bindingName = Match(TokenKind.Identifier) ? Previous.Lexeme : null;
                left = new IsExpression(left, targetType, bindingName, left.Location);
                continue;
            }
            var isLeftShift = Current.Kind == TokenKind.LessThan && Peek(1).Kind == TokenKind.LessThan;
            var isRightShift = Current.Kind == TokenKind.GreaterThan && Peek(1).Kind == TokenKind.GreaterThan;
            var precedence = isLeftShift || isRightShift ? 8 : GetPrecedence(Current.Kind);
            if (precedence < minimumPrecedence)
                break;
            var operatorToken = Advance();
            var operatorKind = isLeftShift ? TokenKind.LessThanLessThan
                : isRightShift ? TokenKind.GreaterThanGreaterThan
                : operatorToken.Kind;
            if (isLeftShift || isRightShift)
                Advance();
            var right = ParseBinaryExpression(precedence + 1);
            left = new BinaryExpression(left, operatorKind, right, left.Location);
        }

        return left;
    }

    private Expression ParseUnaryExpression()
    {
        if (Check(TokenKind.Bang) || Check(TokenKind.Minus) || Check(TokenKind.Plus)
            || Check(TokenKind.Ampersand) || Check(TokenKind.Star)
            || Check(TokenKind.PlusPlus) || Check(TokenKind.MinusMinus))
        {
            var token = Advance();
            return new UnaryExpression(token.Kind, ParseUnaryExpression(), token.Location);
        }

        return ParsePostfixExpression();
    }

    private Expression ParsePostfixExpression()
    {
        var expression = ParsePrimaryExpression();
        while (true)
        {
            if (Match(TokenKind.OpenBracket))
            {
                var index = ParseExpression();
                Consume(TokenKind.CloseBracket, "Expected ']' after array index.");
                expression = new IndexExpression(expression, index, expression.Location);
            }
            else if (Match(TokenKind.Dot))
            {
                var member = Consume(TokenKind.Identifier, "Expected an identifier after '.'.");
                expression = new MemberAccessExpression(expression, member.Lexeme, expression.Location);
            }
            else if (Match(TokenKind.PlusPlus))
            {
                expression = new UnaryExpression(TokenKind.PlusPlus, expression, expression.Location, true);
            }
            else if (Match(TokenKind.MinusMinus))
            {
                expression = new UnaryExpression(TokenKind.MinusMinus, expression, expression.Location, true);
            }
            else break;
        }
        return expression;
    }

    private Expression ParsePrimaryExpression()
    {
        var token = Advance();
        switch (token.Kind)
        {
            case TokenKind.New:
            {
                var type = ParseType(allowInferred: false);
                Consume(TokenKind.OpenParenthesis, "Expected '(' after type in new expression.");
                var arguments = new List<Expression>();
                if (!Check(TokenKind.CloseParenthesis))
                {
                    do
                    {
                        arguments.Add(ParseExpression());
                    } while (Match(TokenKind.Comma));
                }
                Consume(TokenKind.CloseParenthesis, "Expected ')' after constructor arguments.");
                return new NewExpression(type, arguments, token.Location);
            }
            case TokenKind.OpenBrace:
            {
                var elements = new List<Expression>();
                if (!Check(TokenKind.CloseBrace))
                {
                    do { elements.Add(ParseExpression()); } while (Match(TokenKind.Comma));
                }
                Consume(TokenKind.CloseBrace, "Expected '}' after array literal.");
                return new ArrayLiteralExpression(elements, token.Location);
            }
            case TokenKind.Identifier:
                var name = token.Lexeme;
                var qualifiedParts = new List<string> { name };
                while (Match(TokenKind.Dot))
                    qualifiedParts.Add(Consume(TokenKind.Identifier, "Expected an identifier after '.'.").Lexeme);
                name = string.Join(".", qualifiedParts);

                var typeArguments = ParseTypeArguments();
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
                    return new CallExpression(name, arguments, token.Location, typeArguments);
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
            case TokenKind.This:
                return new ThisExpression(token.Location);
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
            case TokenKind.Cast:
            {
                Consume(TokenKind.LessThan, "Expected '<' after 'cast'.");
                var type = ParseType(allowInferred: false);
                Consume(TokenKind.GreaterThan, "Expected '>' after cast type.");
                Consume(TokenKind.OpenParenthesis, "Expected '(' after cast type.");
                var operand = ParseExpression();
                Consume(TokenKind.CloseParenthesis, "Expected ')' after cast operand.");
                return new CastExpression(type, operand, token.Location);
            }
            case TokenKind.SizeOf:
            {
                if (Match(TokenKind.LessThan))
                {
                    var type = ParseType(allowInferred: false);
                    Consume(TokenKind.GreaterThan, "Expected '>' after sizeof type.");
                    Consume(TokenKind.OpenParenthesis, "Expected '(' after sizeof type.");
                    Consume(TokenKind.CloseParenthesis, "Expected ')' after sizeof type.");
                    return new SizeOfExpression(type, null, token.Location);
                }

                Consume(TokenKind.OpenParenthesis, "Expected '(' after 'sizeof'.");
                var operand = ParseExpression();
                Consume(TokenKind.CloseParenthesis, "Expected ')' after sizeof operand.");
                return new SizeOfExpression(null, operand, token.Location);
            }
            case TokenKind.OpenParenthesis:
                if ((Check(TokenKind.Identifier) || IsType(Current.Kind)) && Peek(1).Kind == TokenKind.CloseParenthesis)
                {
                    var type = ParseType(allowInferred: false);
                    Consume(TokenKind.CloseParenthesis, "Expected ')' after cast type.");
                    return new CastExpression(type, ParseUnaryExpression(), token.Location);
                }
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
            var typeArguments = ParseNamedTypeArguments();
            return ParsePointerSuffix(new TypeSyntax(null, false, token.Location, NamedType: token.Lexeme,
                TypeArguments: typeArguments));
        }

        throw Error(
            $"{(allowInferred ? "Expected a primitive type, string, or 'var'" : "Expected a primitive type or string")}, but found {Describe(Current)}.",
            Current);
    }

    private IReadOnlyList<TypeParameterSyntax> ParseTypeParameters()
    {
        var parameters = new List<TypeParameterSyntax>();
        if (!Match(TokenKind.LessThan))
            return parameters;

        do
        {
            var token = Consume(TokenKind.Identifier, "Expected a generic type parameter.");
            parameters.Add(new TypeParameterSyntax(token.Lexeme, token.Location));
        } while (Match(TokenKind.Comma));

        Consume(TokenKind.GreaterThan, "Expected '>' after generic type parameters.");
        return parameters;
    }

    private bool IsGenericVariableDeclarationStart()
    {
        if (!Check(TokenKind.Identifier) || Peek(1).Kind != TokenKind.LessThan)
            return false;

        var index = _position + 1;
        var depth = 0;
        while (index < _tokens.Count)
        {
            if (_tokens[index].Kind == TokenKind.LessThan)
                depth++;
            else if (_tokens[index].Kind == TokenKind.GreaterThan && --depth == 0)
                return index + 1 < _tokens.Count && _tokens[index + 1].Kind == TokenKind.Identifier;
            index++;
        }

        return false;
    }

    private IReadOnlyList<TypeSyntax>? ParseTypeArguments()
    {
        if (!Check(TokenKind.LessThan) || !LooksLikeTypeArgumentCall())
            return null;

        Advance();
        var arguments = new List<TypeSyntax>();
        do
        {
            arguments.Add(ParseType(allowInferred: false));
        } while (Match(TokenKind.Comma));

        Consume(TokenKind.GreaterThan, "Expected '>' after generic type arguments.");
        return arguments;
    }

    private IReadOnlyList<TypeSyntax>? ParseNamedTypeArguments()
    {
        if (!Match(TokenKind.LessThan))
            return null;

        var arguments = new List<TypeSyntax>();
        do
        {
            arguments.Add(ParseType(allowInferred: false));
        } while (Match(TokenKind.Comma));

        Consume(TokenKind.GreaterThan, "Expected '>' after generic type arguments.");
        return arguments;
    }

    private bool LooksLikeTypeArgumentCall()
    {
        var index = _position;
        var depth = 0;
        while (index < _tokens.Count)
        {
            switch (_tokens[index].Kind)
            {
                case TokenKind.LessThan:
                    depth++;
                    break;
                case TokenKind.GreaterThan:
                    depth--;
                    if (depth == 0)
                        return index + 1 < _tokens.Count && _tokens[index + 1].Kind == TokenKind.OpenParenthesis;
                    break;
                case TokenKind.EndOfFile:
                    return false;
            }

            index++;
        }

        return false;
    }

    private TypeSyntax ParsePointerSuffix(TypeSyntax type)
    {
        while (Match(TokenKind.Star))
            type = new TypeSyntax(null, false, type.Location, Pointee: type);
        while (Match(TokenKind.OpenBracket))
        {
            var size = Consume(TokenKind.IntegerLiteral, "Expected a positive decimal array length.");
            if (!int.TryParse(size.Lexeme, out var length) || length <= 0)
                throw Error("Array length must be a positive decimal integer.", size);
            Consume(TokenKind.CloseBracket, "Expected ']' after array length.");
            type = new TypeSyntax(null, false, type.Location, ArrayLength: length, ElementType: type);
        }
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
        TokenKind.Pipe => 5,
        TokenKind.Caret => 6,
        TokenKind.Ampersand => 7,
        TokenKind.LessThanLessThan or TokenKind.GreaterThanGreaterThan => 8,
        TokenKind.Plus or TokenKind.Minus => 9,
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => 10,
        _ => -1
    };

    private bool TryConsumeAssignmentOperator(out TokenKind operatorKind)
    {
        operatorKind = Current.Kind;
        if (operatorKind is TokenKind.Equals or TokenKind.PlusEquals or TokenKind.MinusEquals
            or TokenKind.StarEquals or TokenKind.SlashEquals or TokenKind.PercentEquals
            or TokenKind.AmpersandEquals or TokenKind.PipeEquals or TokenKind.CaretEquals
            or TokenKind.LessThanLessThanEquals or TokenKind.GreaterThanGreaterThanEquals)
        {
            Advance();
            return true;
        }

        return false;
    }

    private static TokenKind CompoundOperator(TokenKind operatorKind) => operatorKind switch
    {
        TokenKind.PlusEquals => TokenKind.Plus,
        TokenKind.MinusEquals => TokenKind.Minus,
        TokenKind.StarEquals => TokenKind.Star,
        TokenKind.SlashEquals => TokenKind.Slash,
        TokenKind.PercentEquals => TokenKind.Percent,
        TokenKind.AmpersandEquals => TokenKind.Ampersand,
        TokenKind.PipeEquals => TokenKind.Pipe,
        TokenKind.CaretEquals => TokenKind.Caret,
        TokenKind.LessThanLessThanEquals => TokenKind.LessThanLessThan,
        TokenKind.GreaterThanGreaterThanEquals => TokenKind.GreaterThanGreaterThan,
        _ => throw new InvalidOperationException($"'{operatorKind}' is not a compound assignment operator.")
    };

    private bool IsType(TokenKind kind) => PrimitiveTypes.ContainsKey(kind) || kind == TokenKind.String;

    private bool IsEnumDeclarationStart()
    {
        var index = _position;
        while (index < _tokens.Count && _tokens[index].Kind == TokenKind.At)
        {
            index += 2;
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
        return index < _tokens.Count && _tokens[index].Kind == TokenKind.Enum;
    }

    private bool IsUnionDeclarationStart()
    {
        var index = _position;
        while (index < _tokens.Count && _tokens[index].Kind == TokenKind.At) index += 2;
        if (index < _tokens.Count && _tokens[index].Kind is TokenKind.Public or TokenKind.Private) index++;
        return index < _tokens.Count && _tokens[index].Kind == TokenKind.Union;
    }

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

    private bool IsClassDeclarationStart() => IsDeclarationStart(TokenKind.Class);

    private bool IsInterfaceDeclarationStart() => IsDeclarationStart(TokenKind.Interface);

    private bool IsDeclarationStart(TokenKind declaration)
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
        return index < _tokens.Count && _tokens[index].Kind == declaration;
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
