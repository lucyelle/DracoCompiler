using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Draco.Compiler.Api.Syntax;
using Draco.Compiler.Internal.Symbols;
using Draco.Compiler.Internal.Symbols.Source;
using Draco.Compiler.Internal.Symbols.Synthetized;

namespace Draco.Compiler.Internal.Binding;

/// <summary>
/// Utilities for binder logic.
/// </summary>
internal static class BinderFacts
{
    /// <summary>
    /// Retrieves the element type for a variadic argument type.
    /// </summary>
    /// <param name="type">The variadic argument type.</param>
    /// <param name="elementType">The element type, if <paramref name="type"/> is a legal variadic argument type.</param>
    /// <returns>True, if <paramref name="type"/> is a legal variadic argument type, false otherwise.</returns>
    public static bool TryGetVariadicElementType(TypeSymbol type, [MaybeNullWhen(false)] out TypeSymbol elementType)
    {
        if (type.GenericDefinition is ArrayTypeSymbol { Rank: 1 })
        {
            elementType = type.GenericArguments[0];
            return true;
        }

        elementType = null;
        return false;
    }

    /// <summary>
    /// Checks, if a given symbol can be referenced in a value-context.
    /// </summary>
    /// <param name="symbol">The symbol to check.</param>
    /// <returns>True, if <paramref name="symbol"/> can be referenced in a value-context.</returns>
    public static bool IsValueSymbol(Symbol symbol) => symbol
        is LocalSymbol
        or UntypedLocalSymbol
        or GlobalSymbol
        or FieldSymbol
        or PropertySymbol
        or FunctionSymbol
        or ModuleSymbol
        or TypeSymbol;

    /// <summary>
    /// Checks, if a given symbol can be referenced in a type-context.
    /// </summary>
    /// <param name="symbol">The symbol to check.</param>
    /// <returns>True, if <paramref name="symbol"/> can be referenced in a type-context.</returns>
    public static bool IsTypeSymbol(Symbol symbol) => symbol
        is TypeSymbol
        or ModuleSymbol;

    /// <summary>
    /// Checks, if a given symbol can be referenced in a label-context.
    /// </summary>
    /// <param name="symbol">The symbol to check.</param>
    /// <returns>True, if <paramref name="symbol"/> can be referenced in a label-context.</returns>
    public static bool IsLabelSymbol(Symbol symbol) => symbol
        is LabelSymbol;

    /// <summary>
    /// Checks, if a given symbol can be referenced in a non-type value context.
    /// </summary>
    /// <param name="symbol">The symbol to check.</param>
    /// <returns>True, if <paramref name="symbol"/> can be referenced in a non-type value context.</returns>
    public static bool IsNonTypeValueSymbol(Symbol symbol) => symbol
        is not TypeSymbol && IsValueSymbol(symbol);

    /// <summary>
    /// Retrieves the first scope defining ancestor of a given syntax node.
    /// </summary>
    /// <param name="node">The node to get the scope defining ancestor of.</param>
    /// <returns>The first scope defining ancestor of <paramref name="node"/>, or null, if there is no such ancestor.</returns>
    public static SyntaxNode? GetScopeDefiningAncestor(SyntaxNode? node)
    {
        if (node is null) return null;
        var result = node;
        while (!DefinesScope(result))
        {
            result = result.Parent;
            if (result is null) return null;
        }
        return result;
    }

    /// <summary>
    /// Enumerates the subtree that does not cross scope boundaries.
    /// </summary>
    /// <param name="tree">The subtree to enumerate.</param>
    /// <returns>The iterator over <paramref name="tree"/>.</returns>
    public static IEnumerable<SyntaxNode> EnumerateNodesInSameScope(SyntaxNode tree)
    {
        // We go through each child of the current tree
        foreach (var child in tree.Children)
        {
            // We yield the child first
            yield return child;

            // If the child defines a scope, we don't recurse
            if (DefinesScope(child)) continue;

            // Otherwise, we can recurse
            foreach (var item in EnumerateNodesInSameScope(child)) yield return item;
        }
    }

    /// <summary>
    /// Checks, if a given syntax node is responsible for defining a scope.
    /// </summary>
    /// <param name="node">The syntax node to check.</param>
    /// <returns>True, if <paramref name="node"/> defines its own scope.</returns>
    public static bool DefinesScope(SyntaxNode node) => node
        is CompilationUnitSyntax
        or FunctionDeclarationSyntax
        or ModuleDeclarationSyntax
        or FunctionBodySyntax
        or BlockExpressionSyntax
        or WhileExpressionSyntax;

    /// <summary>
    /// Checks, if a given syntax node defines a symbol.
    /// </summary>
    /// <param name="node">The syntax node to check.</param>
    /// <returns>True, if <paramref name="node"/> defines a symbol.</returns>
    public static bool DefinesSymbol(SyntaxNode node) => node
        is FunctionDeclarationSyntax
        or VariableDeclarationSyntax
        or ParameterSyntax
        or LabelDeclarationSyntax;

    /// <summary>
    /// Checks, if a given syntax node references a symbol.
    /// </summary>
    /// <param name="node">The syntax node to check.</param>
    /// <returns>True, if <paramref name="node"/> references a symbol.</returns>
    public static bool ReferencesSymbol(SyntaxNode node) => node
        is NameExpressionSyntax
        or NameTypeSyntax
        or NameLabelSyntax
        or MemberExpressionSyntax
        or ImportPathSyntax;

    /// <summary>
    /// Checks, that given syntax node must not reference a type.
    /// </summary>
    /// <param name="node">The syntax node to check.</param>
    /// <returns>True, if <paramref name="node"/> must not reference a type.</returns>
    public static bool SyntaxMustNotReferenceTypes(SyntaxNode node) =>
        node.Parent is CallExpressionSyntax || (node.Parent is GenericExpressionSyntax && node.Parent.Parent is CallExpressionSyntax);
}
