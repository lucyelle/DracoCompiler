using System.Linq;
using Draco.Compiler.Internal.BoundTree;
using Draco.Compiler.Internal.OptimizingIr.Model;
using Draco.Compiler.Internal.Symbols;
using Draco.Compiler.Internal.Symbols.Generic;
using Draco.Compiler.Internal.Symbols.Metadata;
using Draco.Compiler.Internal.Symbols.Source;
using Draco.Compiler.Internal.Symbols.Synthetized;
using static Draco.Compiler.Internal.OptimizingIr.InstructionFactory;

namespace Draco.Compiler.Internal.OptimizingIr;

/// <summary>
/// Generates IR code on function-local level.
/// </summary>
internal sealed partial class FunctionBodyCodegen : BoundTreeVisitor<IOperand>
{
    private readonly Procedure procedure;
    private BasicBlock currentBasicBlock;
    private bool isDetached;
    private int blockIndex = 0;

    public FunctionBodyCodegen(Procedure procedure)
    {
        this.procedure = procedure;
        // NOTE: Attach block takes care of the null
        this.currentBasicBlock = default!;
        this.AttachBlock(procedure.Entry);
    }

    private void Compile(BoundStatement stmt) => stmt.Accept(this);
    private IOperand Compile(BoundExpression expr) => expr.Accept(this);

    private void AttachBlock(BasicBlock basicBlock)
    {
        this.currentBasicBlock = basicBlock;
        this.currentBasicBlock.Index = this.blockIndex++;
        this.isDetached = false;
    }
    private void DetachBlock() => this.isDetached = true;

    public void Write(IInstruction instr)
    {
        // Happens, when the basic block got detached and there's code left over to compile
        // Example:
        //     goto foo;
        //     y = x;    // This is inaccessible, current BB is null here!
        //     foo:
        //
        // Another simple example would be code after return
        if (this.isDetached && !instr.IsValidInUnreachableContext) return;
        this.currentBasicBlock.InsertLast(instr);
    }

    private Module GetDefiningModule(Symbol symbol)
    {
        var pathToSymbol = symbol.AncestorChain.OfType<ModuleSymbol>().First();
        return (Module)this.procedure.Assembly.Lookup(pathToSymbol);
    }

    private Procedure DefineProcedure(FunctionSymbol function) => this.GetDefiningModule(function).DefineProcedure(function);
    private BasicBlock DefineBasicBlock(LabelSymbol label) => this.procedure.DefineBasicBlock(label);
    private Local DefineLocal(LocalSymbol local) => this.procedure.DefineLocal(local);
    private Global DefineGlobal(GlobalSymbol global) => this.GetDefiningModule(global).DefineGlobal(global);
    private Parameter DefineParameter(ParameterSymbol param) => this.procedure.DefineParameter(param);
    private Register DefineRegister(TypeSymbol type) => this.procedure.DefineRegister(type);

    private Procedure SynthetizeProcedure(SynthetizedFunctionSymbol func)
    {
        // We handle synthetized functions a bit specially, as they are not part of our symbol
        // tree, so we compile them, in case they have not been yet
        var compiledAlready = this.procedure.DeclaringModule.Procedures.ContainsKey(func);
        var proc = this.procedure.DeclaringModule.DefineProcedure(func);
        if (!compiledAlready)
        {
            var codegen = new FunctionBodyCodegen(proc);
            func.Body.Accept(codegen);
        }
        return proc;
    }

    // Statements //////////////////////////////////////////////////////////////

    public override IOperand VisitSequencePointStatement(BoundSequencePointStatement node)
    {
        // Emit the sequence point
        this.Write(SequencePoint(node.Range));

        // If we need to emit a NOP, emit it
        if (node.EmitNop) this.Write(Nop());

        // Compile the statement, if there is one
        if (node.Statement is not null) this.Compile(node.Statement);

        return default!;
    }

    public override IOperand VisitLocalDeclaration(BoundLocalDeclaration node)
    {
        if (node.Value is null) return default!;

        var right = this.Compile(node.Value);
        var left = this.DefineLocal(node.Local);
        this.Write(Store(left, right));

        return default!;
    }

