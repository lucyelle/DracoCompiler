using System.Collections.Generic;
using Draco.Compiler.Api.Diagnostics;
using Draco.Compiler.Internal.Diagnostics;

namespace Draco.Compiler.Internal.Solver;

/// <summary>
/// Represents a constraint for the solver.
/// </summary>
internal interface IConstraint
{
    /// <summary>
    /// The solver this constraint belongs to.
    /// </summary>
    public ConstraintSolver Solver { get; }

    /// <summary>
    /// The promise of this constraint.
    /// </summary>
    public IConstraintPromise Promise { get; }

    /// <summary>
    /// The builder for the <see cref="Api.Diagnostics.Diagnostic"/>.
    /// </summary>
    public Diagnostic.Builder Diagnostic { get; }

    /// <summary>
    /// Attempts to solve this constraint.
    /// </summary>
    /// <param name="diagnostics">The bag to report diagnostics to.</param>
    /// <returns>The state progression.</returns>
    public IEnumerable<SolveState> Solve(DiagnosticBag diagnostics);

    /// <summary>
    /// Fails this constraint silently to avoid cascading errors.
    /// </summary>
    public void FailSilently();
}

/// <summary>
/// An <see cref="IConstraint"/> with known resolution type.
/// </summary>
/// <typeparam name="TResult">The result type of this constraint.</typeparam>
internal interface IConstraint<TResult> : IConstraint
{
    /// <summary>
    /// The promise of this constraint.
    /// </summary>
    public new IConstraintPromise<TResult> Promise { get; }
}
