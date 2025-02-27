namespace Draco.Compiler.Internal.OptimizingIr.Model;

/// <summary>
/// Returns from the current procedure.
/// </summary>
internal sealed class RetInstruction : InstructionBase
{
    public override bool IsBranch => true;

    /// <summary>
    /// The returned value.
    /// </summary>
    public IOperand Value { get; set; }

    public RetInstruction(IOperand value)
    {
        this.Value = value;
    }

    public override string ToString() => $"ret {this.Value.ToOperandString()}";

    public override RetInstruction Clone() => new(this.Value);
}