    public override IOperand VisitLabelStatement(BoundLabelStatement node)
    {
        // Define a new basic block
        var newBasicBlock = this.DefineBasicBlock(node.Label);

        // Here we thread the previous basic block to this one
        // Basically an implicit goto
        this.Write(Jump(newBasicBlock));
        this.AttachBlock(newBasicBlock);

        return default!;
    }

    public override IOperand VisitConditionalGotoStatement(BoundConditionalGotoStatement node)
    {
        var condition = this.Compile(node.Condition);

        // In case the condition is a never type, we don't bother writing out the then and else bodies,
        // as they can not be evaluated
        // Note, that for side-effects we still emit the condition code
        if (SymbolEqualityComparer.Default.Equals(node.Condition.TypeRequired, IntrinsicSymbols.Never)) return default(Void);

        // Allocate blocks
        var thenBlock = this.DefineBasicBlock(node.Target);
        var elseBlock = this.DefineBasicBlock(new SynthetizedLabelSymbol());
        // Branch based on condition
        this.Write(Branch(condition, thenBlock, elseBlock));
        // We fall-through to the else block implicitly
        this.AttachBlock(elseBlock);

        return default!;
    }

    // Lvalues /////////////////////////////////////////////////////////////////

    private (IInstruction Load, IInstruction Store) CompileLvalue(BoundLvalue lvalue)
    {
        switch (lvalue)
        {
        case BoundLocalLvalue local:
        {
            var src = this.DefineLocal(local.Local);
            return (Load: Load(default!, src), Store: Store(src, default!));
        }
        case BoundGlobalLvalue global:
        {
            var src = this.DefineGlobal(global.Global);
            return (Load: Load(default!, src), Store: Store(src, default!));
        }
        case BoundFieldLvalue field:
        {
            var receiver = field.Receiver is null ? null : this.Compile(field.Receiver);
            if (receiver is null)
            {
                var src = new SymbolReference(field.Field);
                return (Load: Load(default!, src), Store: Store(src, default!));
            }
            else
            {
                return (
                    Load: LoadField(default!, receiver, field.Field),
                    Store: StoreField(receiver, field.Field, default!));
            }
        }
        case BoundArrayAccessLvalue arrayAccess:
        {
            var array = this.Compile(arrayAccess.Array);
            var indices = arrayAccess.Indices
                .Select(this.Compile)
                .ToList();
            return (Load: LoadElement(default!, array, indices), Store: StoreElement(array, indices, default!));
        }
        default:
            throw new System.ArgumentOutOfRangeException(nameof(lvalue));
        }
    }

    // Expressions /////////////////////////////////////////////////////////////

    public override IOperand VisitStringExpression(BoundStringExpression node) =>
        throw new System.InvalidOperationException("should have been lowered");

    public override IOperand VisitSequencePointExpression(BoundSequencePointExpression node)
    {
        // Emit the sequence point
        this.Write(SequencePoint(node.Range));

        // If we need to emit a NOP, emit it
        if (node.EmitNop) this.Write(Nop());

        // Emit the expression
        return this.Compile(node.Expression);
    }

    public override IOperand VisitCallExpression(BoundCallExpression node)
    {
        var isSetter = node.Method is IPropertyAccessorSymbol p && p.Property.Setter == node.Method;
        if (node.Receiver is null)
        {
            var args = node.Arguments.Select(this.Compile).ToList();
            var callResult = this.DefineRegister(node.TypeRequired);
            var proc = this.TranslateFunctionSymbol(node.Method);
            this.Write(Call(callResult, proc, args));
            return isSetter ? args[^1] : callResult;
        }
        else
        {
            var receiver = this.Compile(node.Receiver);
            var args = node.Arguments.Select(this.Compile).ToList();
            var callResult = this.DefineRegister(node.TypeRequired);
            var proc = this.TranslateFunctionSymbol(node.Method);
            this.Write(MemberCall(callResult, proc, receiver, args));
            return isSetter ? args[^1] : callResult;
        }
    }

