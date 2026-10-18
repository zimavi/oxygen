using System.Linq;
using Oxygen.Compiler.Ast;
using Oxygen.Compiler.Lexer;
using Oxygen.Compiler.Parsing;
using Serilog;
using Xunit;

namespace Oxygen.Compiler.Tests;

public sealed class ParserTests
{
    private readonly Tokenizer _tokenizer = new(Log.Logger);

    [Fact]
    public void ParsesClassAndInterfaceProperties()
    {
        var unit = Parse(
            "interface Named { public string Name { get; } } " +
            "class Person : Named { public string Name { get; private set; } " +
            "public i32 Id { get; init; } } " +
            "i32 main() { return 0; }");

        var contract = Assert.Single(unit.Interfaces).DeclaredProperties;
        Assert.Equal("Name", Assert.Single(contract).Name);
        var properties = Assert.Single(unit.Classes).DeclaredProperties;
        Assert.Equal(2, properties.Count);
        Assert.Contains(properties, property => property.HasInitializer);
        Assert.Equal(Visibility.Private, Assert.Single(properties[0].Accessors,
            accessor => accessor.Kind == PropertyAccessorKind.Set).Visibility);
    }

    [Fact]
    public void ParsesMainFunctionAndReturnExpression()
    {
        var unit = Parse("i32 main() { return 1 + 2 * 3; }");

        var function = Assert.Single(unit.Functions);
        Assert.Equal("main", function.Name);
        Assert.Equal(PrimitiveType.I32, function.ReturnType.Primitive);
        var statement = Assert.IsType<ReturnStatement>(Assert.Single(function.Body.Statements));
        var addition = Assert.IsType<BinaryExpression>(statement.Value);
        Assert.Equal(TokenKind.Plus, addition.Operator);
        Assert.IsType<BinaryExpression>(addition.Right);
    }

    [Fact]
    public void ParsesThrowAndTypedAndBareCatchClauses()
    {
        var unit = Parse(
            "class Exception { } class Failure : Exception { } " +
            "void fail(Failure error) { throw error; } " +
            "i32 main() { try { fail(new Failure()); } catch (Failure caught) { return 1; } catch { return 2; } finally { fail(new Failure()); } }");

        Assert.IsType<ThrowStatement>(unit.Functions[0].Body.Statements[0]);
        var tryStatement = Assert.IsType<TryStatement>(unit.Functions[1].Body.Statements[0]);
        Assert.IsType<ExpressionStatement>(Assert.Single(tryStatement.Body.Statements));
        Assert.Equal("Failure", tryStatement.Catches[0].ExceptionType!.NamedType);
        Assert.Equal("caught", tryStatement.Catches[0].BindingName);
        Assert.Null(tryStatement.Catches[1].ExceptionType);
        Assert.Null(tryStatement.Catches[1].BindingName);
        Assert.NotNull(tryStatement.Finally);
    }

    [Fact]
    public void ParsesForInArraySyntax()
    {
        var unit = Parse("i32 main() { i32[2] values = { 1, 2 }; for (var value in values) { return value; } return 0; }");

        var loop = Assert.IsType<ForInStatement>(unit.Functions[0].Body.Statements[1]);
        Assert.Equal("value", loop.VariableName);
        Assert.True(loop.VariableType.IsInferred);
        Assert.IsType<IdentifierExpression>(loop.Collection);
    }

    [Fact]
    public void ParsesVirtualAndOverrideMethodModifiers()
    {
        var unit = Parse("class Base { public virtual i32 value() { return 1; } } " +
                         "class Child : Base { override public i32 value() { return 2; } } " +
                         "i32 main() { return 0; }");

        Assert.True(unit.Classes[0].Methods[0].IsVirtual);
        Assert.False(unit.Classes[0].Methods[0].IsOverride);
        Assert.True(unit.Classes[1].Methods[0].IsOverride);
    }

