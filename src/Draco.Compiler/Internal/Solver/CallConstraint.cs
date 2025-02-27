using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Draco.Compiler.Api.Syntax;
using Draco.Compiler.Internal.Binding;
using Draco.Compiler.Internal.Diagnostics;
using Draco.Compiler.Internal.Symbols;
using Draco.Compiler.Internal.Symbols.Synthetized;
using Draco.Compiler.Internal.UntypedTree;
using Draco.Compiler.Internal.Utilities;

namespace Draco.Compiler.Internal.Solver;

/// <summary>
/// Represents a callability constraint for indirect calls.
/// </summary>
internal sealed class CallConstraint : Constraint<Unit>
{
    /// <summary>
    /// The called expression type.
    /// </summary>
    public TypeSymbol CalledType { get; }

    /// <summary>
    /// The arguments the function was called with.
    /// </summary>
    public ImmutableArray<object> Arguments { get; }

    /// <summary>
    /// The return type of the call.
    /// </summary>
    public TypeSymbol ReturnType { get; }

    public CallConstraint(
        ConstraintSolver solver,
        TypeSymbol calledType,
        ImmutableArray<object> arguments,
        TypeSymbol returnType)
        : base(solver)
    {
        this.CalledType = calledType;
        this.Arguments = arguments;
        this.ReturnType = returnType;
    }

    public override string ToString() =>
        $"Call(function: {this.CalledType}, args: [{string.Join(", ", this.Arguments)}]) => {this.ReturnType}";

    public override void FailSilently()
    {
        this.Unify(this.ReturnType, IntrinsicSymbols.ErrorType);
        this.Promise.Fail(default, null);
    }

    public override IEnumerable<SolveState> Solve(DiagnosticBag diagnostics)
    {
    start:
        var called = this.CalledType.Substitution;
        // We can't advance on type variables
        if (called.IsTypeVariable)
        {
            yield return SolveState.Stale;
            goto start;
        }

        if (called.IsError)
        {
            // Don't propagate errors
            this.FailSilently();
            yield return SolveState.Solved;
        }

        // We can now check if it's a function
        if (called is not FunctionTypeSymbol functionType)
        {
            // Error
            this.Unify(this.ReturnType, IntrinsicSymbols.ErrorType);
            this.Diagnostic
                .WithTemplate(TypeCheckingErrors.CallNonFunction)
                .WithFormatArgs(called);
            this.Promise.Fail(default, diagnostics);
            yield return SolveState.Solved;
            yield break;
        }

        // It's a function
        // We can merge the return type
        this.Unify(this.ReturnType, functionType.ReturnType);
        yield return SolveState.AdvancedContinue;

        // Check if it has the same number of args
        if (functionType.Parameters.Length != this.Arguments.Length)
        {
            // Error
            this.Unify(this.ReturnType, IntrinsicSymbols.ErrorType);
            this.Diagnostic
                .WithTemplate(TypeCheckingErrors.TypeMismatch)
                .WithFormatArgs(
                    functionType,
                    this.MakeMismatchedType(functionType.ReturnType));
            this.Promise.Fail(default, diagnostics);
            yield return SolveState.Solved;
        }

        // Start scoring args
        var score = new CallScore(functionType.Parameters.Length);
        while (true)
        {
            var changed = this.AdjustScore(functionType, score);
            if (score.HasZero)
            {
                // Error
                this.Unify(this.ReturnType, IntrinsicSymbols.ErrorType);
                this.Diagnostic
                    .WithTemplate(TypeCheckingErrors.TypeMismatch)
                    .WithFormatArgs(
                        functionType,
                        this.MakeMismatchedType(functionType.ReturnType));
                this.Promise.Fail(default, diagnostics);
                yield return SolveState.Solved;
            }
            if (score.IsWellDefined) break;
            yield return changed ? SolveState.AdvancedContinue : SolveState.Stale;
        }

        // We are done
        foreach (var (param, arg) in functionType.Parameters.Zip(this.Arguments))
        {
            this.UnifyParameterWithArgument(param.Type, arg);
        }

        yield return SolveState.Solved;
    }

    private bool AdjustScore(FunctionTypeSymbol candidate, CallScore scoreVector)
    {
        Debug.Assert(candidate.Parameters.Length == this.Arguments.Length);
        Debug.Assert(candidate.Parameters.Length == scoreVector.Length);

        var changed = false;
        for (var i = 0; i < scoreVector.Length; ++i)
        {
            var param = candidate.Parameters[i];
            var arg = this.Arguments[i];
            var score = scoreVector[i];

            // If the argument is not null, it means we have already scored it
            if (score is not null) continue;

            score = OverloadConstraint.ScoreArgument(param, ExtractType(arg));
            changed = changed || score is not null;
            scoreVector[i] = score;

            // If the score hit 0, terminate early, this overload got eliminated
            if (score == 0) return changed;
        }
        return changed;
    }

    private FunctionTypeSymbol MakeMismatchedType(TypeSymbol returnType) => new(
        this.Arguments
            .Select(a => new SynthetizedParameterSymbol(null, ExtractType(a)))
            .Cast<ParameterSymbol>()
            .ToImmutableArray(),
        returnType);

    private void UnifyParameterWithArgument(TypeSymbol paramType, object argument)
    {
        var promise = this.Solver.Assignable(paramType, ExtractType(argument));
        var syntax = ExtractSyntax(argument);
        if (syntax is not null)
        {
            promise.ConfigureDiagnostic(diag => diag.WithLocation(syntax.Location));
        }
    }

    private static TypeSymbol ExtractType(object node) => node switch
    {
        UntypedExpression e => e.TypeRequired,
        UntypedLvalue l => l.Type,
        TypeSymbol t => t,
        _ => throw new ArgumentOutOfRangeException(nameof(node)),
    };

    private static SyntaxNode? ExtractSyntax(object node) => node switch
    {
        UntypedNode n => n.Syntax,
        _ => null,
    };
}
