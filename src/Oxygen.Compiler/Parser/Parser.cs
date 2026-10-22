using System.Globalization;
using System.Numerics;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Diagnostics;
using Oxygen.Compiler.Lexer;
using Serilog;

namespace Oxygen.Compiler.Parsing;

public sealed partial class Parser
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

    // Set by ParseWithDiagnostics, which records errors and keeps parsing. Parse() leaves them null and throws instead.
    private DiagnosticBag? _diagnostics;
    private string? _sourceName;

    public Parser(IReadOnlyList<Token> tokens, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Count == 0 || tokens[^1].Kind != TokenKind.EndOfFile)
        {
            throw new ArgumentException("The token stream must end with an EOF token.", nameof(tokens));
        }

        _tokens = tokens;
        _logger = logger ?? new LoggerConfiguration()
            .MinimumLevel.Warning()
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
            var globals = new List<GlobalVariableDeclaration>();
            var aliases = new List<TypeAliasDeclaration>();
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
                else if (IsTypeAliasStart())
                    aliases.Add(ParseTypeAlias());
                else if (IsGlobalVariableStart())
                    globals.Add(ParseGlobalVariable());
                else
                    functions.Add(ParseFunction());
            }

            _logger.Information("Parsed {FunctionCount} function(s).", functions.Count);
            return new CompilationUnit(module.Name, module.Visibility, imports, functions, location)
            {
                Structs = structs,
                Enums = enums,
                Unions = unions,
                Classes = classes,
                Interfaces = interfaces,
                Globals = globals,
                Aliases = aliases
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
        _diagnostics = diagnostics;
        _sourceName = sourceName;
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
            Report(exception);
            Synchronize();
        }

        var functions = new List<FunctionDeclaration>();
        var globals = new List<GlobalVariableDeclaration>();
        var aliases = new List<TypeAliasDeclaration>();
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
                else if (IsTypeAliasStart())
                    aliases.Add(ParseTypeAlias());
                else if (IsGlobalVariableStart())
                    globals.Add(ParseGlobalVariable());
                else
                    functions.Add(ParseFunction());
            }
            catch (ParseException exception)
            {
                Report(exception);
                Synchronize();
            }
        }

        return new ParseResult(new CompilationUnit(module.Name, module.Visibility, imports, functions, location)
        {
            Structs = structs,
            Enums = enums
            ,
            Unions = unions
            ,
            Classes = classes
            ,
            Interfaces = interfaces,
            Globals = globals,
            Aliases = aliases
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
        var baseTypeSyntax = (TypeSyntax?)null;
        var interfaceTypeSyntaxes = new List<TypeSyntax>();
        if (Match(TokenKind.Colon))
        {
            baseTypeSyntax = ParseNamedTypeReferenceSyntax("Expected a base type.");
            baseType = baseTypeSyntax.NamedType;
            while (Match(TokenKind.Comma))
            {
                var interfaceType = ParseNamedTypeReferenceSyntax("Expected an interface name.");
                interfaces.Add(interfaceType.NamedType!);
                interfaceTypeSyntaxes.Add(interfaceType);
            }
        }

        Consume(TokenKind.OpenBrace, "Expected '{' after class declaration.");
        var fields = new List<ClassFieldDeclaration>();
        var properties = new List<PropertyDeclaration>();
        var methods = new List<FunctionDeclaration>();
        var constructors = new List<ConstructorDeclaration>();
        DestructorDeclaration? destructor = null;
        while (!Check(TokenKind.CloseBrace) && !Check(TokenKind.EndOfFile))
        {
            var memberVisibility = Visibility.Public;
            var hasVisibility = false;
            var isVirtual = false;
            var isOverride = false;
            var isStatic = false;
            while (Current.Kind is TokenKind.Public or TokenKind.Private or TokenKind.Protected or TokenKind.Internal
                   or TokenKind.Virtual or TokenKind.Override or TokenKind.Static)
            {
                if (Current.Kind is TokenKind.Public or TokenKind.Private or TokenKind.Protected or TokenKind.Internal)
                {
                    if (hasVisibility)
                        throw Error("A class member may specify visibility only once.", Current);
                    memberVisibility = ParseVisibility();
                    hasVisibility = true;
                }
                else if (Match(TokenKind.Virtual))
                    isVirtual = true;
                else if (Match(TokenKind.Override))
                    isOverride = true;
                else if (Match(TokenKind.Static))
                    isStatic = true;
            }
            if (isVirtual && isOverride)
                throw Error("A class method cannot be both 'virtual' and 'override'.", Current);
            if (isStatic && (isVirtual || isOverride))
                throw Error("A static member cannot be 'virtual' or 'override'.", Current);
            if (Check(TokenKind.Tilde))
            {
                if (isStatic)
                    throw Error("A destructor cannot be static.", Current);
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
                if (isStatic)
                    throw Error("A constructor cannot be static.", Current);
                var constructorLocation = Current.Location;
                Advance();
                var parameters = ParseParameters();
                var initializer = ParseConstructorInitializer();
                constructors.Add(new ConstructorDeclaration(name, parameters, ParseBlock(), memberVisibility,
                    constructorLocation) { Initializer = initializer });
                continue;
            }

            var memberLocation = Current.Location;
            var isConst = Match(TokenKind.Const);
            // A function pointer field declares its name inside its form. A method or property returns a type first, so it cannot start here.
            if (IsFunctionPointerDeclarationStart())
            {
                var (fieldType, fieldName) = ParseDeclaredName(allowInferred: false, "Expected a class member name.");
                Expression? fieldInitializer = Match(TokenKind.Equals) ? ParseExpression() : null;
                if (isConst && fieldInitializer is null)
                    throw Error("A const class field must have an initializer.", Current);
                Consume(TokenKind.Semicolon, "Expected ';' after class field.");
                fields.Add(new ClassFieldDeclaration(fieldType, fieldName.Lexeme, memberVisibility, memberLocation,
                    fieldInitializer, isConst, isStatic));
                continue;
            }
            var type = ParseType(allowInferred: false);
            var memberName = ConsumeIdentifierLike("Expected a class member name.").Lexeme;
            if (Check(TokenKind.OpenParenthesis))
            {
                var parameters = ParseParameters();
                methods.Add(new FunctionDeclaration(type, memberName, parameters, ParseBlock(), memberVisibility,
                    memberLocation, IsVirtual: isVirtual, IsOverride: isOverride, IsStatic: isStatic));
            }
            else if (Check(TokenKind.OpenBrace))
            {
                var propertyToken = Current;
                var property = ParseProperty(type, memberName, memberVisibility, memberLocation, isVirtual, isOverride);
                if (!isStatic)
                {
                    properties.Add(property);
                    continue;
                }
                if (property.Accessors.Any(accessor => accessor.Kind == PropertyAccessorKind.Init || accessor.Body is null))
                    throw Error("A static property has a get and a set accessor, each with a body.", propertyToken);
                methods.AddRange(StaticPropertyMethods(property, isVirtual, isOverride));
            }
            else
            {
                if (isVirtual || isOverride)
                    throw Error("Only class methods may use 'virtual' or 'override'.", Current);
                Expression? initializer = null;
                if (Match(TokenKind.Equals))
                    initializer = ParseExpression();
                if (isConst && initializer is null)
                    throw Error("A const class field must have an initializer.", Current);
                Consume(TokenKind.Semicolon, "Expected ';' after class field.");
                fields.Add(new ClassFieldDeclaration(type, memberName, memberVisibility, memberLocation,
                    initializer, isConst, isStatic));
            }
        }
        Consume(TokenKind.CloseBrace, "Expected '}' after class declaration.");
        Match(TokenKind.Semicolon);
        return new ClassDeclaration(name, baseType, interfaces, fields, properties, methods, constructors, destructor, visibility,
            location, annotations, typeParameters)
        {
            BaseTypeSyntax = baseTypeSyntax is null ? null : [baseTypeSyntax],
            InterfaceTypeSyntaxes = interfaceTypeSyntaxes
        };
    }

    private string ParseNamedTypeReference(string message)
    {
        return ParseNamedTypeReferenceSyntax(message).NamedType!;
    }

    private TypeSyntax ParseNamedTypeReferenceSyntax(string message)
    {
        var type = ParseType(allowInferred: false);
        if (type.NamedType is null || type.IsPointer || type.IsArray || type.IsString)
            throw Error(message, Current);
        return type;
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
        var properties = new List<PropertyDeclaration>();
        while (!Check(TokenKind.CloseBrace) && !Check(TokenKind.EndOfFile))
        {
            var memberVisibility = ParseVisibility(Visibility.Public);
            if (Check(TokenKind.Static))
                throw Error("An interface cannot declare static members; its contract is per instance.", Current);
            var methodLocation = Current.Location;
            var returnType = ParseType(allowInferred: false);
            var methodName = ConsumeIdentifierLike("Expected an interface method name.").Lexeme;
            if (Check(TokenKind.OpenBrace))
            {
                properties.Add(ParseProperty(returnType, methodName, memberVisibility, methodLocation));
                continue;
            }
            var parameters = ParseParameters();
            Consume(TokenKind.Semicolon, "Expected ';' after interface method.");
            methods.Add(new FunctionDeclaration(returnType, methodName, parameters,
                new BlockStatement([], methodLocation), memberVisibility, methodLocation, IsExternalDeclaration: true));
        }
        Consume(TokenKind.CloseBrace, "Expected '}' after interface declaration.");
        Match(TokenKind.Semicolon);
        return new InterfaceDeclaration(name, methods, properties, visibility, location, annotations, typeParameters);
    }

    // A static property is compiled as two static methods: `get_<name>()` and `set_<name>(value)`, which the analyzer and the
    // backend handle as they handle any static method. Member access to the property calls them.
    private static IEnumerable<FunctionDeclaration> StaticPropertyMethods(PropertyDeclaration property, bool isVirtual, bool isOverride)
    {
        foreach (var accessor in property.Accessors)
        {
            if (accessor.Kind == PropertyAccessorKind.Get)
                yield return new FunctionDeclaration(property.Type, $"get_{property.Name}", [], accessor.Body!,
                    accessor.Visibility, accessor.Location, IsVirtual: isVirtual, IsOverride: isOverride, IsStatic: true);
            else
                yield return new FunctionDeclaration(new TypeSyntax(PrimitiveType.Void, false, accessor.Location),
                    $"set_{property.Name}", [new ParameterDeclaration(property.Type, "value", accessor.Location)], accessor.Body!,
                    accessor.Visibility, accessor.Location, IsVirtual: isVirtual, IsOverride: isOverride, IsStatic: true);
        }
    }

    private PropertyDeclaration ParseProperty(TypeSyntax type, string name, Visibility visibility,
        SourceLocation location, bool isVirtual = false, bool isOverride = false)
    {
        Consume(TokenKind.OpenBrace, "Expected '{' after property name.");
        var accessors = new List<PropertyAccessorDeclaration>();
        while (!Check(TokenKind.CloseBrace) && !Check(TokenKind.EndOfFile))
        {
            var accessorVisibility = visibility;
            if (Current.Kind is TokenKind.Public or TokenKind.Private or TokenKind.Protected or TokenKind.Internal)
                accessorVisibility = ParseVisibility();
            var accessorToken = Advance();
            var accessorName = accessorToken.Lexeme;
            var kind = accessorToken.Kind switch
            {
                TokenKind.Get => PropertyAccessorKind.Get,
                TokenKind.Set => PropertyAccessorKind.Set,
                TokenKind.Init => PropertyAccessorKind.Init,
                TokenKind.Identifier when accessorName == "get" => PropertyAccessorKind.Get,
                TokenKind.Identifier when accessorName == "set" => PropertyAccessorKind.Set,
                TokenKind.Identifier when accessorName == "init" => PropertyAccessorKind.Init,
                _ => throw Error(DiagnosticCode.ExpectedToken, "Expected 'get', 'set', or 'init' in property.", accessorToken)
            };
            BlockStatement? body = null;
            var auto = false;
            if (Match(TokenKind.Semicolon))
                auto = true;
            else if (Check(TokenKind.CloseBrace))
                auto = true;
            else
                body = ParseBlock();
            accessors.Add(new PropertyAccessorDeclaration(kind, accessorVisibility, body, auto, accessorToken.Location));
        }
        Consume(TokenKind.CloseBrace, "Expected '}' after property accessors.");
        Match(TokenKind.Semicolon);
        if (accessors.Count == 0)
            throw Error("A property must declare at least one accessor.", Current);
        if (accessors.Count(a => a.Kind == PropertyAccessorKind.Get) > 1
            || accessors.Count(a => a.Kind == PropertyAccessorKind.Set) > 1
            || accessors.Count(a => a.Kind == PropertyAccessorKind.Init) > 1)
            throw Error("A property cannot declare the same accessor more than once.", Current);
        return new PropertyDeclaration(type, name, visibility, accessors, location, isVirtual, isOverride);
    }

    private Token ConsumeIdentifierLike(string message)
    {
        if (Current.Kind is TokenKind.Identifier or TokenKind.Get or TokenKind.Set or TokenKind.Init)
            return Advance();
        throw Error(message, Current);
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
                var (type, parameterNameToken) = ParseDeclaredName(allowInferred: false, "Expected a parameter name.");
                var parameterName = parameterNameToken.Lexeme;
                var defaultValue = Match(TokenKind.Equals) ? ParseExpression() : null;
                parameters.Add(new ParameterDeclaration(type, parameterName, parameterLocation, defaultValue));
            } while (Match(TokenKind.Comma));
        }
        Consume(TokenKind.CloseParenthesis, "Expected ')' after parameters.");
        return parameters;
    }

    // `: this(...)` or `: base(...)` between a constructor's parameters and its body.
    private ConstructorInitializer? ParseConstructorInitializer()
    {
        if (!Match(TokenKind.Colon))
            return null;
        var location = Current.Location;
        var kind = Match(TokenKind.This) ? ConstructorInitializerKind.This
            : Match(TokenKind.Base) ? ConstructorInitializerKind.Base
            : throw Error(DiagnosticCode.ExpectedToken, "Expected 'this' or 'base' after ':' in a constructor declaration.", Current);
        Consume(TokenKind.OpenParenthesis, "Expected '(' after 'this' or 'base' in a constructor initializer.");
        var arguments = ParseArguments("Expected ')' after constructor initializer arguments.");
        return new ConstructorInitializer(kind, arguments, location);
    }

    // The visibility keyword at the current position, or `fallback` when none is written.
    private Visibility ParseVisibility(Visibility fallback = Visibility.Private)
    {
        if (Match(TokenKind.Public))
            return Visibility.Public;
        if (Match(TokenKind.Protected))
            return Visibility.Protected;
        if (Match(TokenKind.Internal))
            return Visibility.Internal;
        if (Match(TokenKind.Private))
            return Visibility.Private;
        return fallback;
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
            BigInteger? value = null;
            if (Match(TokenKind.Equals))
            {
                var negative = Match(TokenKind.Minus);
                var number = Consume(TokenKind.IntegerLiteral, "Expected an integer enum member value.");
                // The value is exact here. The analyzer checks that it fits the enum's underlying type.
                if (!BigInteger.TryParse((negative ? "-" : "") + number.Lexeme, NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out var parsed))
                    throw Error("Expected an integer enum member value.", number);
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
            var (type, fieldNameToken) = ParseDeclaredName(allowInferred: false, "Expected a field name.");
            var fieldName = fieldNameToken.Lexeme;
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

    // Three forms, each producing one ImportDeclaration per module or symbol, so the rest of the compiler sees only plain imports and
    // symbol lists:
    //   import std.memory;                          a module, optionally `as alias`
    //   import std.{memory, strings} as {mem, str}; a group of modules, with an optional list of aliases
    //   import std.memory{alloc, free as release};  a symbol list, which brings only the listed names into scope
    private IReadOnlyList<ImportDeclaration> ParseImports()
    {
        var imports = new List<ImportDeclaration>();
        while (Match(TokenKind.Import))
        {
            var token = Previous;
            var name = Consume(TokenKind.Identifier, "Expected an imported module name.").Lexeme;
            while (Check(TokenKind.Dot) && Peek(1).Kind == TokenKind.Identifier)
            {
                Advance();
                name += "." + Advance().Lexeme;
            }

            if (Check(TokenKind.Dot) && Peek(1).Kind == TokenKind.OpenBrace)
            {
                Advance();
                ParseImportGroup(name, token, imports);
            }
            else if (Check(TokenKind.OpenBrace))
            {
                imports.Add(new ImportDeclaration(name, token.Location, null, ParseImportSymbols()));
            }
            else
            {
                string? alias = null;
                if (Match(TokenKind.As))
                    alias = Consume(TokenKind.Identifier, "Expected an import alias after 'as'.").Lexeme;
                imports.Add(new ImportDeclaration(name, token.Location, alias));
            }
            Consume(TokenKind.Semicolon, "Expected ';' after import declaration.");
        }

        return imports;
    }

    // Parses `{memory, strings}` after the prefix's dot, and an optional `as {mem, str}`, and adds one import per module.
    private void ParseImportGroup(string prefix, Token importToken, List<ImportDeclaration> imports)
    {
        var modules = new List<Token>();
        Consume(TokenKind.OpenBrace, "Expected '{' to start an import group.");
        do
        {
            modules.Add(Consume(TokenKind.Identifier, "Expected a module name in the import group."));
            if (Check(TokenKind.OpenBrace))
                throw Error("Import groups do not nest; list the module with its own import instead.", Current);
        }
        while (Match(TokenKind.Comma));
        Consume(TokenKind.CloseBrace, "Expected '}' after the import group.");

        var aliases = new List<string>();
        if (Match(TokenKind.As))
        {
            var aliasToken = Current;
            if (!Check(TokenKind.OpenBrace))
                throw Error("A module group takes a list of aliases after 'as', such as as {mem, str}.", aliasToken);
            Advance();
            do
                aliases.Add(Consume(TokenKind.Identifier, "Expected an alias in the list after 'as'.").Lexeme);
            while (Match(TokenKind.Comma));
            Consume(TokenKind.CloseBrace, "Expected '}' after the list of aliases.");
            if (aliases.Count != modules.Count)
                throw Error(
                    $"The import group names {modules.Count} modules, but its alias list has {aliases.Count} names.",
                    aliasToken);
        }

        for (var index = 0; index < modules.Count; index++)
            imports.Add(new ImportDeclaration($"{prefix}.{modules[index].Lexeme}", importToken.Location,
                aliases.Count == 0 ? null : aliases[index]));
    }

    // Parses `{alloc, free as release}` after a module name.
    private IReadOnlyList<ImportSymbol> ParseImportSymbols()
    {
        Consume(TokenKind.OpenBrace, "Expected '{' to start a symbol list.");
        var symbols = new List<ImportSymbol>();
        if (Check(TokenKind.CloseBrace))
            throw Error("A symbol list needs at least one name.", Current);
        do
        {
            if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Dot)
                throw Error("A symbol list names declarations of one module, so each entry is a plain name.", Peek(1));
            var nameToken = Consume(TokenKind.Identifier, "Expected a name in the symbol list.");
            var localName = nameToken.Lexeme;
            if (Match(TokenKind.As))
                localName = Consume(TokenKind.Identifier, "Expected an alias after 'as' in the symbol list.").Lexeme;
            symbols.Add(new ImportSymbol(nameToken.Lexeme, localName, nameToken.Location));
        }
        while (Match(TokenKind.Comma));
        Consume(TokenKind.CloseBrace, "Expected '}' after the symbol list.");
        return symbols;
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
                var (type, parameterNameToken) = ParseDeclaredName(allowInferred: false, "Expected a parameter name.");
                var parameterName = parameterNameToken.Lexeme;
                var defaultValue = Match(TokenKind.Equals) ? ParseExpression() : null;
                parameters.Add(new ParameterDeclaration(type, parameterName, parameterLocation, defaultValue));
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
                : throw Error(DiagnosticCode.ExpectedToken, "Expected an annotation name after '@'.", Current);
            var arguments = new List<string>();
            if (Match(TokenKind.OpenParenthesis))
            {
                if (!Check(TokenKind.CloseParenthesis))
                {
                    do
                    {
                        var argument = Advance();
                        if (argument.Kind is not (TokenKind.StringLiteral or TokenKind.IntegerLiteral or TokenKind.Identifier))
                            throw Error(DiagnosticCode.ExpectedToken, "Expected a string, integer, or identifier annotation argument.", argument);
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
            // An error ends one statement, not the function, so the rest of the body is still checked.
            try
            {
                statements.Add(ParseStatement());
            }
            catch (ParseException exception) when (_diagnostics is not null)
            {
                Report(exception);
                SynchronizeStatement();
            }
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
        if (Match(TokenKind.Throw))
            return ParseThrow(Previous.Location);
        if (Match(TokenKind.Delete))
            return ParseDelete(Previous.Location);

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
        if (Match(TokenKind.Try))
            return ParseTry(Previous.Location);

        if (Check(TokenKind.Const) || Check(TokenKind.Var) || IsType(Current.Kind)
            || (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Identifier)
            || (Check(TokenKind.Fn) && !IsLambdaStart())
            || IsQualifiedVariableDeclarationStart()
            || IsGenericVariableDeclarationStart()
            || IsNamedPointerOrArrayDeclarationStart())
        {
            return ParseVariableDeclaration();
        }

        var expressionLocation = Current.Location;
        var expression = ParseExpression();
        if (TryConsumeAssignmentOperator(out var assignmentOperator))
        {
            var value = ParseExpression();
            Consume(TokenKind.Semicolon, "Expected ';' after assignment.");
            return new AssignmentStatement(expression, value, expressionLocation, assignmentOperator);
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
        // Cases and the default are kept in the order they are written, because fall-through follows that order.
        while (Check(TokenKind.Case) || Check(TokenKind.Default))
        {
            if (Match(TokenKind.Default))
            {
                var defaultToken = Previous;
                if (cases.Any(item => item.IsDefault))
                    throw Error("A switch can have only one default case.", defaultToken);
                Consume(TokenKind.Colon, "Expected ':' after 'default'.");
                cases.Add(new SwitchCase(null, null, true, null, ParseStatement(), defaultToken.Location));
                continue;
            }
            Advance();
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

    // A for-in header is `var name in` or `Type name in`. The type is read speculatively and the position is restored, so a for loop with
    // a declaration (`for (i32 i = 0; ...)`) is still a for loop.
    private bool IsForInHeader()
    {
        var start = _position;
        try
        {
            if (!Match(TokenKind.Var))
                ParseType(allowInferred: false);
            return Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.In;
        }
        catch (ParseException)
        {
            return false;
        }
        finally
        {
            _position = start;
        }
    }

    private Statement ParseFor(SourceLocation location)
    {
        Consume(TokenKind.OpenParenthesis, "Expected '(' after 'for'.");
        if (IsForInHeader())
        {
            var variableType = ParseType(allowInferred: true);
            var variable = Consume(TokenKind.Identifier, "Expected an iteration variable name.").Lexeme;
            Consume(TokenKind.In, "Expected 'in' after the iteration variable.");
            var collection = ParseExpression();
            Consume(TokenKind.CloseParenthesis, "Expected ')' after the for-in collection.");
            return new ForInStatement(variable, variableType, collection, ParseStatement(), location);
        }

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
            || (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Identifier)
            || (Check(TokenKind.Fn) && !IsLambdaStart()))
            return ParseVariableDeclaration();

        var expressionLocation = Current.Location;
        var expression = ParseExpression();
        if (!isInitializer && expression is UnaryExpression increment
            && increment.Operator is TokenKind.PlusPlus or TokenKind.MinusMinus
            && increment.IsPostfix)
        {
            // x++ is x += 1, so the target is evaluated once like any compound assignment.
            var operatorKind = increment.Operator == TokenKind.PlusPlus ? TokenKind.PlusEquals : TokenKind.MinusEquals;
            return new AssignmentStatement(increment.Operand,
                new IntegerLiteralExpression("1", increment.Location), expressionLocation, operatorKind);
        }
        if (TryConsumeAssignmentOperator(out var assignmentOperator))
        {
            var value = ParseExpression();
            if (isInitializer)
                Consume(TokenKind.Semicolon, "Expected ';' after for initializer.");
            return new AssignmentStatement(expression, value, expressionLocation, assignmentOperator);
        }
        if (isInitializer)
            Consume(TokenKind.Semicolon, "Expected ';' after for initializer.");
        return new ExpressionStatement(expression, expressionLocation);
    }

    private VariableDeclarationStatement ParseVariableDeclaration()
    {
        var location = Current.Location;
        var isConst = Match(TokenKind.Const);
        var (type, nameToken) = ParseDeclaredName(allowInferred: true, "Expected a variable name.");
        var name = nameToken.Lexeme;
        Expression? initializer = null;
        if (Match(TokenKind.Equals))
        {
            initializer = ParseExpression();
        }

        Consume(TokenKind.Semicolon, "Expected ';' after variable declaration.");
        return new VariableDeclarationStatement(type, name, isConst, initializer, location);
    }

    // A global variable is `[const] Type name` followed by '=' or ';'. A function is `Type name` followed by '(' or '<',
    // so the token after the name tells them apart.
    // `type Name = Target;`, with an optional `public` or `private` before `type`.
    private bool IsTypeAliasStart()
    {
        var index = _position;
        if (index < _tokens.Count && _tokens[index].Kind is TokenKind.Public or TokenKind.Private)
            index++;
        return index < _tokens.Count && _tokens[index].Kind == TokenKind.Type;
    }

    private TypeAliasDeclaration ParseTypeAlias()
    {
        var location = Current.Location;
        var visibility = ParseVisibility();
        Consume(TokenKind.Type, "Expected 'type'.");
        var name = Consume(TokenKind.Identifier, "Expected a type alias name after 'type'.").Lexeme;
        Consume(TokenKind.Equals, "Expected '=' after the type alias name.");
        var target = ParseType(allowInferred: false);
        Consume(TokenKind.Semicolon, "Expected ';' after the type alias.");
        return new TypeAliasDeclaration(name, target, visibility, location);
    }

    private bool IsGlobalVariableStart()
    {
        var start = _position;
        try
        {
            Match(TokenKind.Const);
            if (Check(TokenKind.Fn))
            {
                // A function pointer declaration names its variable inside its form, so the name is already read.
                ParseDeclaredName(allowInferred: false, "Expected a global variable name.");
                return Check(TokenKind.Equals) || Check(TokenKind.Semicolon);
            }
            ParseType(allowInferred: false);
            return Check(TokenKind.Identifier) && Peek(1).Kind is TokenKind.Equals or TokenKind.Semicolon;
        }
        catch (ParseException)
        {
            return false;
        }
        finally
        {
            _position = start;
        }
    }

    private GlobalVariableDeclaration ParseGlobalVariable()
    {
        var location = Current.Location;
        var isConst = Match(TokenKind.Const);
        var (type, nameToken) = ParseDeclaredName(allowInferred: false, "Expected a global variable name.");
        var name = nameToken.Lexeme;
        Expression? initializer = Match(TokenKind.Equals) ? ParseExpression() : null;
        Consume(TokenKind.Semicolon, "Expected ';' after global variable declaration.");
        return new GlobalVariableDeclaration(type, name, isConst, initializer, location);
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

    // `delete target;` releases an object allocated with `new`: its destructor runs, then its memory is freed.
    private DeleteStatement ParseDelete(SourceLocation location)
    {
        var target = ParseExpression();
        Consume(TokenKind.Semicolon, "Expected ';' after delete target.");
        return new DeleteStatement(target, location);
    }

    private ThrowStatement ParseThrow(SourceLocation location)
    {
        var value = Check(TokenKind.Semicolon) ? null : ParseExpression();
        Consume(TokenKind.Semicolon, value is null ? "Expected ';' after throw." : "Expected ';' after throw expression.");
        return new ThrowStatement(value, location);
    }

    private TryStatement ParseTry(SourceLocation location)
    {
        var body = ParseBlock();
        var catches = new List<CatchClause>();
        while (Match(TokenKind.Catch))
        {
            var catchToken = Previous;
            TypeSyntax? exceptionType = null;
            string? bindingName = null;
            if (Match(TokenKind.OpenParenthesis))
            {
                exceptionType = ParseType(allowInferred: false);
                bindingName = Consume(TokenKind.Identifier, "Expected a catch binding name.").Lexeme;
                Consume(TokenKind.CloseParenthesis, "Expected ')' after catch declaration.");
            }
            catches.Add(new CatchClause(exceptionType, bindingName, ParseBlock(), catchToken.Location));
        }

        BlockStatement? finallyBlock = null;
        if (Match(TokenKind.Finally))
            finallyBlock = ParseBlock();

        if (catches.Count == 0 && finallyBlock is null)
            throw Error(DiagnosticCode.ExpectedToken, "Expected at least one 'catch' clause or a 'finally' block after try block.", Current);
        return new TryStatement(body, catches, finallyBlock, location);
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
            if (Match(TokenKind.As))
            {
                var targetType = ParseType(allowInferred: false);
                left = new CastExpression(targetType, left, left.Location);
                continue;
            }
            // Two '<' or two '>' tokens form a shift only when they are written next to each other: `a < < b` is not a shift.
            var isLeftShift = Current.Kind == TokenKind.LessThan && IsWrittenNextToCurrent(TokenKind.LessThan);
            var isRightShift = Current.Kind == TokenKind.GreaterThan && IsWrittenNextToCurrent(TokenKind.GreaterThan);
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
        if (Check(TokenKind.Bang) || Check(TokenKind.Tilde) || Check(TokenKind.Minus) || Check(TokenKind.Plus)
            || Check(TokenKind.Ampersand) || Check(TokenKind.Star)
            || Check(TokenKind.PlusPlus) || Check(TokenKind.MinusMinus))
        {
            var token = Advance();
            return new UnaryExpression(token.Kind, ParseUnaryExpression(), token.Location);
        }

        return ParsePostfixExpression();
    }

    // The expression `a.b.c` for the names a, b and c, in order.
    private static Expression NameChain(IEnumerable<string> names, SourceLocation location)
    {
        var parts = names.ToList();
        Expression chain = new IdentifierExpression(parts[0], location);
        foreach (var part in parts.Skip(1))
            chain = new MemberAccessExpression(chain, part, location);
        return chain;
    }

    // The arguments of a call, after its '(' and up to and including its ')'.
    private List<Expression> ParseArguments(string closeMessage = "Expected ')' after arguments.")
    {
        var arguments = new List<Expression>();
        if (!Check(TokenKind.CloseParenthesis))
        {
            do
            {
                arguments.Add(ParseArgument());
            } while (Match(TokenKind.Comma));
        }

        Consume(TokenKind.CloseParenthesis, closeMessage);
        return arguments;
    }

    // One argument: a value, or `name: value` for the parameter called `name`. A label is recognized only at the start of an
    // argument, so the ':' of `?:` in a value is never taken for a label.
    private Expression ParseArgument()
    {
        if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Colon)
        {
            var name = Advance();
            Advance();
            return new NamedArgumentExpression(name.Lexeme, ParseExpression(), name.Location);
        }

        return ParseExpression();
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
                var member = ConsumeIdentifierLike("Expected an identifier after '.'.");
                // `this.name(...)` is a call to a method of the current instance. It has the name form `this.name`, which the
                // analyzer resolves from the receiver's name.
                if (expression is ThisExpression && Match(TokenKind.OpenParenthesis))
                {
                    var arguments = ParseArguments("Expected ')' after arguments.");
                    expression = new CallExpression($"this.{member.Lexeme}", arguments, expression.Location);
                    continue;
                }
                // Any other `expression.name(...)` calls the method on the value of expression, such as `make().name()`.
                expression = ParseMemberSuffix(expression, member);
            }
            else if (Match(TokenKind.Arrow))
            {
                var member = ConsumeIdentifierLike("Expected an identifier after '->'.");
                // `pointer->name` is `(*pointer).name`. The parser builds that same tree, so both forms are analyzed and lowered alike.
                var pointee = new UnaryExpression(TokenKind.Star, expression, expression.Location);
                expression = ParseMemberSuffix(pointee, member);
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

    // The part of a member access after its receiver: `name(...)` calls a method of the receiver's value, and `name` reads the member.
    private Expression ParseMemberSuffix(Expression receiver, Token member)
    {
        if (Match(TokenKind.OpenParenthesis))
        {
            var arguments = ParseArguments();
            return new CallExpression(member.Lexeme, arguments, member.Location, null, receiver);
        }
        return new MemberAccessExpression(receiver, member.Lexeme, receiver.Location);
    }

    private Expression ParsePrimaryExpression()
    {
        if (Check(TokenKind.Fn))
            return ParseLambda();
        var token = Advance();
        switch (token.Kind)
        {
            case TokenKind.New:
                {
                    var type = ParseType(allowInferred: false);
                    Consume(TokenKind.OpenParenthesis, "Expected '(' after type in new expression.");
                    var arguments = ParseArguments("Expected ')' after constructor arguments.");
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
                var typeArguments = ParseTypeArguments();
                while (Match(TokenKind.Dot))
                {
                    qualifiedParts.Add(ConsumeIdentifierLike("Expected an identifier after '.'.").Lexeme);
                    typeArguments ??= ParseTypeArguments();
                }
                name = string.Join(".", qualifiedParts);
                if (Match(TokenKind.OpenParenthesis))
                {
                    var arguments = ParseArguments("Expected ')' after arguments.");
                    // `a.b.name(...)` with more than one dot calls a method on the value of `a.b`, unless the chain names a module
                    // function, which the analyzer decides. The receiver form carries the type arguments as well.
                    if (qualifiedParts.Count > 2)
                        return new CallExpression(qualifiedParts[^1], arguments, token.Location, typeArguments,
                            NameChain(qualifiedParts.Take(qualifiedParts.Count - 1), token.Location));
                    // A capitalized name may construct a class, `Box(...)` or `util.Box(...)`. The analyzer decides: a name that
                    // is not a class is a call, as a union case or a static method of a class is.
                    if (char.IsUpper(qualifiedParts[^1][0]) && qualifiedParts.Count <= 2)
                        return new ClassConstructionExpression(name, arguments, token.Location, typeArguments);
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

                if (typeArguments is not null && qualifiedParts.Count > 1)
                    return new CallExpression(name, [], token.Location, typeArguments) { IsBareName = true };

                if (qualifiedParts.Count > 1)
                    return NameChain(qualifiedParts, token.Location);

                return new IdentifierExpression(name, token.Location);
            case TokenKind.This:
                return new ThisExpression(token.Location);
            case TokenKind.Base:
                {
                    // `base.name(...)` calls a method of the base class on the current instance, without virtual dispatch.
                    // Other uses of `base` are not supported.
                    Consume(TokenKind.Dot, "Expected '.' after 'base'.");
                    var method = ConsumeIdentifierLike("Expected a method name after 'base.'.");
                    Consume(TokenKind.OpenParenthesis, "Only calls are supported after 'base.', such as base.name(...).");
                    var arguments = ParseArguments("Expected ')' after arguments.");
                    return new CallExpression($"base.{method.Lexeme}", arguments, token.Location);
                }
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
            case TokenKind.Null:
                return new NullLiteralExpression(token.Location);
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
                // A parenthesized expression only. Casts are written as cast<T>(x) or x as T.
                var expression = ParseExpression();
                Consume(TokenKind.CloseParenthesis, "Expected ')' after expression.");
                return expression;
            default:
                // The token is not part of the expression, so it is given back. Statement recovery then starts at it,
                // rather than skipping it along with the statement that holds it.
                if (token.Kind != TokenKind.EndOfFile)
                    _position--;
                throw Error(DiagnosticCode.ExpectedToken, $"Expected an expression, but found {Describe(token)}.", token);
        }
    }

    // A function pointer type where no name is declared: `fn <return> (<types>)`.
    private TypeSyntax ParseFunctionType()
    {
        var location = Consume(TokenKind.Fn, "Expected 'fn'.").Location;
        var returnType = ParseType(allowInferred: false);
        return new TypeSyntax(null, false, location) { FunctionReturn = returnType, FunctionParameters = ParseFunctionTypeParameters() };
    }

    // The parameter types of a function pointer type, `(<type>, ...)`. The parameters have no names.
    private IReadOnlyList<TypeSyntax> ParseFunctionTypeParameters()
    {
        Consume(TokenKind.OpenParenthesis, "Expected '(' after the return type of a function pointer.");
        var types = new List<TypeSyntax>();
        if (!Check(TokenKind.CloseParenthesis))
        {
            do
            {
                types.Add(ParseType(allowInferred: false));
            }
            while (Match(TokenKind.Comma));
        }
        Consume(TokenKind.CloseParenthesis, "Expected ')' after the parameter types of a function pointer.");
        return types;
    }

    // A declared name with its type. A function pointer declares its name inside its form, `fn <return> <name>(<types>)`. Any other
    // declaration writes its type first and then its name. `var` is allowed only where `allowInferred` is set, and then no name is inside a form.
    private (TypeSyntax Type, Token Name) ParseDeclaredName(bool allowInferred, string message)
    {
        if (!Check(TokenKind.Fn))
        {
            var type = ParseType(allowInferred);
            return (type, Consume(TokenKind.Identifier, message));
        }
        var location = Consume(TokenKind.Fn, "Expected 'fn'.").Location;
        var returnType = ParseType(allowInferred: false);
        var name = Consume(TokenKind.Identifier,
            "Expected a name in a function pointer declaration, which has the form fn <return> <name>(<types>).");
        var parameters = ParseFunctionTypeParameters();
        return (new TypeSyntax(null, false, location) { FunctionReturn = returnType, FunctionParameters = parameters }, name);
    }

    // Whether a function pointer declaration starts here: `fn <return> <name>(`. A form without a name, such as a return type, does not.
    private bool IsFunctionPointerDeclarationStart()
    {
        if (!Check(TokenKind.Fn))
            return false;
        var start = _position;
        try
        {
            Advance();
            ParseType(allowInferred: false);
            return Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.OpenParenthesis;
        }
        catch (ParseException)
        {
            return false;
        }
        finally
        {
            _position = start;
        }
    }

    // Whether a lambda starts here: `fn <return> (` with its parameter list closed by `)` and followed by `{`. Any other `fn` starts a type or a
    // declaration.
    private bool IsLambdaStart()
    {
        if (!Check(TokenKind.Fn))
            return false;
        var start = _position;
        try
        {
            Advance();
            ParseType(allowInferred: false);
            if (!Check(TokenKind.OpenParenthesis))
                return false;
            var depth = 0;
            while (!Check(TokenKind.EndOfFile))
            {
                if (Check(TokenKind.OpenParenthesis))
                    depth++;
                else if (Check(TokenKind.CloseParenthesis) && --depth == 0)
                {
                    Advance();
                    return Check(TokenKind.OpenBrace);
                }
                Advance();
            }
            return false;
        }
        catch (ParseException)
        {
            return false;
        }
        finally
        {
            _position = start;
        }
    }

    // A lambda without captures: `fn <return> (<type> <name>, ...) { <body> }`.
    private LambdaExpression ParseLambda()
    {
        var location = Consume(TokenKind.Fn, "Expected 'fn'.").Location;
        var returnType = ParseType(allowInferred: false);
        Consume(TokenKind.OpenParenthesis, "Expected '(' after the return type of a lambda.");
        var parameters = new List<ParameterDeclaration>();
        if (!Check(TokenKind.CloseParenthesis))
        {
            do
            {
                var parameterLocation = Current.Location;
                var type = ParseType(allowInferred: false);
                var name = Consume(TokenKind.Identifier, "Expected a lambda parameter name.").Lexeme;
                parameters.Add(new ParameterDeclaration(type, name, parameterLocation));
            }
            while (Match(TokenKind.Comma));
        }
        Consume(TokenKind.CloseParenthesis, "Expected ')' after the lambda parameters.");
        return new LambdaExpression(returnType, parameters, ParseBlock(), location);
    }

    private TypeSyntax ParseType(bool allowInferred)
    {
        if (allowInferred && Match(TokenKind.Var))
        {
            return new TypeSyntax(null, true, Previous.Location);
        }

        if (Match(TokenKind.String))
            return ParsePointerSuffix(new TypeSyntax(null, false, Previous.Location, true));

        if (Check(TokenKind.Fn))
            return ParsePointerSuffix(ParseFunctionType());

        if (PrimitiveTypes.TryGetValue(Current.Kind, out var primitive))
        {
            var token = Advance();
            var type = new TypeSyntax(primitive, false, token.Location);
            return ParsePointerSuffix(type);
        }

        if (Check(TokenKind.Identifier))
        {
            var token = Advance();
            // A type can be named through its module, as `util.Box`.
            var typeName = token.Lexeme;
            while (Check(TokenKind.Dot) && Peek(1).Kind == TokenKind.Identifier)
            {
                Advance();
                typeName += "." + Advance().Lexeme;
            }
            var typeArguments = ParseNamedTypeArguments();
            return ParsePointerSuffix(new TypeSyntax(null, false, token.Location, NamedType: typeName,
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
            var constraints = new List<string>();
            if (Match(TokenKind.Colon))
            {
                constraints.Add(ConsumeIdentifierLike("Expected an interface name after ':' in a type parameter.").Lexeme);
                while (Match(TokenKind.Ampersand))
                    constraints.Add(ConsumeIdentifierLike("Expected an interface name after '&' in a type parameter.").Lexeme);
            }
            parameters.Add(new TypeParameterSyntax(token.Lexeme, token.Location) { Constraints = constraints });
        } while (Match(TokenKind.Comma));

        Consume(TokenKind.GreaterThan, "Expected '>' after generic type parameters.");
        return parameters;
    }

    // `Name* name` and `Name[N] name` declare a variable of a named type. Neither is a valid statement otherwise, because a
    // pointer or an element cannot be the target of a plain expression statement that begins this way.
    // Whether the next token is of the given kind and starts in the column right after the current token.
    private bool IsWrittenNextToCurrent(TokenKind kind)
    {
        var next = Peek(1);
        return next.Kind == kind && next.Location.Line == Current.Location.Line
            && next.Location.Column == Current.Location.Column + 1;
    }

    private bool IsNamedPointerOrArrayDeclarationStart()
    {
        if (!Check(TokenKind.Identifier))
            return false;
        var index = _position + 1;
        if (index < _tokens.Count && _tokens[index].Kind == TokenKind.OpenBracket)
        {
            // Each dimension is `[N]` with an integer literal N.
            while (index + 2 < _tokens.Count && _tokens[index].Kind == TokenKind.OpenBracket
                   && _tokens[index + 1].Kind == TokenKind.IntegerLiteral && _tokens[index + 2].Kind == TokenKind.CloseBracket)
                index += 3;
        }
        else if (index < _tokens.Count && _tokens[index].Kind == TokenKind.Star)
        {
            while (index < _tokens.Count && _tokens[index].Kind == TokenKind.Star)
                index++;
        }
        else
        {
            return false;
        }

        return index + 1 < _tokens.Count && _tokens[index].Kind == TokenKind.Identifier
            && _tokens[index + 1].Kind is TokenKind.Equals or TokenKind.Semicolon;
    }

    // `util.Box name` declares a variable whose type is named through its module. `a.b = c` is an assignment, and `a.b()` a call.
    private bool IsQualifiedVariableDeclarationStart()
    {
        if (!Check(TokenKind.Identifier) || Peek(1).Kind != TokenKind.Dot)
            return false;

        var index = _position;
        while (index + 1 < _tokens.Count && _tokens[index].Kind == TokenKind.Identifier && _tokens[index + 1].Kind == TokenKind.Dot)
            index += 2;
        return index + 1 < _tokens.Count && _tokens[index].Kind == TokenKind.Identifier
            && _tokens[index + 1].Kind == TokenKind.Identifier;
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
            // Type arguments never contain these tokens, so the '<' was a comparison.
            else if (_tokens[index].Kind is TokenKind.Semicolon or TokenKind.OpenBrace or TokenKind.CloseBrace
                     or TokenKind.CloseParenthesis or TokenKind.EndOfFile)
                return false;
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
                        return index + 1 < _tokens.Count
                            && _tokens[index + 1].Kind is TokenKind.OpenParenthesis or TokenKind.Dot;
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

}
