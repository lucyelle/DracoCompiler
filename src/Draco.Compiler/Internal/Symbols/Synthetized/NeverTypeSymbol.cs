namespace Draco.Compiler.Internal.Symbols.Synthetized;

/// <summary>
/// Represents the type of an unreachable piece of code, also known as the bottom-type.
/// </summary>
internal sealed class NeverTypeSymbol : TypeSymbol
{
    /// <summary>
    /// A singleton instance.
    /// </summary>
    public static NeverTypeSymbol Instance { get; } = new();

    public override Symbol? ContainingSymbol => null;

    private NeverTypeSymbol()
    {
    }

    public override string ToString() => "<never>";
}
