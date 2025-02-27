using Draco.Compiler.Internal.Symbols;

namespace Draco.Compiler.Internal.OptimizingIr.Model;

/// <summary>
/// Stores a value in a field.
/// </summary>
internal sealed class StoreFieldInstruction : InstructionBase
{
    /// <summary>
    /// The accessed object.
    /// </summary>
    public IOperand Receiver { get; set; }

    /// <summary>
    /// The accessed member.
    /// </summary>
    public FieldSymbol Member { get; set; }

    /// <summary>
    /// The operand to store the value of.
    /// </summary>
    public IOperand Source { get; set; }

    public StoreFieldInstruction(IOperand receiver, FieldSymbol member, IOperand source)
    {
        this.Receiver = receiver;
        this.Member = member;
        this.Source = source;
    }

    public override string ToString() =>
        $"store {this.Receiver.ToOperandString()}.{this.Member.Name} := {this.Source.ToOperandString()}";

    public override StoreFieldInstruction Clone() => new(this.Receiver, this.Member, this.Source);
}