    public override IOperand VisitObjectCreationExpression(BoundObjectCreationExpression node)
    {
        var ctor = this.TranslateFunctionSymbol(node.Constructor);
        var args = node.Arguments.Select(this.Compile).ToList();
        var result = this.DefineRegister(node.TypeRequired);
        this.Write(NewObject(result, ctor, args));
        return result;
    }

    public override IOperand VisitArrayAccessExpression(BoundArrayAccessExpression node)
    {
        var array = this.Compile(node.Array);
        var indices = node.Indices.Select(this.Compile).ToList();
        var result = this.DefineRegister(node.TypeRequired);
        this.Write(LoadElement(result, array, indices));
        return result;
    }

    public override IOperand VisitArrayCreationExpression(BoundArrayCreationExpression node)
    {
        var dimensions = node.Sizes.Select(this.Compile).ToList();
        var result = this.DefineRegister(node.TypeRequired);
        this.Write(NewArray(result, node.ElementType, dimensions));
        return result;
    }

    public override IOperand VisitArrayLengthExpression(BoundArrayLengthExpression node)
    {
        var array = this.Compile(node.Array);
        var result = this.DefineRegister(node.TypeRequired);
        this.Write(ArrayLength(result, array));
        return result;
    }

    public override IOperand VisitGotoExpression(BoundGotoExpression node)
    {
        var target = this.DefineBasicBlock(node.Target);
        this.Write(Jump(target));
        this.DetachBlock();
        return default(Void);
    }

    public override IOperand VisitBlockExpression(BoundBlockExpression node)
    {
        // Find locals that we care about
        var locals = node.Locals
            .OfType<SourceLocalSymbol>()
            .ToList();

        // Start scope
        if (locals.Count > 0) this.Write(StartScope(locals));

        // Compile all of the statements within
        foreach (var stmt in node.Statements) this.Compile(stmt);
        // Compile value
        var result = this.Compile(node.Value);

        // End scope
        if (locals.Count > 0) this.Write(EndScope());

        return result;
    }

    public override IOperand VisitAssignmentExpression(BoundAssignmentExpression node)
    {
        var right = this.Compile(node.Right);
        var (leftLoad, leftStore) = this.CompileLvalue(node.Left);
        var toStore = right;

        if (node.CompoundOperator is not null)
        {
            var leftValue = this.DefineRegister(node.Left.Type);
            var tmp = this.DefineRegister(node.TypeRequired);
            toStore = tmp;
            // Patch
            PatchLoadTarget(leftLoad, leftValue);
            this.Write(leftLoad);
            if (IsAdd(node.CompoundOperator)) this.Write(Add(tmp, leftValue, right));
            else if (IsSub(node.CompoundOperator)) this.Write(Sub(tmp, leftValue, right));
            else if (IsMul(node.CompoundOperator)) this.Write(Mul(tmp, leftValue, right));
            else if (IsDiv(node.CompoundOperator)) this.Write(Div(tmp, leftValue, right));
            else throw new System.NotImplementedException();
        }

        // Patch
        PatchStoreSource(leftStore, toStore);
        this.Write(leftStore);
        return toStore;
    }

    private static void PatchLoadTarget(IInstruction loadInstr, Register target)
    {
        switch (loadInstr)
        {
        case LoadInstruction load:
            load.Target = target;
            break;
        case LoadElementInstruction loadElement:
            loadElement.Target = target;
            break;
        case LoadFieldInstruction loadField:
            loadField.Target = target;
            break;
        default:
            throw new System.ArgumentOutOfRangeException(nameof(loadInstr));
        }
    }

    private static void PatchStoreSource(IInstruction storeInstr, IOperand source)
    {
        switch (storeInstr)
        {
        case StoreInstruction store:
            store.Source = source;
            break;
        case StoreElementInstruction storeElement:
            storeElement.Source = source;
            break;
        case StoreFieldInstruction storeField:
            storeField.Source = source;
            break;
        default:
            throw new System.ArgumentOutOfRangeException(nameof(storeInstr));
        }
    }

