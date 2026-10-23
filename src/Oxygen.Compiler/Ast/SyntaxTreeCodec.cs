using System.Numerics;
using System.Text;
using Oxygen.Compiler.Lexer;

namespace Oxygen.Compiler.Ast;

// The binary form of a syntax tree, as the parser produces it. A library carries the syntax of its generic declarations in
// this form, and the importing compiler reads it back into the same tree and runs the usual analysis on it. The form keeps
// no comments, formatting or original text. Each node has a fixed number (NodeKind), and the layout belongs to the format:
// a change to either needs a new FormatVersion.
public static class SyntaxTreeCodec
{
    public const int FormatVersion = 3;

    private static readonly byte[] Magic = "OXYAST"u8.ToArray();

    // Writes a unit, with the name of the source it was parsed from, which a diagnostic for the unit shows.
    public static byte[] Write(CompilationUnit unit, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(sourceName);
        var writer = new Writer();
        writer.Node(unit);
        return writer.Finish(sourceName);
    }

    public static DecodedUnit Read(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return new Reader(bytes.ToArray()).ReadFile();
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("The syntax tree is cut short.");
        }
    }

    // The format's node numbers. Append new kinds at the end and never renumber, since the numbers are part of the format.
    private enum NodeKind : byte
    {
        CompilationUnit = 1,
        GlobalVariableDeclaration,
        UnionCaseDeclaration,
        UnionDeclaration,
        EnumMemberDeclaration,
        EnumDeclaration,
        StructFieldDeclaration,
        StructDeclaration,
        ClassFieldDeclaration,
        PropertyAccessorDeclaration,
        PropertyDeclaration,
        ConstructorInitializer,
        ConstructorDeclaration,
        DestructorDeclaration,
        ClassDeclaration,
        InterfaceDeclaration,
        AnnotationSyntax,
        ImportSymbol,
        ImportDeclaration,
        FunctionDeclaration,
        TypeParameterSyntax,
        ParameterDeclaration,
        TypeSyntax,
        BlockStatement,
        VariableDeclarationStatement,
        AssignmentStatement,
        ReturnStatement,
        ExpressionStatement,
        IfStatement,
        WhileStatement,
        ForStatement,
        ForInStatement,
        BreakStatement,
        ContinueStatement,
        ThrowStatement,
        DeleteStatement,
        CatchClause,
        TryStatement,
        IdentifierExpression,
        ThisExpression,
        IntegerLiteralExpression,
        FloatingPointLiteralExpression,
        StringLiteralExpression,
        BooleanLiteralExpression,
        NullLiteralExpression,
        IsExpression,
        UnaryExpression,
        BinaryExpression,
        ConditionalExpression,
        CallExpression,
        StructLiteralExpression,
        NamedArgumentExpression,
        MemberAccessExpression,
        ArrayLiteralExpression,
        IndexExpression,
        CastExpression,
        LengthExpression,
        SizeOfExpression,
        UnionConstructionExpression,
        NewExpression,
        ClassConstructionExpression,
        SwitchCase,
        SwitchStatement,
        TypeAliasDeclaration,
        LambdaExpression,
        IndirectCallExpression,
        FunctionAddressExpression
    }

    private sealed class Writer
    {
        private readonly MemoryStream _body = new();
        private readonly BinaryWriter _out;
        private readonly List<string> _strings = [];
        private readonly Dictionary<string, int> _stringIndexes = new(StringComparer.Ordinal);

        public Writer() => _out = new BinaryWriter(_body, Encoding.UTF8, leaveOpen: true);

        // The header is written after the body, because the body is what fills the string table.
        public byte[] Finish(string sourceName)
        {
            var sourceIndex = IndexOf(sourceName);
            _out.Flush();

            var output = new MemoryStream();
            using (var header = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
            {
                header.Write(Magic);
                header.Write(FormatVersion);
                header.Write(sourceIndex);
                header.Write(_strings.Count);
                foreach (var text in _strings)
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    header.Write(bytes.Length);
                    header.Write(bytes);
                }
                header.Write(_body.ToArray());
            }
            return output.ToArray();
        }

        private int IndexOf(string text)
        {
            if (_stringIndexes.TryGetValue(text, out var index))
                return index;
            index = _strings.Count;
            _strings.Add(text);
            _stringIndexes[text] = index;
            return index;
        }

        private void Tag(NodeKind kind) => _out.Write((byte)kind);
        private void Int(int value) => _out.Write(value);
        private void Bool(bool value) => _out.Write(value);
        private void Str(string value) => Int(IndexOf(value));
        private void Enum<T>(T value) where T : struct, Enum => Int(Convert.ToInt32(value));
        private void Loc(SourceLocation location)
        {
            Int(location.Line);
            Int(location.Column);
        }

        private void OptStr(string? value)
        {
            Bool(value is not null);
            if (value is not null)
                Str(value);
        }

        private void OptInt(int? value)
        {
            Bool(value is not null);
            if (value is not null)
                Int(value.Value);
        }

        private void OptEnum<T>(T? value) where T : struct, Enum
        {
            Bool(value is not null);
            if (value is not null)
                Enum(value.Value);
        }

        private void OptNode(SyntaxNode? node)
        {
            Bool(node is not null);
            if (node is not null)
                Node(node);
        }

        private void Items<T>(IReadOnlyList<T> items, Action<T> write)
        {
            Int(items.Count);
            foreach (var item in items)
                write(item);
        }

        private void OptItems<T>(IReadOnlyList<T>? items, Action<T> write)
        {
            Bool(items is not null);
            if (items is not null)
                Items(items, write);
        }

        private void Big(BigInteger value)
        {
            var bytes = value.ToByteArray();
            Int(bytes.Length);
            _out.Write(bytes);
        }

        public void Node(SyntaxNode node)
        {
            switch (node)
            {
                case CompilationUnit n: Tag(NodeKind.CompilationUnit); Unit(n); break;
                case GlobalVariableDeclaration n: Tag(NodeKind.GlobalVariableDeclaration); Global(n); break;
                case TypeAliasDeclaration n: Tag(NodeKind.TypeAliasDeclaration); TypeAlias(n); break;
                case LambdaExpression n: Tag(NodeKind.LambdaExpression); Lambda(n); break;
                case IndirectCallExpression n: Tag(NodeKind.IndirectCallExpression); Indirect(n); break;
                case FunctionAddressExpression n: Tag(NodeKind.FunctionAddressExpression); FunctionAddress(n); break;
                case UnionCaseDeclaration n: Tag(NodeKind.UnionCaseDeclaration); UnionCase(n); break;
                case UnionDeclaration n: Tag(NodeKind.UnionDeclaration); Union(n); break;
                case EnumMemberDeclaration n: Tag(NodeKind.EnumMemberDeclaration); EnumMember(n); break;
                case EnumDeclaration n: Tag(NodeKind.EnumDeclaration); Enumeration(n); break;
                case StructFieldDeclaration n: Tag(NodeKind.StructFieldDeclaration); StructField(n); break;
                case StructDeclaration n: Tag(NodeKind.StructDeclaration); Structure(n); break;
                case ClassFieldDeclaration n: Tag(NodeKind.ClassFieldDeclaration); ClassField(n); break;
                case PropertyAccessorDeclaration n: Tag(NodeKind.PropertyAccessorDeclaration); Accessor(n); break;
                case PropertyDeclaration n: Tag(NodeKind.PropertyDeclaration); Property(n); break;
                case ConstructorInitializer n: Tag(NodeKind.ConstructorInitializer); Initializer(n); break;
                case ConstructorDeclaration n: Tag(NodeKind.ConstructorDeclaration); Constructor(n); break;
                case DestructorDeclaration n: Tag(NodeKind.DestructorDeclaration); Destructor(n); break;
                case ClassDeclaration n: Tag(NodeKind.ClassDeclaration); Class(n); break;
                case InterfaceDeclaration n: Tag(NodeKind.InterfaceDeclaration); Interface(n); break;
                case AnnotationSyntax n: Tag(NodeKind.AnnotationSyntax); Annotation(n); break;
                case ImportSymbol n: Tag(NodeKind.ImportSymbol); ImportSymbol(n); break;
                case ImportDeclaration n: Tag(NodeKind.ImportDeclaration); Import(n); break;
                case FunctionDeclaration n: Tag(NodeKind.FunctionDeclaration); Function(n); break;
                case TypeParameterSyntax n: Tag(NodeKind.TypeParameterSyntax); TypeParameter(n); break;
                case ParameterDeclaration n: Tag(NodeKind.ParameterDeclaration); Parameter(n); break;
                case TypeSyntax n: Tag(NodeKind.TypeSyntax); Type(n); break;
                case BlockStatement n: Tag(NodeKind.BlockStatement); Block(n); break;
                case VariableDeclarationStatement n: Tag(NodeKind.VariableDeclarationStatement); Variable(n); break;
                case AssignmentStatement n: Tag(NodeKind.AssignmentStatement); Assignment(n); break;
                case ReturnStatement n: Tag(NodeKind.ReturnStatement); Return(n); break;
                case ExpressionStatement n: Tag(NodeKind.ExpressionStatement); ExpressionStatement(n); break;
                case IfStatement n: Tag(NodeKind.IfStatement); If(n); break;
                case WhileStatement n: Tag(NodeKind.WhileStatement); While(n); break;
                case ForStatement n: Tag(NodeKind.ForStatement); For(n); break;
                case ForInStatement n: Tag(NodeKind.ForInStatement); ForIn(n); break;
                case BreakStatement n: Tag(NodeKind.BreakStatement); Loc(n.Location); break;
                case ContinueStatement n: Tag(NodeKind.ContinueStatement); Loc(n.Location); break;
                case ThrowStatement n: Tag(NodeKind.ThrowStatement); Throw(n); break;
                case DeleteStatement n: Tag(NodeKind.DeleteStatement); Delete(n); break;
                case CatchClause n: Tag(NodeKind.CatchClause); Catch(n); break;
                case TryStatement n: Tag(NodeKind.TryStatement); Try(n); break;
                case IdentifierExpression n: Tag(NodeKind.IdentifierExpression); Str(n.Name); Loc(n.Location); break;
                case ThisExpression n: Tag(NodeKind.ThisExpression); Loc(n.Location); break;
                case IntegerLiteralExpression n: Tag(NodeKind.IntegerLiteralExpression); Str(n.Lexeme); Loc(n.Location); break;
                case FloatingPointLiteralExpression n: Tag(NodeKind.FloatingPointLiteralExpression); Str(n.Lexeme); Loc(n.Location); break;
                case StringLiteralExpression n: Tag(NodeKind.StringLiteralExpression); Str(n.Lexeme); Loc(n.Location); break;
                case BooleanLiteralExpression n: Tag(NodeKind.BooleanLiteralExpression); Bool(n.Value); Loc(n.Location); break;
                case NullLiteralExpression n: Tag(NodeKind.NullLiteralExpression); Loc(n.Location); break;
                case IsExpression n: Tag(NodeKind.IsExpression); Is(n); break;
                case UnaryExpression n: Tag(NodeKind.UnaryExpression); Unary(n); break;
                case BinaryExpression n: Tag(NodeKind.BinaryExpression); Binary(n); break;
                case ConditionalExpression n: Tag(NodeKind.ConditionalExpression); Conditional(n); break;
                case CallExpression n: Tag(NodeKind.CallExpression); Call(n); break;
                case StructLiteralExpression n: Tag(NodeKind.StructLiteralExpression); StructLiteral(n); break;
                case NamedArgumentExpression n: Tag(NodeKind.NamedArgumentExpression); NamedArgument(n); break;
                case MemberAccessExpression n: Tag(NodeKind.MemberAccessExpression); MemberAccess(n); break;
                case ArrayLiteralExpression n: Tag(NodeKind.ArrayLiteralExpression); ArrayLiteral(n); break;
                case IndexExpression n: Tag(NodeKind.IndexExpression); Index(n); break;
                case CastExpression n: Tag(NodeKind.CastExpression); Cast(n); break;
                case LengthExpression n: Tag(NodeKind.LengthExpression); Length(n); break;
                case SizeOfExpression n: Tag(NodeKind.SizeOfExpression); SizeOf(n); break;
                case UnionConstructionExpression n: Tag(NodeKind.UnionConstructionExpression); UnionConstruction(n); break;
                case NewExpression n: Tag(NodeKind.NewExpression); New(n); break;
                case ClassConstructionExpression n: Tag(NodeKind.ClassConstructionExpression); ClassConstruction(n); break;
                case SwitchCase n: Tag(NodeKind.SwitchCase); Case(n); break;
                case SwitchStatement n: Tag(NodeKind.SwitchStatement); Switch(n); break;
                default:
                    throw new InvalidOperationException($"The syntax node '{node.GetType().Name}' has no binary form.");
            }
        }

        private void Unit(CompilationUnit n)
        {
            OptStr(n.ModuleName);
            Enum(n.ModuleVisibility);
            Items(n.Imports, Node);
            Items(n.Functions, Node);
            Loc(n.Location);
            Items(n.Structs, Node);
            Items(n.Enums, Node);
            Items(n.Unions, Node);
            Items(n.Classes, Node);
            Items(n.Interfaces, Node);
            Items(n.Globals, Node);
            Items(n.Aliases, Node);
        }

        private void TypeAlias(TypeAliasDeclaration n)
        {
            Str(n.Name);
            Node(n.Target);
            Enum(n.Visibility);
            Loc(n.Location);
        }

        private void Global(GlobalVariableDeclaration n)
        {
            Node(n.Type);
            Str(n.Name);
            Bool(n.IsConst);
            OptNode(n.Initializer);
            Loc(n.Location);
        }

        private void UnionCase(UnionCaseDeclaration n)
        {
            Str(n.Name);
            OptNode(n.PayloadType);
            Loc(n.Location);
        }

        private void Union(UnionDeclaration n)
        {
            Str(n.Name);
            Items(n.Cases, Node);
            Enum(n.Visibility);
            Loc(n.Location);
            OptItems(n.Annotations, Node);
            OptItems(n.TypeParameters, Node);
        }

        private void EnumMember(EnumMemberDeclaration n)
        {
            Str(n.Name);
            Bool(n.ExplicitValue is not null);
            if (n.ExplicitValue is not null)
                Big(n.ExplicitValue.Value);
            Loc(n.Location);
        }

        private void Enumeration(EnumDeclaration n)
        {
            Str(n.Name);
            Enum(n.UnderlyingType);
            Items(n.Members, Node);
            Enum(n.Visibility);
            Loc(n.Location);
            OptItems(n.Annotations, Node);
        }

        private void StructField(StructFieldDeclaration n)
        {
            Node(n.Type);
            Str(n.Name);
            Loc(n.Location);
        }

        private void Structure(StructDeclaration n)
        {
            Str(n.Name);
            Items(n.Fields, Node);
            Enum(n.Visibility);
            Loc(n.Location);
            OptItems(n.Annotations, Node);
            OptItems(n.TypeParameters, Node);
        }

        private void ClassField(ClassFieldDeclaration n)
        {
            Node(n.Type);
            Str(n.Name);
            Enum(n.Visibility);
            Loc(n.Location);
            OptNode(n.Initializer);
            Bool(n.IsConst);
            Bool(n.IsStatic);
        }

        private void Accessor(PropertyAccessorDeclaration n)
        {
            Enum(n.Kind);
            Enum(n.Visibility);
            OptNode(n.Body);
            Bool(n.IsAuto);
            Loc(n.Location);
        }

        private void Property(PropertyDeclaration n)
        {
            Node(n.Type);
            Str(n.Name);
            Enum(n.Visibility);
            Items(n.Accessors, Node);
            Loc(n.Location);
            Bool(n.IsVirtual);
            Bool(n.IsOverride);
        }

        private void Initializer(ConstructorInitializer n)
        {
            Enum(n.Kind);
            Items(n.Arguments, Node);
            Loc(n.Location);
        }

        private void Constructor(ConstructorDeclaration n)
        {
            Str(n.ClassName);
            Items(n.Parameters, Node);
            Node(n.Body);
            Enum(n.Visibility);
            Loc(n.Location);
            OptNode(n.Initializer);
            OptItems(n.Annotations, Node);
        }

        private void Destructor(DestructorDeclaration n)
        {
            Str(n.ClassName);
            Node(n.Body);
            Enum(n.Visibility);
            Loc(n.Location);
        }

        private void Class(ClassDeclaration n)
        {
            Str(n.Name);
            OptStr(n.BaseType);
            Items(n.Interfaces, Str);
            Items(n.Fields, Node);
            OptItems(n.Properties, Node);
            Items(n.Methods, Node);
            Items(n.Constructors, Node);
            OptNode(n.Destructor);
            Enum(n.Visibility);
            Loc(n.Location);
            OptItems(n.Annotations, Node);
            OptItems(n.TypeParameters, Node);
            OptItems(n.BaseTypeSyntax, Node);
            OptItems(n.InterfaceTypeSyntaxes, Node);
        }

        private void Interface(InterfaceDeclaration n)
        {
            Str(n.Name);
            Items(n.Methods, Node);
            OptItems(n.Properties, Node);
            Enum(n.Visibility);
            Loc(n.Location);
            OptItems(n.Annotations, Node);
            OptItems(n.TypeParameters, Node);
        }

        private void Annotation(AnnotationSyntax n)
        {
            Str(n.Name);
            Items(n.Arguments, Str);
            Loc(n.Location);
        }

        private void ImportSymbol(ImportSymbol n)
        {
            Str(n.Name);
            Str(n.LocalName);
            Loc(n.Location);
        }

        private void Import(ImportDeclaration n)
        {
            Str(n.Name);
            Loc(n.Location);
            OptStr(n.Alias);
            OptItems(n.Symbols, Node);
        }

        private void Function(FunctionDeclaration n)
        {
            Node(n.ReturnType);
            Str(n.Name);
            Items(n.Parameters, Node);
            Node(n.Body);
            Enum(n.Visibility);
            Loc(n.Location);
            OptItems(n.Annotations, Node);
            Bool(n.IsExternalDeclaration);
            OptItems(n.TypeParameters, Node);
            Bool(n.IsVirtual);
            Bool(n.IsOverride);
            Bool(n.IsStatic);
        }

        private void TypeParameter(TypeParameterSyntax n)
        {
            Str(n.Name);
            Loc(n.Location);
            Items(n.Constraints, Str);
        }

        private void Parameter(ParameterDeclaration n)
        {
            Node(n.Type);
            Str(n.Name);
            Loc(n.Location);
            OptNode(n.DefaultValue);
        }

        private void Type(TypeSyntax n)
        {
            OptEnum(n.Primitive);
            Bool(n.IsInferred);
            Loc(n.Location);
            Bool(n.IsString);
            OptNode(n.Pointee);
            OptStr(n.NamedType);
            OptInt(n.ArrayLength);
            OptNode(n.ElementType);
            OptItems(n.TypeArguments, Node);
            OptNode(n.FunctionReturn);
            Items(n.FunctionParameters, Node);
        }

        private void Indirect(IndirectCallExpression n)
        {
            Node(n.Callee);
            Items(n.Arguments, Node);
            Loc(n.Location);
        }

        private void FunctionAddress(FunctionAddressExpression n)
        {
            Str(n.Name);
            Items(n.TypeArguments, Node);
            Loc(n.Location);
        }

        private void Lambda(LambdaExpression n)
        {
            Node(n.ReturnType);
            Items(n.Parameters, Node);
            Node(n.Body);
            Loc(n.Location);
        }

        private void Block(BlockStatement n)
        {
            Items(n.Statements, Node);
            Loc(n.Location);
        }

        private void Variable(VariableDeclarationStatement n)
        {
            Node(n.Type);
            Str(n.Name);
            Bool(n.IsConst);
            OptNode(n.Initializer);
            Loc(n.Location);
        }

        private void Assignment(AssignmentStatement n)
        {
            Node(n.Target);
            Node(n.Value);
            Loc(n.Location);
            Enum(n.Operator);
        }

        private void Return(ReturnStatement n)
        {
            OptNode(n.Value);
            Loc(n.Location);
        }

        private void ExpressionStatement(ExpressionStatement n)
        {
            Node(n.Expression);
            Loc(n.Location);
        }

        private void If(IfStatement n)
        {
            Node(n.Condition);
            Node(n.Then);
            OptNode(n.Else);
            Loc(n.Location);
        }

        private void While(WhileStatement n)
        {
            Node(n.Condition);
            Node(n.Body);
            Loc(n.Location);
        }

        private void For(ForStatement n)
        {
            OptNode(n.Initializer);
            OptNode(n.Condition);
            OptNode(n.Increment);
            Node(n.Body);
            Loc(n.Location);
        }

        private void ForIn(ForInStatement n)
        {
            Str(n.VariableName);
            Node(n.VariableType);
            Node(n.Collection);
            Node(n.Body);
            Loc(n.Location);
        }

        private void Throw(ThrowStatement n)
        {
            OptNode(n.Value);
            Loc(n.Location);
        }

        private void Delete(DeleteStatement n)
        {
            Node(n.Target);
            Loc(n.Location);
        }

        private void Catch(CatchClause n)
        {
            OptNode(n.ExceptionType);
            OptStr(n.BindingName);
            Node(n.Body);
            Loc(n.Location);
        }

        private void Try(TryStatement n)
        {
            Node(n.Body);
            Items(n.Catches, Node);
            OptNode(n.Finally);
            Loc(n.Location);
        }

        private void Is(IsExpression n)
        {
            Node(n.Operand);
            Node(n.TargetType);
            OptStr(n.BindingName);
            Loc(n.Location);
        }

        private void Unary(UnaryExpression n)
        {
            Enum(n.Operator);
            Node(n.Operand);
            Loc(n.Location);
            Bool(n.IsPostfix);
        }

        private void Binary(BinaryExpression n)
        {
            Node(n.Left);
            Enum(n.Operator);
            Node(n.Right);
            Loc(n.Location);
        }

        private void Conditional(ConditionalExpression n)
        {
            Node(n.Condition);
            Node(n.WhenTrue);
            Node(n.WhenFalse);
            Loc(n.Location);
        }

        private void Call(CallExpression n)
        {
            Str(n.Name);
            Items(n.Arguments, Node);
            Loc(n.Location);
            OptItems(n.TypeArguments, Node);
            OptNode(n.Receiver);
            Bool(n.IsBareName);
        }

        private void StructLiteral(StructLiteralExpression n)
        {
            Str(n.TypeName);
            Items(n.Fields, Field);
            Loc(n.Location);
        }

        private void Field(StructLiteralField n)
        {
            Str(n.Name);
            Node(n.Value);
            Loc(n.Location);
        }

        private void NamedArgument(NamedArgumentExpression n)
        {
            Str(n.Name);
            Node(n.Value);
            Loc(n.Location);
        }

        private void MemberAccess(MemberAccessExpression n)
        {
            Node(n.Target);
            Str(n.MemberName);
            Loc(n.Location);
        }

        private void ArrayLiteral(ArrayLiteralExpression n)
        {
            Items(n.Elements, Node);
            Loc(n.Location);
        }

        private void Index(IndexExpression n)
        {
            Node(n.Target);
            Node(n.Index);
            Loc(n.Location);
        }

        private void Cast(CastExpression n)
        {
            Node(n.TargetType);
            Node(n.Operand);
            Loc(n.Location);
        }

        private void Length(LengthExpression n)
        {
            Node(n.Target);
            Loc(n.Location);
        }

        private void SizeOf(SizeOfExpression n)
        {
            OptNode(n.TargetType);
            OptNode(n.Operand);
            Loc(n.Location);
        }

        private void UnionConstruction(UnionConstructionExpression n)
        {
            Str(n.UnionName);
            Str(n.CaseName);
            Items(n.Arguments, Node);
            Loc(n.Location);
        }

        private void New(NewExpression n)
        {
            Node(n.Type);
            Items(n.Arguments, Node);
            Loc(n.Location);
        }

        private void ClassConstruction(ClassConstructionExpression n)
        {
            Str(n.TypeName);
            Items(n.Arguments, Node);
            Loc(n.Location);
            OptItems(n.TypeArguments, Node);
        }

        private void Case(SwitchCase n)
        {
            OptStr(n.CaseName);
            OptNode(n.Value);
            Bool(n.IsDefault);
            OptStr(n.BindingName);
            Node(n.Body);
            Loc(n.Location);
            OptNode(n.PatternType);
        }

        private void Switch(SwitchStatement n)
        {
            Node(n.Value);
            Items(n.Cases, Node);
            Loc(n.Location);
        }
    }

    private sealed class Reader
    {
        private readonly byte[] _bytes;
        private readonly BinaryReader _in;
        private string[] _strings = [];

        public Reader(byte[] bytes)
        {
            _bytes = bytes;
            _in = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8, leaveOpen: false);
        }

        public DecodedUnit ReadFile()
        {
            var magic = _in.ReadBytes(Magic.Length);
            if (!magic.AsSpan().SequenceEqual(Magic))
                throw new InvalidDataException("The data is not an Oxygen syntax tree.");
            var version = _in.ReadInt32();
            if (version != FormatVersion)
                throw new InvalidDataException(
                    $"Unsupported syntax tree format version {version}; this compiler reads version {FormatVersion}.");

            var sourceIndex = _in.ReadInt32();
            var count = _in.ReadInt32();
            if (count < 0 || count > _bytes.Length)
                throw new InvalidDataException("The syntax tree has an invalid string table.");
            _strings = new string[count];
            for (var index = 0; index < count; index++)
            {
                var length = _in.ReadInt32();
                if (length < 0 || length > _bytes.Length)
                    throw new InvalidDataException("The syntax tree has an invalid string.");
                _strings[index] = Encoding.UTF8.GetString(_in.ReadBytes(length));
            }

            var unit = Node() as CompilationUnit
                ?? throw new InvalidDataException("The syntax tree does not start with a compilation unit.");
            if (_in.BaseStream.Position != _in.BaseStream.Length)
                throw new InvalidDataException("The syntax tree has data after its last node.");
            return new DecodedUnit(unit, Text(sourceIndex));
        }

        private string Text(int index)
        {
            if (index < 0 || index >= _strings.Length)
                throw new InvalidDataException("The syntax tree refers to a string that it does not have.");
            return _strings[index];
        }

        private int Int() => _in.ReadInt32();
        private bool Bool() => _in.ReadBoolean();
        private string Str() => Text(Int());
        private T Enum<T>() where T : struct, Enum
        {
            var value = (T)System.Enum.ToObject(typeof(T), Int());
            if (!System.Enum.IsDefined(value))
                throw new InvalidDataException($"The syntax tree has an invalid {typeof(T).Name} value.");
            return value;
        }

        private SourceLocation Loc() => new(Int(), Int());
        private string? OptStr() => Bool() ? Str() : null;
        private int? OptInt() => Bool() ? Int() : null;
        private T? OptEnum<T>() where T : struct, Enum => Bool() ? Enum<T>() : null;
        private T? OptNode<T>() where T : SyntaxNode => Bool() ? Typed<T>() : null;

        private T Typed<T>() where T : SyntaxNode =>
            Node() as T ?? throw new InvalidDataException($"The syntax tree has a node where a {typeof(T).Name} belongs.");

        // Lists are read as arrays, which is how the parser builds them.
        private T[] Items<T>(Func<T> read)
        {
            var count = Int();
            if (count < 0 || count > _bytes.Length)
                throw new InvalidDataException("The syntax tree has an invalid list length.");
            var items = new T[count];
            for (var index = 0; index < count; index++)
                items[index] = read();
            return items;
        }

        private IReadOnlyList<T>? OptItems<T>(Func<T> read) => Bool() ? Items(read) : null;

        private BigInteger Big()
        {
            var length = Int();
            if (length < 0 || length > _bytes.Length)
                throw new InvalidDataException("The syntax tree has an invalid integer.");
            return new BigInteger(_in.ReadBytes(length));
        }

        private SyntaxNode Node()
        {
            var kind = (NodeKind)_in.ReadByte();
            return kind switch
            {
                NodeKind.CompilationUnit => ReadUnit(),
                NodeKind.GlobalVariableDeclaration => ReadGlobal(),
                NodeKind.TypeAliasDeclaration => ReadTypeAlias(),
                NodeKind.LambdaExpression => ReadLambda(),
                NodeKind.IndirectCallExpression => ReadIndirect(),
                NodeKind.FunctionAddressExpression => ReadFunctionAddress(),
                NodeKind.UnionCaseDeclaration => ReadUnionCase(),
                NodeKind.UnionDeclaration => ReadUnion(),
                NodeKind.EnumMemberDeclaration => ReadEnumMember(),
                NodeKind.EnumDeclaration => ReadEnumeration(),
                NodeKind.StructFieldDeclaration => ReadStructField(),
                NodeKind.StructDeclaration => ReadStructure(),
                NodeKind.ClassFieldDeclaration => ReadClassField(),
                NodeKind.PropertyAccessorDeclaration => ReadAccessor(),
                NodeKind.PropertyDeclaration => ReadProperty(),
                NodeKind.ConstructorInitializer => ReadInitializer(),
                NodeKind.ConstructorDeclaration => ReadConstructor(),
                NodeKind.DestructorDeclaration => ReadDestructor(),
                NodeKind.ClassDeclaration => ReadClass(),
                NodeKind.InterfaceDeclaration => ReadInterface(),
                NodeKind.AnnotationSyntax => ReadAnnotation(),
                NodeKind.ImportSymbol => ReadImportSymbol(),
                NodeKind.ImportDeclaration => ReadImport(),
                NodeKind.FunctionDeclaration => ReadFunction(),
                NodeKind.TypeParameterSyntax => ReadTypeParameter(),
                NodeKind.ParameterDeclaration => ReadParameter(),
                NodeKind.TypeSyntax => ReadType(),
                NodeKind.BlockStatement => ReadBlock(),
                NodeKind.VariableDeclarationStatement => ReadVariable(),
                NodeKind.AssignmentStatement => ReadAssignment(),
                NodeKind.ReturnStatement => ReadReturn(),
                NodeKind.ExpressionStatement => ReadExpressionStatement(),
                NodeKind.IfStatement => ReadIf(),
                NodeKind.WhileStatement => ReadWhile(),
                NodeKind.ForStatement => ReadFor(),
                NodeKind.ForInStatement => ReadForIn(),
                NodeKind.BreakStatement => new BreakStatement(Loc()),
                NodeKind.ContinueStatement => new ContinueStatement(Loc()),
                NodeKind.ThrowStatement => ReadThrow(),
                NodeKind.DeleteStatement => ReadDelete(),
                NodeKind.CatchClause => ReadCatch(),
                NodeKind.TryStatement => ReadTry(),
                NodeKind.IdentifierExpression => new IdentifierExpression(Str(), Loc()),
                NodeKind.ThisExpression => new ThisExpression(Loc()),
                NodeKind.IntegerLiteralExpression => new IntegerLiteralExpression(Str(), Loc()),
                NodeKind.FloatingPointLiteralExpression => new FloatingPointLiteralExpression(Str(), Loc()),
                NodeKind.StringLiteralExpression => new StringLiteralExpression(Str(), Loc()),
                NodeKind.BooleanLiteralExpression => new BooleanLiteralExpression(Bool(), Loc()),
                NodeKind.NullLiteralExpression => new NullLiteralExpression(Loc()),
                NodeKind.IsExpression => ReadIs(),
                NodeKind.UnaryExpression => ReadUnary(),
                NodeKind.BinaryExpression => ReadBinary(),
                NodeKind.ConditionalExpression => ReadConditional(),
                NodeKind.CallExpression => ReadCall(),
                NodeKind.StructLiteralExpression => ReadStructLiteral(),
                NodeKind.NamedArgumentExpression => ReadNamedArgument(),
                NodeKind.MemberAccessExpression => ReadMemberAccess(),
                NodeKind.ArrayLiteralExpression => ReadArrayLiteral(),
                NodeKind.IndexExpression => ReadIndex(),
                NodeKind.CastExpression => ReadCast(),
                NodeKind.LengthExpression => ReadLength(),
                NodeKind.SizeOfExpression => ReadSizeOf(),
                NodeKind.UnionConstructionExpression => ReadUnionConstruction(),
                NodeKind.NewExpression => ReadNew(),
                NodeKind.ClassConstructionExpression => ReadClassConstruction(),
                NodeKind.SwitchCase => ReadCase(),
                NodeKind.SwitchStatement => ReadSwitch(),
                _ => throw new InvalidDataException($"The syntax tree has an unknown node kind {(byte)kind}.")
            };
        }

        private CompilationUnit ReadUnit()
        {
            var moduleName = OptStr();
            var moduleVisibility = Enum<Visibility>();
            var imports = Items(Typed<ImportDeclaration>);
            var functions = Items(Typed<FunctionDeclaration>);
            var location = Loc();
            return new CompilationUnit(moduleName, moduleVisibility, imports, functions, location)
            {
                Structs = Items(Typed<StructDeclaration>),
                Enums = Items(Typed<EnumDeclaration>),
                Unions = Items(Typed<UnionDeclaration>),
                Classes = Items(Typed<ClassDeclaration>),
                Interfaces = Items(Typed<InterfaceDeclaration>),
                Globals = Items(Typed<GlobalVariableDeclaration>),
                Aliases = Items(Typed<TypeAliasDeclaration>)
            };
        }

        private TypeAliasDeclaration ReadTypeAlias()
        {
            var name = Str();
            var target = Typed<TypeSyntax>();
            var visibility = Enum<Visibility>();
            return new TypeAliasDeclaration(name, target, visibility, Loc());
        }

        private GlobalVariableDeclaration ReadGlobal()
        {
            var type = Typed<TypeSyntax>();
            var name = Str();
            var isConst = Bool();
            var initializer = OptNode<Expression>();
            return new GlobalVariableDeclaration(type, name, isConst, initializer, Loc());
        }

        private UnionCaseDeclaration ReadUnionCase()
        {
            var name = Str();
            var payload = OptNode<TypeSyntax>();
            return new UnionCaseDeclaration(name, payload, Loc());
        }

        private UnionDeclaration ReadUnion()
        {
            var name = Str();
            var cases = Items(Typed<UnionCaseDeclaration>);
            var visibility = Enum<Visibility>();
            var location = Loc();
            var annotations = OptItems(Typed<AnnotationSyntax>);
            var typeParameters = OptItems(Typed<TypeParameterSyntax>);
            return new UnionDeclaration(name, cases, visibility, location, annotations, typeParameters);
        }

        private EnumMemberDeclaration ReadEnumMember()
        {
            var name = Str();
            var value = Bool() ? (BigInteger?)Big() : null;
            return new EnumMemberDeclaration(name, value, Loc());
        }

        private EnumDeclaration ReadEnumeration()
        {
            var name = Str();
            var underlying = Enum<PrimitiveType>();
            var members = Items(Typed<EnumMemberDeclaration>);
            var visibility = Enum<Visibility>();
            var location = Loc();
            var annotations = OptItems(Typed<AnnotationSyntax>);
            return new EnumDeclaration(name, underlying, members, visibility, location, annotations);
        }

        private StructFieldDeclaration ReadStructField()
        {
            var type = Typed<TypeSyntax>();
            var name = Str();
            return new StructFieldDeclaration(type, name, Loc());
        }

        private StructDeclaration ReadStructure()
        {
            var name = Str();
            var fields = Items(Typed<StructFieldDeclaration>);
            var visibility = Enum<Visibility>();
            var location = Loc();
            var annotations = OptItems(Typed<AnnotationSyntax>);
            var typeParameters = OptItems(Typed<TypeParameterSyntax>);
            return new StructDeclaration(name, fields, visibility, location, annotations, typeParameters);
        }

        private ClassFieldDeclaration ReadClassField()
        {
            var type = Typed<TypeSyntax>();
            var name = Str();
            var visibility = Enum<Visibility>();
            var location = Loc();
            var initializer = OptNode<Expression>();
            var isConst = Bool();
            var isStatic = Bool();
            return new ClassFieldDeclaration(type, name, visibility, location, initializer, isConst, isStatic);
        }

        private PropertyAccessorDeclaration ReadAccessor()
        {
            var kind = Enum<PropertyAccessorKind>();
            var visibility = Enum<Visibility>();
            var body = OptNode<BlockStatement>();
            var isAuto = Bool();
            return new PropertyAccessorDeclaration(kind, visibility, body, isAuto, Loc());
        }

        private PropertyDeclaration ReadProperty()
        {
            var type = Typed<TypeSyntax>();
            var name = Str();
            var visibility = Enum<Visibility>();
            var accessors = Items(Typed<PropertyAccessorDeclaration>);
            var location = Loc();
            var isVirtual = Bool();
            var isOverride = Bool();
            return new PropertyDeclaration(type, name, visibility, accessors, location, isVirtual, isOverride);
        }

        private ConstructorInitializer ReadInitializer()
        {
            var kind = Enum<ConstructorInitializerKind>();
            var arguments = Items(Typed<Expression>);
            return new ConstructorInitializer(kind, arguments, Loc());
        }

        private ConstructorDeclaration ReadConstructor()
        {
            var className = Str();
            var parameters = Items(Typed<ParameterDeclaration>);
            var body = Typed<BlockStatement>();
            var visibility = Enum<Visibility>();
            var location = Loc();
            var initializer = OptNode<ConstructorInitializer>();
            var annotations = OptItems(Typed<AnnotationSyntax>);
            return new ConstructorDeclaration(className, parameters, body, visibility, location)
            {
                Initializer = initializer,
                Annotations = annotations
            };
        }

        private DestructorDeclaration ReadDestructor()
        {
            var className = Str();
            var body = Typed<BlockStatement>();
            var visibility = Enum<Visibility>();
            return new DestructorDeclaration(className, body, visibility, Loc());
        }

        private ClassDeclaration ReadClass()
        {
            var name = Str();
            var baseType = OptStr();
            var interfaces = Items(Str);
            var fields = Items(Typed<ClassFieldDeclaration>);
            var properties = OptItems(Typed<PropertyDeclaration>);
            var methods = Items(Typed<FunctionDeclaration>);
            var constructors = Items(Typed<ConstructorDeclaration>);
            var destructor = OptNode<DestructorDeclaration>();
            var visibility = Enum<Visibility>();
            var location = Loc();
            var annotations = OptItems(Typed<AnnotationSyntax>);
            var typeParameters = OptItems(Typed<TypeParameterSyntax>);
            return new ClassDeclaration(name, baseType, interfaces, fields, properties, methods, constructors, destructor,
                visibility, location, annotations, typeParameters)
            {
                BaseTypeSyntax = OptItems(Typed<TypeSyntax>),
                InterfaceTypeSyntaxes = OptItems(Typed<TypeSyntax>)
            };
        }

        private InterfaceDeclaration ReadInterface()
        {
            var name = Str();
            var methods = Items(Typed<FunctionDeclaration>);
            var properties = OptItems(Typed<PropertyDeclaration>);
            var visibility = Enum<Visibility>();
            var location = Loc();
            var annotations = OptItems(Typed<AnnotationSyntax>);
            var typeParameters = OptItems(Typed<TypeParameterSyntax>);
            return new InterfaceDeclaration(name, methods, properties, visibility, location, annotations, typeParameters);
        }

        private AnnotationSyntax ReadAnnotation()
        {
            var name = Str();
            var arguments = Items(Str);
            return new AnnotationSyntax(name, arguments, Loc());
        }

        private ImportSymbol ReadImportSymbol()
        {
            var name = Str();
            var localName = Str();
            return new ImportSymbol(name, localName, Loc());
        }

        private ImportDeclaration ReadImport()
        {
            var name = Str();
            var location = Loc();
            var alias = OptStr();
            var symbols = OptItems(Typed<ImportSymbol>);
            return new ImportDeclaration(name, location, alias, symbols);
        }

        private FunctionDeclaration ReadFunction()
        {
            var returnType = Typed<TypeSyntax>();
            var name = Str();
            var parameters = Items(Typed<ParameterDeclaration>);
            var body = Typed<BlockStatement>();
            var visibility = Enum<Visibility>();
            var location = Loc();
            var annotations = OptItems(Typed<AnnotationSyntax>);
            var isExternal = Bool();
            var typeParameters = OptItems(Typed<TypeParameterSyntax>);
            var isVirtual = Bool();
            var isOverride = Bool();
            var isStatic = Bool();
            return new FunctionDeclaration(returnType, name, parameters, body, visibility, location, annotations, isExternal,
                typeParameters, isVirtual, isOverride, isStatic);
        }

        private TypeParameterSyntax ReadTypeParameter()
        {
            var name = Str();
            var location = Loc();
            return new TypeParameterSyntax(name, location) { Constraints = Items(Str) };
        }

        private ParameterDeclaration ReadParameter()
        {
            var type = Typed<TypeSyntax>();
            var name = Str();
            var location = Loc();
            return new ParameterDeclaration(type, name, location, OptNode<Expression>());
        }

        private TypeSyntax ReadType()
        {
            var primitive = OptEnum<PrimitiveType>();
            var isInferred = Bool();
            var location = Loc();
            var isString = Bool();
            var pointee = OptNode<TypeSyntax>();
            var namedType = OptStr();
            var arrayLength = OptInt();
            var elementType = OptNode<TypeSyntax>();
            var typeArguments = OptItems(Typed<TypeSyntax>);
            var functionReturn = OptNode<TypeSyntax>();
            var functionParameters = Items(Typed<TypeSyntax>);
            return new TypeSyntax(primitive, isInferred, location, isString, pointee, namedType, arrayLength, elementType,
                typeArguments) { FunctionReturn = functionReturn, FunctionParameters = functionParameters };
        }

        private IndirectCallExpression ReadIndirect()
        {
            var callee = Typed<Expression>();
            var arguments = Items(Typed<Expression>);
            return new IndirectCallExpression(callee, arguments, Loc());
        }

        private FunctionAddressExpression ReadFunctionAddress()
        {
            var name = Str();
            var typeArguments = Items(Typed<TypeSyntax>);
            return new FunctionAddressExpression(name, typeArguments, Loc());
        }

        private LambdaExpression ReadLambda()
        {
            var returnType = Typed<TypeSyntax>();
            var parameters = Items(Typed<ParameterDeclaration>);
            var body = Typed<BlockStatement>();
            return new LambdaExpression(returnType, parameters, body, Loc());
        }

        private BlockStatement ReadBlock()
        {
            var statements = Items(Typed<Statement>);
            return new BlockStatement(statements, Loc());
        }

        private VariableDeclarationStatement ReadVariable()
        {
            var type = Typed<TypeSyntax>();
            var name = Str();
            var isConst = Bool();
            var initializer = OptNode<Expression>();
            return new VariableDeclarationStatement(type, name, isConst, initializer, Loc());
        }

        private AssignmentStatement ReadAssignment()
        {
            var target = Typed<Expression>();
            var value = Typed<Expression>();
            var location = Loc();
            var operatorKind = Enum<TokenKind>();
            return new AssignmentStatement(target, value, location, operatorKind);
        }

        private ReturnStatement ReadReturn()
        {
            var value = OptNode<Expression>();
            return new ReturnStatement(value, Loc());
        }

        private ExpressionStatement ReadExpressionStatement()
        {
            var expression = Typed<Expression>();
            return new ExpressionStatement(expression, Loc());
        }

        private IfStatement ReadIf()
        {
            var condition = Typed<Expression>();
            var then = Typed<Statement>();
            var otherwise = OptNode<Statement>();
            return new IfStatement(condition, then, otherwise, Loc());
        }

        private WhileStatement ReadWhile()
        {
            var condition = Typed<Expression>();
            var body = Typed<Statement>();
            return new WhileStatement(condition, body, Loc());
        }

        private ForStatement ReadFor()
        {
            var initializer = OptNode<Statement>();
            var condition = OptNode<Expression>();
            var increment = OptNode<Statement>();
            var body = Typed<Statement>();
            return new ForStatement(initializer, condition, increment, body, Loc());
        }

        private ForInStatement ReadForIn()
        {
            var variableName = Str();
            var variableType = Typed<TypeSyntax>();
            var collection = Typed<Expression>();
            var body = Typed<Statement>();
            return new ForInStatement(variableName, variableType, collection, body, Loc());
        }

        private ThrowStatement ReadThrow()
        {
            var value = OptNode<Expression>();
            return new ThrowStatement(value, Loc());
        }

        private DeleteStatement ReadDelete()
        {
            var target = Typed<Expression>();
            return new DeleteStatement(target, Loc());
        }

        private CatchClause ReadCatch()
        {
            var exceptionType = OptNode<TypeSyntax>();
            var bindingName = OptStr();
            var body = Typed<BlockStatement>();
            return new CatchClause(exceptionType, bindingName, body, Loc());
        }

        private TryStatement ReadTry()
        {
            var body = Typed<BlockStatement>();
            var catches = Items(Typed<CatchClause>);
            var finallyBlock = OptNode<BlockStatement>();
            return new TryStatement(body, catches, finallyBlock, Loc());
        }

        private IsExpression ReadIs()
        {
            var operand = Typed<Expression>();
            var targetType = Typed<TypeSyntax>();
            var bindingName = OptStr();
            return new IsExpression(operand, targetType, bindingName, Loc());
        }

        private UnaryExpression ReadUnary()
        {
            var operatorKind = Enum<TokenKind>();
            var operand = Typed<Expression>();
            var location = Loc();
            return new UnaryExpression(operatorKind, operand, location, Bool());
        }

        private BinaryExpression ReadBinary()
        {
            var left = Typed<Expression>();
            var operatorKind = Enum<TokenKind>();
            var right = Typed<Expression>();
            return new BinaryExpression(left, operatorKind, right, Loc());
        }

        private ConditionalExpression ReadConditional()
        {
            var condition = Typed<Expression>();
            var whenTrue = Typed<Expression>();
            var whenFalse = Typed<Expression>();
            return new ConditionalExpression(condition, whenTrue, whenFalse, Loc());
        }

        private CallExpression ReadCall()
        {
            var name = Str();
            var arguments = Items(Typed<Expression>);
            var location = Loc();
            var typeArguments = OptItems(Typed<TypeSyntax>);
            var receiver = OptNode<Expression>();
            var isBareName = Bool();
            return new CallExpression(name, arguments, location, typeArguments, receiver) { IsBareName = isBareName };
        }

        private StructLiteralExpression ReadStructLiteral()
        {
            var typeName = Str();
            var fields = Items(ReadStructLiteralField);
            return new StructLiteralExpression(typeName, fields, Loc());
        }

        private StructLiteralField ReadStructLiteralField()
        {
            var name = Str();
            var value = Typed<Expression>();
            return new StructLiteralField(name, value, Loc());
        }

        private NamedArgumentExpression ReadNamedArgument()
        {
            var name = Str();
            var value = Typed<Expression>();
            return new NamedArgumentExpression(name, value, Loc());
        }

        private MemberAccessExpression ReadMemberAccess()
        {
            var target = Typed<Expression>();
            var memberName = Str();
            return new MemberAccessExpression(target, memberName, Loc());
        }

        private ArrayLiteralExpression ReadArrayLiteral()
        {
            var elements = Items(Typed<Expression>);
            return new ArrayLiteralExpression(elements, Loc());
        }

        private IndexExpression ReadIndex()
        {
            var target = Typed<Expression>();
            var index = Typed<Expression>();
            return new IndexExpression(target, index, Loc());
        }

        private CastExpression ReadCast()
        {
            var targetType = Typed<TypeSyntax>();
            var operand = Typed<Expression>();
            return new CastExpression(targetType, operand, Loc());
        }

        private LengthExpression ReadLength()
        {
            var target = Typed<Expression>();
            return new LengthExpression(target, Loc());
        }

        private SizeOfExpression ReadSizeOf()
        {
            var targetType = OptNode<TypeSyntax>();
            var operand = OptNode<Expression>();
            return new SizeOfExpression(targetType, operand, Loc());
        }

        private UnionConstructionExpression ReadUnionConstruction()
        {
            var unionName = Str();
            var caseName = Str();
            var arguments = Items(Typed<Expression>);
            return new UnionConstructionExpression(unionName, caseName, arguments, Loc());
        }

        private NewExpression ReadNew()
        {
            var type = Typed<TypeSyntax>();
            var arguments = Items(Typed<Expression>);
            return new NewExpression(type, arguments, Loc());
        }

        private ClassConstructionExpression ReadClassConstruction()
        {
            var typeName = Str();
            var arguments = Items(Typed<Expression>);
            var location = Loc();
            var typeArguments = OptItems(Typed<TypeSyntax>);
            return new ClassConstructionExpression(typeName, arguments, location, typeArguments);
        }

        private SwitchCase ReadCase()
        {
            var caseName = OptStr();
            var value = OptNode<Expression>();
            var isDefault = Bool();
            var bindingName = OptStr();
            var body = Typed<Statement>();
            var location = Loc();
            return new SwitchCase(caseName, value, isDefault, bindingName, body, location, OptNode<TypeSyntax>());
        }

        private SwitchStatement ReadSwitch()
        {
            var value = Typed<Expression>();
            var cases = Items(Typed<SwitchCase>);
            return new SwitchStatement(value, cases, Loc());
        }
    }
}

// A unit read back from the binary form, with the name of its source.
public sealed record DecodedUnit(CompilationUnit Unit, string SourceName);
