using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Metadata;
using Draco.Compiler.Api;
using Draco.Compiler.Internal.Symbols.Synthetized;

namespace Draco.Compiler.Internal.Symbols.Metadata;

/// <summary>
/// Helper for decoding metadata blob-encoded types.
/// </summary>
internal sealed class TypeProvider : ISignatureTypeProvider<TypeSymbol, Symbol>, ICustomAttributeTypeProvider<TypeSymbol>
{
    // TODO: We return a special error type for now to swallow errors
    private static TypeSymbol UnknownType { get; } = new PrimitiveTypeSymbol("<unknown>", false);

    private WellKnownTypes WellKnownTypes => this.compilation.WellKnownTypes;

    private readonly Compilation compilation;

    public TypeProvider(Compilation compilation)
    {
        this.compilation = compilation;
    }

    public TypeSymbol GetArrayType(TypeSymbol elementType, ArrayShape shape) =>
        new ArrayTypeSymbol(shape.Rank).GenericInstantiate(elementType);
    public TypeSymbol GetSZArrayType(TypeSymbol elementType) =>
        IntrinsicSymbols.Array.GenericInstantiate(elementType);
    public TypeSymbol GetByReferenceType(TypeSymbol elementType) => UnknownType;
    public TypeSymbol GetFunctionPointerType(MethodSignature<TypeSymbol> signature) => UnknownType;
    public TypeSymbol GetGenericInstantiation(TypeSymbol genericType, ImmutableArray<TypeSymbol> typeArguments)
    {
        if (ReferenceEquals(genericType, UnknownType)) return UnknownType;
        return genericType.GenericInstantiate(genericType.ContainingSymbol, typeArguments);
    }
    public TypeSymbol GetGenericMethodParameter(Symbol genericContext, int index)
    {
        var methodAncestor = genericContext.AncestorChain
            .OfType<FunctionSymbol>()
            .First();

        return methodAncestor.IsGenericDefinition
            ? methodAncestor.GenericParameters[index]
            : methodAncestor.GenericDefinition!.GenericParameters[index];
    }
    public TypeSymbol GetGenericTypeParameter(Symbol genericContext, int index)
    {
        var typeAncestor = genericContext.AncestorChain
            .OfType<TypeSymbol>()
            .First();

        return typeAncestor.IsGenericDefinition
            ? typeAncestor.GenericParameters[index]
            : typeAncestor.GenericDefinition!.GenericParameters[index];
    }
    public TypeSymbol GetModifiedType(TypeSymbol modifier, TypeSymbol unmodifiedType, bool isRequired) => UnknownType;
    public TypeSymbol GetPinnedType(TypeSymbol elementType) => UnknownType;
    public TypeSymbol GetPointerType(TypeSymbol elementType) => UnknownType;
    public TypeSymbol GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => IntrinsicSymbols.Bool,
        PrimitiveTypeCode.Int32 => IntrinsicSymbols.Int32,
        PrimitiveTypeCode.String => IntrinsicSymbols.String,
        PrimitiveTypeCode.Void => IntrinsicSymbols.Unit,
        PrimitiveTypeCode.Object => IntrinsicSymbols.Object,
        _ => UnknownType
    };
    public TypeSymbol GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var definition = reader.GetTypeDefinition(handle);
        if (definition.IsNested)
        {
            // TODO
            return UnknownType;
        }

        var assemblyName = reader
            .GetAssemblyDefinition()
            .GetAssemblyName();

        // Type path
        var @namespace = reader.GetString(definition.Namespace);
        var name = reader.GetString(definition.Name);
        var fullName = string.IsNullOrWhiteSpace(@namespace) ? name : $"{@namespace}.{name}";
        var path = fullName.Split('.').ToImmutableArray();

        return this.WellKnownTypes.GetTypeFromAssembly(assemblyName, path);
    }
    public TypeSymbol GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var reference = reader.GetTypeReference(handle);
        var resolutionScope = reference.ResolutionScope;

        // TODO: Based on resolution scope, do the lookup
        return UnknownType;
    }
    public TypeSymbol GetTypeFromSpecification(MetadataReader reader, Symbol genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        UnknownType;
    public TypeSymbol GetSystemType() => this.WellKnownTypes.SystemType;
    public bool IsSystemType(TypeSymbol type) => ReferenceEquals(type, this.WellKnownTypes.SystemType);
    public TypeSymbol GetTypeFromSerializedName(string name) => UnknownType;
    public PrimitiveTypeCode GetUnderlyingEnumType(TypeSymbol type) => throw new System.ArgumentOutOfRangeException(nameof(type));
}