    public override IOperand VisitUnaryExpression(BoundUnaryExpression node)
    {
        var sub = node.Operand.Accept(this);
        var target = this.DefineRegister(node.TypeRequired);

        if (IsNot(node.Operator)) this.Write(Equal(target, sub, new Constant(false)));
        else if (IsPlus(node.Operator)) { /* no-op */ }
        else if (IsMinus(node.Operator)) this.Write(Mul(target, sub, new Constant(-1)));
        // TODO
        else throw new System.NotImplementedException();

        return target;
    }

    public override IOperand VisitBinaryExpression(BoundBinaryExpression node)
    {
        var left = this.Compile(node.Left);
        var right = this.Compile(node.Right);
        var target = this.DefineRegister(node.TypeRequired);

        if (IsAdd(node.Operator))
        {
            this.Write(Add(target, left, right));
        }
        else if (IsSub(node.Operator))
        {
            this.Write(Sub(target, left, right));
        }
        else if (IsMul(node.Operator))
        {
            this.Write(Mul(target, left, right));
        }
        else if (IsDiv(node.Operator))
        {
            this.Write(Div(target, left, right));
        }
        else if (IsRem(node.Operator))
        {
            this.Write(Rem(target, left, right));
        }
        else if (IsMod(node.Operator))
        {
            // a mod b
            //  <=>
            // (a rem b + b) rem b
            var tmp1 = this.DefineRegister(node.TypeRequired);
            var tmp2 = this.DefineRegister(node.TypeRequired);
            this.Write(Rem(tmp1, left, right));
            this.Write(Add(tmp2, tmp1, right));
            this.Write(Rem(target, tmp1, right));
        }
        else if (IsLess(node.Operator))
        {
            this.Write(Less(target, left, right));
        }
        else if (IsGreater(node.Operator))
        {
            // a > b
            //  <=>
            // b < a
            this.Write(Less(target, right, left));
        }
        else if (IsLessEqual(node.Operator))
        {
            // a <= b
            //  <=>
            // (b < a) == false
            var tmp = this.DefineRegister(node.TypeRequired);
            this.Write(Less(tmp, right, left));
            this.Write(Equal(target, tmp, new Constant(false)));
        }
        else if (IsGreaterEqual(node.Operator))
        {
            // a >= b
            //  <=>
            // (a < b) == false
            var tmp = this.DefineRegister(node.TypeRequired);
            this.Write(Less(tmp, left, right));
            this.Write(Equal(target, tmp, new Constant(false)));
        }
        else if (IsEqual(node.Operator))
        {
            this.Write(Equal(target, left, right));
        }
        else if (IsNotEqual(node.Operator))
        {
            // a != b
            //  <=>
            // (a == b) == false
            var tmp = this.DefineRegister(node.TypeRequired);
            this.Write(Equal(tmp, left, right));
            this.Write(Equal(target, tmp, new Constant(false)));
        }
        else
        {
            // TODO
            throw new System.NotImplementedException();
        }

        return target;
    }

    public override IOperand VisitReturnExpression(BoundReturnExpression node)
    {
        var operand = this.Compile(node.Value);
        this.Write(Ret(operand));
        this.DetachBlock();
        return default!;
    }

    public override IOperand VisitGlobalExpression(BoundGlobalExpression node)
    {
        var result = this.DefineRegister(node.TypeRequired);
        var global = this.DefineGlobal(node.Global);
        this.Write(Load(result, global));
        return result;
    }

    public override IOperand VisitLocalExpression(BoundLocalExpression node)
    {
        var result = this.DefineRegister(node.TypeRequired);
        var local = this.DefineLocal(node.Local);
        this.Write(Load(result, local));
        return result;
    }

    public override IOperand VisitFunctionGroupExpression(BoundFunctionGroupExpression node) =>
        // TODO
        throw new System.NotImplementedException();

