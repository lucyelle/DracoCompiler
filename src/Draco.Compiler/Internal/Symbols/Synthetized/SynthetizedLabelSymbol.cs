namespace Draco.Compiler.Internal.Symbols.Synthetized;

/// <summary>
/// A label generated by the compiler.
/// </summary>
internal sealed class SynthetizedLabelSymbol : LabelSymbol
{
    public override string Name { get; }
    public override Symbol? ContainingSymbol => null;

    public SynthetizedLabelSymbol()
        : this(string.Empty)
    {
    }

    public SynthetizedLabelSymbol(string name)
    {
        this.Name = name;
    }
}
