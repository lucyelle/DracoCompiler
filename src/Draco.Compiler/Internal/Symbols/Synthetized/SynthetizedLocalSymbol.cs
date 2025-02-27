namespace Draco.Compiler.Internal.Symbols.Synthetized;

/// <summary>
/// A local generated by the compiler.
/// </summary>
internal sealed class SynthetizedLocalSymbol : LocalSymbol
{
    public override TypeSymbol Type { get; }
    public override bool IsMutable { get; }
    public override Symbol? ContainingSymbol => null;

    public SynthetizedLocalSymbol(TypeSymbol type, bool isMutable)
    {
        this.Type = type;
        this.IsMutable = isMutable;
    }
}