    private IOperand TranslateFunctionSymbol(FunctionSymbol symbol) => symbol switch
    {
        SourceFunctionSymbol func => this.DefineProcedure(func),
        SynthetizedFunctionSymbol func => this.SynthetizeProcedure(func),
        MetadataMethodSymbol m => new SymbolReference(m),
        FunctionInstanceSymbol i => this.TranslateFunctionInstanceSymbol(i),
        _ => throw new System.ArgumentOutOfRangeException(nameof(symbol)),
    };

    private IOperand TranslateFunctionInstanceSymbol(FunctionInstanceSymbol i)
    {
        // NOTE: We visit the underlying instantiated symbol in case it's synthetized by us
        this.TranslateFunctionSymbol(i.GenericDefinition);
        return new SymbolReference(i);
    }

    // NOTE: Parameters don't need loading, they are read-only values by default
    public override IOperand VisitParameterExpression(BoundParameterExpression node) =>
        this.DefineParameter(node.Parameter);

    public override IOperand VisitLiteralExpression(BoundLiteralExpression node) => new Constant(node.Value);
    public override IOperand VisitUnitExpression(BoundUnitExpression node) => default(Void);

    public override IOperand VisitFieldExpression(BoundFieldExpression node)
    {
        var receiver = node.Receiver is null ? null : this.Compile(node.Receiver);
        var result = this.DefineRegister(node.TypeRequired);
        this.Write(receiver is null
            ? Load(result, new SymbolReference(node.Field))
            : LoadField(result, receiver, node.Field));
        return result;
    }

    // TODO: Do something with this block

    private static bool IsEqual(Symbol op) => op == IntrinsicSymbols.Int32_Equal
                                           || op == IntrinsicSymbols.Float64_Equal;
    private static bool IsNotEqual(Symbol op) => op == IntrinsicSymbols.Int32_NotEqual
                                              || op == IntrinsicSymbols.Float64_NotEqual;
    private static bool IsLess(Symbol op) => op == IntrinsicSymbols.Int32_LessThan
                                          || op == IntrinsicSymbols.Float64_LessThan;
    private static bool IsLessEqual(Symbol op) => op == IntrinsicSymbols.Int32_LessEqual
                                               || op == IntrinsicSymbols.Float64_LessEqual;
    private static bool IsGreater(Symbol op) => op == IntrinsicSymbols.Int32_GreaterThan
                                             || op == IntrinsicSymbols.Float64_GreaterThan;
    private static bool IsGreaterEqual(Symbol op) => op == IntrinsicSymbols.Int32_GreaterEqual
                                                  || op == IntrinsicSymbols.Float64_GreaterEqual;

    private static bool IsNot(Symbol op) => op == IntrinsicSymbols.Bool_Not;
    private static bool IsPlus(Symbol op) => op == IntrinsicSymbols.Int32_Plus
                                          || op == IntrinsicSymbols.Float64_Plus;
    private static bool IsMinus(Symbol op) => op == IntrinsicSymbols.Int32_Minus
                                           || op == IntrinsicSymbols.Float64_Minus;

    private static bool IsAdd(Symbol op) => op == IntrinsicSymbols.Int32_Add
                                         || op == IntrinsicSymbols.Float64_Add;
    private static bool IsSub(Symbol op) => op == IntrinsicSymbols.Int32_Sub
                                         || op == IntrinsicSymbols.Float64_Sub;
    private static bool IsMul(Symbol op) => op == IntrinsicSymbols.Int32_Mul
                                         || op == IntrinsicSymbols.Float64_Mul;
    private static bool IsDiv(Symbol op) => op == IntrinsicSymbols.Int32_Div
                                         || op == IntrinsicSymbols.Float64_Div;
    private static bool IsRem(Symbol op) => op == IntrinsicSymbols.Int32_Rem
                                         || op == IntrinsicSymbols.Float64_Rem;
    private static bool IsMod(Symbol op) => op == IntrinsicSymbols.Int32_Mod
                                         || op == IntrinsicSymbols.Float64_Mod;
}