    [Fact]
    public void ParsesValueClassesAndDistinguishesDirectConstruction()
    {
        var unit = Parse("@value class Point { } i32 main() { var point = Point(); var other = new Point(); return 0; }");

        Assert.True(unit.Classes[0].IsValue);
        var statements = unit.Functions[0].Body.Statements;
        Assert.IsType<ClassConstructionExpression>(
            Assert.IsType<VariableDeclarationStatement>(statements[0]).Initializer);
        Assert.IsType<NewExpression>(
            Assert.IsType<VariableDeclarationStatement>(statements[1]).Initializer);
    }

    [Fact]
    public void ParsesRightAssociativeConditionalExpressions()
    {
        var unit = Parse("i32 main() { return true ? 1 : false ? 2 : 3; }");

        var conditional = Assert.IsType<ConditionalExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(unit.Functions[0].Body.Statements)).Value);
        Assert.IsType<BooleanLiteralExpression>(conditional.Condition);
        Assert.Equal("1", Assert.IsType<IntegerLiteralExpression>(conditional.WhenTrue).Lexeme);
        var nested = Assert.IsType<ConditionalExpression>(conditional.WhenFalse);
        Assert.Equal("2", Assert.IsType<IntegerLiteralExpression>(nested.WhenTrue).Lexeme);
        Assert.Equal("3", Assert.IsType<IntegerLiteralExpression>(nested.WhenFalse).Lexeme);
    }

    [Fact]
    public void ParsesEnumsAndExplicitCasts()
    {
        var unit = Parse("enum Color : i32 { Red, Green = 4, Blue } i32 main() { return (i32) Color.Blue; }");

        var enumeration = Assert.Single(unit.Enums);
        Assert.Equal("Color", enumeration.Name);
        Assert.Equal(PrimitiveType.I32, enumeration.UnderlyingType);
        Assert.Equal(["Red", "Green", "Blue"], enumeration.Members.Select(member => member.Name));
        var cast = Assert.IsType<CastExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(Assert.Single(unit.Functions).Body.Statements)).Value);
        Assert.Equal(PrimitiveType.I32, cast.TargetType.Primitive);
    }

    [Fact]
    public void DefaultsEnumUnderlyingTypeToI32()
    {
        var unit = Parse("enum Color { Red, Green } i32 main() { return 0; }");

        Assert.Equal(PrimitiveType.I32, Assert.Single(unit.Enums).UnderlyingType);
    }

    [Fact]
    public void ParsesGenericAggregateDeclarationsAndTypeUses()
    {
        var unit = Parse(
            "struct Pair<T, U> { T first; U second; } union Result<T> { Ok(T); } " +
            "i32 main() { Pair<i32, Pair<i64, i32>> value; return 0; }");

        var pair = Assert.Single(unit.Structs);
        Assert.Equal(["T", "U"], pair.DeclaredTypeParameters.Select(parameter => parameter.Name));
        var result = Assert.Single(unit.Unions);
        Assert.Equal(["T"], result.DeclaredTypeParameters.Select(parameter => parameter.Name));
        var variable = Assert.IsType<VariableDeclarationStatement>(
            Assert.Single(Assert.Single(unit.Functions).Body.Statements.Take(1)));
        Assert.Equal("Pair", variable.Type.NamedType);
        Assert.Equal(["i32", "Pair"], variable.Type.DeclaredTypeArguments
            .Select(argument => argument.NamedType ?? argument.Primitive?.ToString()?.ToLowerInvariant()));
        Assert.Equal(["i64", "i32"], variable.Type.DeclaredTypeArguments[1].DeclaredTypeArguments
            .Select(argument => argument.Primitive?.ToString().ToLowerInvariant()));
    }

    [Fact]
    public void ParsesGenericClassInterfacesAndQualifiedUnionCases()
    {
        var unit = Parse(
            "interface Iterator<T> { Option<T> next(); } " +
            "class Items<T> : Iterator<T> { Option<T> next() { return Option<T>.None; } } " +
            "i32 main() { return 0; }");

        var iterator = Assert.Single(unit.Interfaces);
        Assert.Equal(["T"], iterator.DeclaredTypeParameters.Select(parameter => parameter.Name));
        var items = Assert.Single(unit.Classes);
        Assert.Equal("Iterator", items.BaseType);
        var next = Assert.Single(items.Methods);
        var unionCase = Assert.IsType<CallExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(next.Body.Statements)).Value);
        Assert.Equal("Option.None", unionCase.Name);
        Assert.Single(unionCase.ExplicitTypeArguments);
    }

    [Fact]
    public void ParsesConstructorsDestructorsAutoPropertiesAndInitializedFields()
    {
        var unit = Parse(
            "class Buffer { const i32 size = 4; i32 position = 0; " +
            "public i32 Length { get; private set } " +
            "public Buffer(i32 length) { this.Length = length; } " +
            "public ~Buffer() { position = 0; } } " +
            "i32 main() { return 0; }");

        var buffer = Assert.Single(unit.Classes);
        Assert.Equal(2, buffer.Fields.Count);
        Assert.True(buffer.Fields[0].IsConst);
        Assert.NotNull(buffer.Fields[0].Initializer);
        Assert.NotNull(buffer.Fields[1].Initializer);
        Assert.True(Assert.Single(buffer.DeclaredProperties).HasGetter);
        Assert.Single(buffer.Constructors);
        Assert.NotNull(buffer.Destructor);
    }

    [Fact]
    public void ParsesTaggedUnionsAndSwitchCases()
    {
        var unit = Parse(
            "union Result { Ok(i32); Error(i32); Empty; } i32 main() { Result value = Result.Ok(42); switch (value) { case Ok(number): return number; case Error(code): return code; case Empty: return 0; } }");

        var union = Assert.Single(unit.Unions);
        Assert.Equal(["Ok", "Error", "Empty"], union.Cases.Select(item => item.Name));
        var switchStatement = Assert.IsType<SwitchStatement>(unit.Functions[0].Body.Statements[1]);
        Assert.Equal("number", switchStatement.Cases[0].BindingName);
        Assert.Null(switchStatement.Cases[2].BindingName);
    }

    [Fact]
    public void ParsesIntegerSwitchCasesAndDefault()
    {
        var unit = Parse("i32 main() { switch (1) { case 0: return 10; case 1: return 11; default: return 12; } }");

        var switchStatement = Assert.IsType<SwitchStatement>(Assert.Single(unit.Functions[0].Body.Statements));
        Assert.Equal("0", Assert.IsType<IntegerLiteralExpression>(switchStatement.Cases[0].Value).Lexeme);
        Assert.True(switchStatement.Cases[2].IsDefault);
    }

    [Fact]
    public void ParsesTypePatternsWithOptionalBindings()
    {
        var unit = Parse(
            "class Base { } class Child : Base { } i32 main() { Base value = new Child(); " +
            "if (value is Child child) { return 1; } switch (value) { case is Child: return 2; default: return 0; } }");

        var condition = Assert.IsType<IsExpression>(
            Assert.IsType<IfStatement>(unit.Functions[0].Body.Statements[1]).Condition);
        Assert.Equal("Child", condition.TargetType.NamedType);
        Assert.Equal("child", condition.BindingName);
        var switchStatement = Assert.IsType<SwitchStatement>(unit.Functions[0].Body.Statements[2]);
        Assert.Equal("Child", switchStatement.Cases[0].PatternType!.NamedType);
        Assert.Null(switchStatement.Cases[0].BindingName);
    }

    [Fact]
    public void ParsesParametersDeclarationsAssignmentsAndCalls()
    {
        var unit = Parse("i32 add(i32 left, i32 right) { var result = left + right; result = print(result); return result; }");

        var function = Assert.Single(unit.Functions);
        Assert.Equal(2, function.Parameters.Count);
        Assert.IsType<VariableDeclarationStatement>(function.Body.Statements[0]);
        var assignment = Assert.IsType<AssignmentStatement>(function.Body.Statements[1]);
        var call = Assert.IsType<CallExpression>(assignment.Value);
        Assert.Equal("print", call.Name);
    }

    [Fact]
    public void ParsesThisMemberAccess()
    {
        var unit = Parse("class Box { i32 value; i32 get() { return this.value; } } i32 main() { return 0; }");

        var member = Assert.IsType<MemberAccessExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(unit.Classes[0].Methods[0].Body.Statements)).Value);
        Assert.IsType<ThisExpression>(member.Target);
        Assert.Equal("value", member.MemberName);
    }

    [Fact]
    public void ReportsMalformedSyntax()
    {
        var exception = Assert.Throws<ParseException>(() => Parse("i32 main() { return 1 }"));

        Assert.Equal(TokenKind.CloseBrace, exception.Token.Kind);
    }

    [Fact]
    public void ParsesNestedModulesImportsVisibilityAndQualifiedCalls()
    {
        var unit = Parse("public module foo.bar; import foo.bar.baz; public i32 main() { return bar.func(); }");

        Assert.Equal("foo.bar", unit.ModuleName);
        Assert.Equal(Visibility.Public, unit.ModuleVisibility);
        Assert.Equal("foo.bar.baz", Assert.Single(unit.Imports).Name);
        Assert.Equal(Visibility.Public, Assert.Single(unit.Functions).Visibility);
        var call = Assert.IsType<CallExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(Assert.Single(unit.Functions).Body.Statements)).Value);
        Assert.Equal("bar.func", call.Name);
    }

    [Fact]
    public void ParsesImportAliasesAndAsCasts()
    {
        var unit = Parse("import foo.bar as baz; i32 main() { return value as i32; }");

        var import = Assert.Single(unit.Imports);
        Assert.Equal("foo.bar", import.Name);
        Assert.Equal("baz", import.Alias);
        var cast = Assert.IsType<CastExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(Assert.Single(unit.Functions).Body.Statements)).Value);
        Assert.Equal(PrimitiveType.I32, cast.TargetType.Primitive);
    }

    [Fact]
    public void ParsesFunctionAnnotations()
    {
        var unit = Parse("@extern(\"puts\") @calling_convention(c) i32 main() { return 0; }");

        var annotations = Assert.Single(unit.Functions).DeclaredAnnotations;
        Assert.Equal(["extern", "calling_convention"], annotations.Select(annotation => annotation.Name));
        Assert.Equal(["\"puts\""], annotations[0].Arguments);
        Assert.Equal(["c"], annotations[1].Arguments);
    }

    [Fact]
    public void ParsesMultipleExternDeclarationsWithAnnotations()
    {
        var unit = Parse(
            """
            module std.io;
            @extern("oxygen_write_stdout")
            public extern void write_stdout(string value);
            @extern("oxygen_write_stderr")
            public extern void write_stderr(string value);
            public void print(string value) {
                write_stdout(value);
            }
            """);

        Assert.Equal(3, unit.Functions.Count);
        Assert.True(unit.Functions[0].IsExternalDeclaration);
        Assert.True(unit.Functions[1].IsExternalDeclaration);
    }

    [Fact]
    public void ParsesMultipleExternDeclarationsWithDiagnostics()
    {
        var result = new Parser(
                _tokenizer.Tokenize(
                    """
                    module std.io;
                    @extern("oxygen_write_stdout")
                    public extern void write_stdout(string value);
                    @extern("oxygen_write_stderr")
                    public extern void write_stderr(string value);
                    public void print(string value) {
                        write_stdout(value);
                    }
                    """),
                Log.Logger)
            .ParseWithDiagnostics("std/io.oxy");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(3, result.Unit.Functions.Count);
    }

    [Fact]
    public void ParsesExternFunctionDeclaration()
    {
        var unit = Parse("@extern(\"puts\") extern i32 puts(i32 value);");

        var function = Assert.Single(unit.Functions);
        Assert.True(function.IsExternalDeclaration);
        Assert.Empty(function.Body.Statements);
    }

    [Fact]
    public void ParsesExplicitStringType()
    {
        var unit = Parse("string main() { string value = \"hello\"; return value; }");

        var function = Assert.Single(unit.Functions);
        var declaration = Assert.IsType<VariableDeclarationStatement>(function.Body.Statements[0]);
        Assert.True(declaration.Type.IsString);
    }

    [Fact]
    public void ParsesPointerTypesAndDereferenceAssignments()
    {
        var unit = Parse("i32 main() { i32 value = 41; i32* pointer = &value; *pointer = 42; pointer[0] = 43; return pointer[0]; }");

        var declaration = Assert.IsType<VariableDeclarationStatement>(unit.Functions[0].Body.Statements[1]);
        Assert.True(declaration.Type.IsPointer);
        Assert.Equal(PrimitiveType.I32, declaration.Type.Pointee!.Primitive);
        var assignment = Assert.IsType<AssignmentStatement>(unit.Functions[0].Body.Statements[2]);
        Assert.IsType<UnaryExpression>(assignment.Target);
        Assert.IsType<IndexExpression>(
            Assert.IsType<AssignmentStatement>(unit.Functions[0].Body.Statements[3]).Target);
    }

    [Fact]
    public void ParsesGenericFunctionsAndExplicitTypeArguments()
    {
        var unit = Parse("i32 identity<T>(T value) { return 1; } i32 main() { return identity<i32>(2); }");

        var generic = unit.Functions[0];
        var parameter = Assert.Single(generic.DeclaredTypeParameters);
        Assert.Equal("T", parameter.Name);
        var call = Assert.IsType<CallExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(unit.Functions[1].Body.Statements)).Value);
        Assert.Equal(PrimitiveType.I32, Assert.Single(call.ExplicitTypeArguments).Primitive);
    }

    [Fact]
    public void ParsesTypeParameterConstraints()
    {
        var unit = Parse("void show<T : Printable, U>(T value, U other) { } i32 main() { return 0; }");

        var parameters = unit.Functions[0].DeclaredTypeParameters;
        Assert.Equal("Printable", parameters[0].Constraint);
        Assert.Null(parameters[1].Constraint);
    }

    [Fact]
    public void ParsesGenericCastSizeOfAndVoidPointerTypes()
    {
        var unit = Parse(
            "void* pointer<T>(T value) { return pointer; } i32 main() { i32 value = cast<i32>(sizeof<void*>()); return sizeof(value); }");

        Assert.True(unit.Functions[0].ReturnType.IsPointer);
        Assert.Equal(PrimitiveType.Void, unit.Functions[0].ReturnType.Pointee!.Primitive);
        var declaration = Assert.IsType<VariableDeclarationStatement>(unit.Functions[1].Body.Statements[0]);
        var cast = Assert.IsType<CastExpression>(declaration.Initializer);
        Assert.IsType<SizeOfExpression>(cast.Operand);
        var sizeOfValue = Assert.IsType<SizeOfExpression>(
            Assert.IsType<ReturnStatement>(unit.Functions[1].Body.Statements[1]).Value);
        Assert.NotNull(sizeOfValue.Operand);
    }

    [Fact]
    public void ParsesStructDeclarationsLiteralsAndMembers()
    {
        var unit = Parse(
            "struct Point { i32 x; i32 y; } i32 main() { Point point = Point { x: 1, y: 2 }; point.x = 3; return point.y; }");

        var structure = Assert.Single(unit.Structs);
        Assert.Equal("Point", structure.Name);
        Assert.Equal(["x", "y"], structure.Fields.Select(field => field.Name));
        var declaration = Assert.IsType<VariableDeclarationStatement>(unit.Functions[0].Body.Statements[0]);
        Assert.Equal("Point", declaration.Type.NamedType);
        Assert.IsType<StructLiteralExpression>(declaration.Initializer);
        Assert.IsType<MemberAccessExpression>(
            Assert.IsType<AssignmentStatement>(unit.Functions[0].Body.Statements[1]).Target);
    }

    [Fact]
    public void ParsesClassesInterfacesMembersConstructorsDestructorsAndNew()
    {
        var unit = Parse(
            "interface Printable { void print(i32 value); } " +
            "class Child : Base, Printable { i32 value; public Child(i32 initial) { value = initial; } " +
            "void print(i32 value) { } ~Child() { } } " +
            "i32 main() { var child = new Child(42); return 0; }");

        var @interface = Assert.Single(unit.Interfaces);
        Assert.Equal("Printable", @interface.Name);
        Assert.Equal("print", Assert.Single(@interface.Methods).Name);

        var @class = Assert.Single(unit.Classes);
        Assert.Equal("Base", @class.BaseType);
        Assert.Equal(["Printable"], @class.Interfaces);
        Assert.Equal("value", Assert.Single(@class.Fields).Name);
        Assert.Equal("Child", Assert.Single(@class.Constructors).ClassName);
        Assert.Equal("Child", @class.Destructor!.ClassName);
        Assert.Equal("print", Assert.Single(@class.Methods).Name);

        var declaration = Assert.IsType<VariableDeclarationStatement>(
            unit.Functions[0].Body.Statements[0]);
        var construction = Assert.IsType<NewExpression>(declaration.Initializer);
        Assert.Equal("Child", construction.Type.NamedType);
        Assert.Single(construction.Arguments);
    }

    [Fact]
    public void ParsesFixedArrayTypesLiteralsAndIndexing()
    {
        var unit = Parse("i32 main() { i32[3] values = { 1, 2, 3 }; values[1] = 4; return values[1]; }");

        var declaration = Assert.IsType<VariableDeclarationStatement>(unit.Functions[0].Body.Statements[0]);
        Assert.True(declaration.Type.IsArray);
        Assert.Equal(3, declaration.Type.ArrayLength);
        Assert.IsType<ArrayLiteralExpression>(declaration.Initializer);
        var assignment = Assert.IsType<AssignmentStatement>(unit.Functions[0].Body.Statements[1]);
        Assert.IsType<IndexExpression>(assignment.Target);
    }

    [Fact]
    public void ParsesControlFlowStatements()
    {
        var unit = Parse(
            "i32 main() { i32 value = 0; while (value < 10) { if (value == 5) { continue; } value = value + 1; } for (i32 i = 0; i < 3; i = i + 1) { if (i == 2) break; } return value; }");

        var body = unit.Functions[0].Body.Statements;
        Assert.IsType<WhileStatement>(body[1]);
        var loop = Assert.IsType<ForStatement>(body[2]);
        Assert.IsType<VariableDeclarationStatement>(loop.Initializer);
        Assert.IsType<AssignmentStatement>(loop.Increment);
        var whileBody = Assert.IsType<BlockStatement>(Assert.IsType<WhileStatement>(body[1]).Body);
        Assert.IsType<IfStatement>(whileBody.Statements[0]);
    }

    [Fact]
    public void ParsesPostfixIncrementAndVarAfterForLoop()
    {
        var unit = Parse(
            "i32 main() { i32[2] arr = { 34, 35 }; for (i32 i = 0; i < length(arr); i++) { } var ret = arr[1]; return ret; }");

        var body = unit.Functions[0].Body.Statements;
        var loop = Assert.IsType<ForStatement>(body[1]);
        var increment = Assert.IsType<AssignmentStatement>(loop.Increment);
        Assert.Equal("i", Assert.IsType<IdentifierExpression>(increment.Target).Name);
        Assert.IsType<BinaryExpression>(increment.Value);
        var inferred = Assert.IsType<VariableDeclarationStatement>(body[2]);
        Assert.True(inferred.Type.IsInferred);
    }

    [Fact]
    public void ParsesPrefixPostfixAndCompoundArithmetic()
    {
        var unit = Parse(
            "i32 main() { i32 value = 1; i32 before = value++; i32 after = ++value; value += 2; value <<= 1; return value; }");

        var statements = unit.Functions[0].Body.Statements;
        var before = Assert.IsType<VariableDeclarationStatement>(statements[1]);
        var postfix = Assert.IsType<UnaryExpression>(before.Initializer);
        Assert.True(postfix.IsPostfix);
        Assert.Equal(TokenKind.PlusPlus, postfix.Operator);
        var after = Assert.IsType<VariableDeclarationStatement>(statements[2]);
        Assert.Equal(TokenKind.PlusPlus, Assert.IsType<UnaryExpression>(after.Initializer).Operator);
        var compound = Assert.IsType<AssignmentStatement>(statements[3]);
        Assert.Equal(TokenKind.Plus, Assert.IsType<BinaryExpression>(compound.Value).Operator);
        var shift = Assert.IsType<AssignmentStatement>(statements[4]);
        Assert.Equal(TokenKind.LessThanLessThan, Assert.IsType<BinaryExpression>(shift.Value).Operator);
    }

    private CompilationUnit Parse(string source) =>
        new Parser(_tokenizer.Tokenize(source), Log.Logger).Parse();
}
