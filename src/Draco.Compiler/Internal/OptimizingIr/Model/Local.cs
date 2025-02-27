using System.Text;
using Draco.Compiler.Internal.Symbols;

namespace Draco.Compiler.Internal.OptimizingIr.Model;

/// <summary>
/// A local value that can be read from and written to.
/// </summary>
/// <param name="Symbol">The corresponding local symbol.</param>
/// <param name="Index">The index of this local to help naming.</param>
internal readonly record struct Local(LocalSymbol Symbol, int Index) : IOperand
{
    /// <summary>
    /// An optional name of this local.
    /// </summary>
    public string Name => this.Symbol.Name;

    /// <summary>
    /// The type this local holds.
    /// </summary>
    public TypeSymbol Type => this.Symbol.Type;

    public override string ToString()
    {
        var result = new StringBuilder();
        result.Append($"{this.ToOperandString()}: {this.Type}");
        if (!string.IsNullOrWhiteSpace(this.Name)) result.Append($" ; {this.Name}");
        return result.ToString();
    }
    public string ToOperandString() => $"loc{this.Index}";

    public bool Equals(Local other) => this.Symbol == other.Symbol;
    public override int GetHashCode() => this.Symbol.GetHashCode();
}
