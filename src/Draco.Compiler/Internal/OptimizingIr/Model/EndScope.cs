namespace Draco.Compiler.Internal.OptimizingIr.Model;

/// <summary>
/// A pseudo-instruction for representing the end of a local scope.
/// </summary>
internal sealed class EndScope : InstructionBase
{
    public override bool IsValidInUnreachableContext => true;

    public override EndScope Clone() => new();

    public override string ToString() => "@scope end";
}
