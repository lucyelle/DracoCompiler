using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Draco.Compiler.Api.Diagnostics;
using Draco.Compiler.Api.Syntax;
using Draco.Compiler.Internal.Diagnostics;

namespace Draco.Compiler.Internal.Syntax;

/// <summary>
/// Parses a sequence of <see cref="SyntaxToken"/>s into a <see cref="SyntaxNode"/>.
/// </summary>
internal sealed class Parser
{
    /// <summary>
    /// The different declaration contexts.
    /// </summary>
    private enum DeclarationContext
    {
        /// <summary>
        /// Global, like in a compilation unit, module, class, ...
        /// </summary>
        Global,

        /// <summary>
        /// Local to a function body/expression/code-block.
        /// </summary>
        Local,
    }

    /// <summary>
    /// Control flow statements parse sligtly differently in expression and statement contexts.
    /// This is the discriminating enum for them to avoid duplicating parser code.
    /// </summary>
    private enum ControlFlowContext
    {
        /// <summary>
        /// Control flow for an expression.
        /// </summary>
        Expr,

        /// <summary>
        /// Control flow for a statement.
        /// </summary>
        Stmt,
    }

    /// <summary>
    /// The result of trying to disambiguate '<'.
    /// </summary>
    private enum LessThanDisambiguation
    {
        /// <summary>
        /// Must be an operator for comparison.
        /// </summary>
        Operator,

        /// <summary>
        /// Must be a generic parameter list.
        /// </summary>
        Generics,
    }

    /// <summary>
    /// Represents a parsed block.
    /// This is factored out because we parse blocks differently, and instantiating an AST node could be wasteful.
    /// </summary>
    /// <param name="OpenCurly">The open curly brace.</param>
    /// <param name="Statements">The list of statements.</param>
    /// <param name="Value">The evaluation value, if any.</param>
    /// <param name="CloseCurly">The close curly brace.</param>
    private readonly record struct ParsedBlock(
        SyntaxToken OpenCurly,
        SyntaxList<StatementSyntax> Statements,
        ExpressionSyntax? Value,
        SyntaxToken CloseCurly);

    /// <summary>
    /// A delegate for an <see cref="ExpressionSyntax"/> parser.
    /// </summary>
    /// <param name="level">The level in the precedence table.</param>
    /// <returns>The parsed <see cref="ExpressionSyntax"/>.</returns>
    private delegate ExpressionSyntax ExpressionParserDelegate(int level);

    /// <summary>
    /// Constructs an <see cref="ExpressionParserDelegate"/> for a set of prefix operators.
    /// </summary>
    /// <param name="operators">The set of prefix operators.</param>
    /// <returns>An <see cref="ExpressionParserDelegate"/> that recognizes <paramref name="operators"/> as prefix operators.</returns>
    private ExpressionParserDelegate Prefix(params TokenKind[] operators) => level =>
    {
        var opKind = this.Peek();
        if (operators.Contains(opKind))
        {
            // There is such operator on this level
            var op = this.Advance();
            var subexpr = this.ParseExpression(level);
            return new UnaryExpressionSyntax(op, subexpr);
        }
        else
        {
            // Just descent to next level
            return this.ParseExpression(level + 1);
        }
    };

    /// <summary>
    /// Constructs an <see cref="ExpressionParserDelegate"/> for a set of left-associative binary operators.
    /// </summary>
    /// <param name="operators">The set of binary operators.</param>
    /// <returns>An <see cref="ExpressionParserDelegate"/> that recognizes <paramref name="operators"/> as
    /// left-associative binary operators.</returns>
    private ExpressionParserDelegate BinaryLeft(params TokenKind[] operators) => level =>
    {
        // We unroll left-associativity into a loop
        var result = this.ParseExpression(level + 1);
        while (true)
        {
            var opKind = this.Peek();
            if (!operators.Contains(opKind)) break;
            var op = this.Advance();
            var right = this.ParseExpression(level + 1);
            result = new BinaryExpressionSyntax(result, op, right);
        }
        return result;
    };

    /// <summary>
    /// Constructs an <see cref="ExpressionParserDelegate"/> for a set of right-associative binary operators.
    /// </summary>
    /// <param name="operators">The set of binary operators.</param>
    /// <returns>An <see cref="ExpressionParserDelegate"/> that recognizes <paramref name="operators"/> as
    /// right-associative binary operators.</returns>
    private ExpressionParserDelegate BinaryRight(params TokenKind[] operators) => level =>
    {
        // Right-associativity is simply right-recursion
        var result = this.ParseExpression(level + 1);
        var opKind = this.Peek();
        if (operators.Contains(this.Peek()))
        {
            var op = this.Advance();
            var right = this.ParseExpression(level);
            result = new BinaryExpressionSyntax(result, op, right);
        }
        return result;
    };

    /// <summary>
    /// The list of all tokens that can start a declaration.
    /// </summary>
    private static readonly TokenKind[] declarationStarters = new[]
    {
        TokenKind.KeywordImport,
        TokenKind.KeywordFunc,
        TokenKind.KeywordModule,
        TokenKind.KeywordVar,
        TokenKind.KeywordVal,
    };

    /// <summary>
    /// The list of all tokens that can be a visibility modifier.
    /// </summary>
    private static readonly TokenKind[] visibilityModifiers = new[]
    {
        TokenKind.KeywordInternal,
        TokenKind.KeywordPublic,
    };

    /// <summary>
    /// The list of all tokens that can start an expression.
    /// </summary>
    private static readonly TokenKind[] expressionStarters = new[]
    {
        TokenKind.Identifier,
        TokenKind.LiteralInteger,
        TokenKind.LiteralFloat,
        TokenKind.LiteralCharacter,
        TokenKind.LineStringStart,
        TokenKind.MultiLineStringStart,
        TokenKind.KeywordFalse,
        // NOTE: This is for later, when we decide if the lambda syntax should be func(...) = ...
        TokenKind.KeywordFunc,
        TokenKind.KeywordGoto,
        TokenKind.KeywordIf,
        TokenKind.KeywordNot,
        TokenKind.KeywordReturn,
        TokenKind.KeywordTrue,
        TokenKind.KeywordWhile,
        TokenKind.ParenOpen,
        TokenKind.CurlyOpen,
        TokenKind.BracketOpen,
        TokenKind.Plus,
        TokenKind.Minus,
        TokenKind.Star,
    };

    private readonly ITokenSource tokenSource;
    private readonly SyntaxDiagnosticTable diagnostics;

    public Parser(ITokenSource tokenSource, SyntaxDiagnosticTable diagnostics)
    {
        this.tokenSource = tokenSource;
        this.diagnostics = diagnostics;
    }

    /// <summary>
    /// Checks, if the current token kind and the potentially following tokens form a declaration.
    /// </summary>
    /// <param name="kind">The current token kind.</param>
    /// <returns>True, if <paramref name="kind"/> in the current state can form the start of a declaration.</returns>
    private bool IsDeclarationStarter(TokenKind kind) =>
           declarationStarters.Contains(kind)
        // Label
        || kind == TokenKind.Identifier && this.Peek(1) == TokenKind.Colon;

    /// <summary>
    /// Checks if the token kind is visibility modifier.
    /// </summary>
    /// <param name="kind">The token kind to check for.</param>
    /// <returns>True, if the token kind is visibility modifier, otherwise false.</returns>
    private static bool IsVisibilityModifier(TokenKind kind) => visibilityModifiers.Contains(kind);

    /// <summary>
    /// Checks, if the current token kind and the potentially following tokens form an expression.
    /// </summary>
    /// <param name="kind">The current token kind.</param>
    /// <returns>True, uf <paramref name="kind"/> in the current state can form the start of an expression.</returns>
    private static bool IsExpressionStarter(TokenKind kind) => expressionStarters.Contains(kind);

    /// <summary>
    /// Parses a <see cref="CompilationUnitSyntax"/> until the end of input.
    /// </summary>
    /// <returns>The parsed <see cref="CompilationUnitSyntax"/>.</returns>
    public CompilationUnitSyntax ParseCompilationUnit()
    {
        var decls = SyntaxList.CreateBuilder<DeclarationSyntax>();
        while (this.Peek() != TokenKind.EndOfInput) decls.Add(this.ParseDeclaration());
        var end = this.Expect(TokenKind.EndOfInput);
        return new(decls.ToSyntaxList(), end);
    }

    /// <summary>
    /// Parses a global-level declaration.
    /// </summary>
    /// <param name="local">True, if the declaration should allow local context elements.</param>
    /// <returns>The parsed <see cref="DeclarationSyntax"/>.</returns>
    internal DeclarationSyntax ParseDeclaration(bool local = false) =>
        this.ParseDeclaration(local ? DeclarationContext.Local : DeclarationContext.Global);

    /// <summary>
    /// Parses a declaration.
    /// </summary>
    /// <param name="context">The current context.</param>
    /// <returns>The parsed <see cref="DeclarationSyntax"/>.</returns>
    private DeclarationSyntax ParseDeclaration(DeclarationContext context)
    {
        var modifier = this.ParseVisibilityModifier();
        switch (this.Peek())
        {
        case TokenKind.KeywordImport:
            return this.ParseImportDeclaration();

        case TokenKind.KeywordFunc:
            return this.ParseFunctionDeclaration(modifier);

        case TokenKind.KeywordModule:
            return this.ParseModuleDeclaration(context);

        case TokenKind.KeywordVar:
        case TokenKind.KeywordVal:
            return this.ParseVariableDeclaration(modifier);

        case TokenKind.Identifier when this.Peek(1) == TokenKind.Colon:
            return this.ParseLabelDeclaration(context);

        default:
        {
            var input = this.Synchronize(t => t switch
            {
                _ when this.IsDeclarationStarter(t) => false,
                _ when IsVisibilityModifier(t) => false,
                _ => true,
            });
            var info = DiagnosticInfo.Create(SyntaxErrors.UnexpectedInput, formatArgs: "declaration");
            var diag = new SyntaxDiagnosticInfo(info, Offset: 0, Width: input.FullWidth);
            var node = new UnexpectedDeclarationSyntax(modifier, input);
            this.AddDiagnostic(node, diag);
            return node;
        }
        }
    }

    /// <summary>
    /// Parses a statement.
    /// </summary>
    /// <returns>The parsed <see cref="StatementSyntax"/>.</returns>
    internal StatementSyntax ParseStatement(bool allowDecl)
    {
        switch (this.Peek())
        {
        // Declarations
        case TokenKind t when allowDecl && this.IsDeclarationStarter(t):
        {
            var decl = this.ParseDeclaration(DeclarationContext.Local);
            return new DeclarationStatementSyntax(decl);
        }

        // Expressions that can appear without braces
        case TokenKind.CurlyOpen:
        case TokenKind.KeywordIf:
        case TokenKind.KeywordWhile:
        {
            var expr = this.ParseControlFlowExpression(ControlFlowContext.Stmt);
            return new ExpressionStatementSyntax(expr, null);
        }

        // Assume expression
        default:
        {
            // TODO: This assumption might not be the best
            var expr = this.ParseExpression();
            var semicolon = this.Expect(TokenKind.Semicolon);
            return new ExpressionStatementSyntax(expr, semicolon);
        }
        }
    }

    /// <summary>
    /// Parses an <see cref="ImportDeclarationSyntax"/>.
    /// </summary>
    /// <returns>The parsed <see cref="ImportDeclarationSyntax"/>.</returns>
    private ImportDeclarationSyntax ParseImportDeclaration()
    {
        // Import keyword
        var importKeyword = this.Expect(TokenKind.KeywordImport);
        // Path
        var path = this.ParseImportPath();
        // Ending semicolon
        var semicolon = this.Expect(TokenKind.Semicolon);
        return new ImportDeclarationSyntax(importKeyword, path, semicolon);
    }

    /// <summary>
    /// Parses an <see cref="ImportPathSyntax"/>.
    /// </summary>
    /// <returns>The parsed <see cref="ImportPathSyntax"/>.</returns>
    private ImportPathSyntax ParseImportPath()
    {
        // Root element
        var rootName = this.Expect(TokenKind.Identifier);
        var result = new RootImportPathSyntax(rootName) as ImportPathSyntax;
        // For every dot, we make a member-access
        while (this.Matches(TokenKind.Dot, out var dot))
        {
            var memberName = this.Expect(TokenKind.Identifier);
            result = new MemberImportPathSyntax(result, dot, memberName);
        }
        return result;
    }

    /// <summary>
    /// Parses a <see cref="VariableDeclarationSyntax"/>.
    /// </summary>
    /// <returns>The parsed <see cref="VariableDeclarationSyntax"/>.</returns>
    private VariableDeclarationSyntax ParseVariableDeclaration(SyntaxToken? visibility)
    {
        // NOTE: We will always call this function by checking the leading keyword
        var keyword = this.Advance();
        Debug.Assert(keyword.Kind is TokenKind.KeywordVal or TokenKind.KeywordVar);
        var identifier = this.Expect(TokenKind.Identifier);
        // We don't necessarily have type specifier
        TypeSpecifierSyntax? type = null;
        if (this.Peek() == TokenKind.Colon) type = this.ParseTypeSpecifier();
        // We don't necessarily have value assigned to the variable
        ValueSpecifierSyntax? assignment = null;
        if (this.Matches(TokenKind.Assign, out var assign))
        {
            var value = this.ParseExpression();
            assignment = new(assign, value);
        }
        // Eat semicolon at the end of declaration
        var semicolon = this.Expect(TokenKind.Semicolon);
        return new VariableDeclarationSyntax(visibility, keyword, identifier, type, assignment, semicolon);
    }

    /// <summary>
    /// Parses a function declaration.
    /// </summary>
    /// <returns>The parsed <see cref="FunctionDeclarationSyntax"/>.</returns>
    private FunctionDeclarationSyntax ParseFunctionDeclaration(SyntaxToken? visibility)
    {
        // Func keyword and name of the function
        var funcKeyword = this.Expect(TokenKind.KeywordFunc);
        var name = this.Expect(TokenKind.Identifier);

        // Optional generics
        var generics = null as GenericParameterListSyntax;
        if (this.Peek() == TokenKind.LessThan) generics = this.ParseGenericParameterList();

        // Parameters
        var openParen = this.Expect(TokenKind.ParenOpen);
        var funcParameters = this.ParseSeparatedSyntaxList(
            elementParser: this.ParseParameter,
            separatorKind: TokenKind.Comma,
            stopKind: TokenKind.ParenClose);
        var closeParen = this.Expect(TokenKind.ParenClose);

        // We don't necessarily have type specifier
        TypeSpecifierSyntax? returnType = null;
        if (this.Peek() == TokenKind.Colon) returnType = this.ParseTypeSpecifier();

        var body = this.ParseFunctionBody();

        return new FunctionDeclarationSyntax(
            visibility,
            funcKeyword,
            name,
            generics,
            openParen,
            funcParameters,
            closeParen,
            returnType,
            body);
    }

    /// <summary>
    /// Parses a module declaration.
    /// </summary>
    /// <param name="context">The current declaration context.</param>
    /// <returns>The parsed <see cref="DeclarationSyntax"/>.</returns>
    private DeclarationSyntax ParseModuleDeclaration(DeclarationContext context)
    {
        // Module keyword and name of the module
        var moduleKeyword = this.Expect(TokenKind.KeywordModule);
        var name = this.Expect(TokenKind.Identifier);

        var openCurly = this.Expect(TokenKind.CurlyOpen);
        var decls = SyntaxList.CreateBuilder<DeclarationSyntax>();
        while (true)
        {
            switch (this.Peek())
            {
            case TokenKind.EndOfInput:
            case TokenKind.CurlyClose:
                // On a close curly or end of input, we can immediately exit
                goto end_of_block;
            default:
                decls.Add(this.ParseDeclaration(DeclarationContext.Global));
                break;
            }
        }
    end_of_block:
        var closeCurly = this.Expect(TokenKind.CurlyClose);

        var result = new ModuleDeclarationSyntax(
            moduleKeyword,
            name,
            openCurly,
            decls.ToSyntaxList(),
            closeCurly) as DeclarationSyntax;

        if (context != DeclarationContext.Global)
        {
            // Create diagnostic
            var info = DiagnosticInfo.Create(SyntaxErrors.IllegalElementInContext, formatArgs: "module");
            var diag = new SyntaxDiagnosticInfo(info, Offset: 0, Width: result.Width);
            // Wrap up the result in an error node
            result = new UnexpectedDeclarationSyntax(null, SyntaxList.Create(result as SyntaxNode));
            // Add diagnostic
            this.AddDiagnostic(result, diag);
        }
        return result;
    }

    /// <summary>
    /// Parses a label declaration.
    /// </summary>
    /// <param name="context">The current declaration context.</param>
    /// <returns>The parsed <see cref="DeclarationSyntax"/>.</returns>
    private DeclarationSyntax ParseLabelDeclaration(DeclarationContext context)
    {
        var labelName = this.Expect(TokenKind.Identifier);
        var colon = this.Expect(TokenKind.Colon);
        var result = new LabelDeclarationSyntax(labelName, colon) as DeclarationSyntax;
        if (context != DeclarationContext.Local)
        {
            // Create diagnostic
            var info = DiagnosticInfo.Create(SyntaxErrors.IllegalElementInContext, formatArgs: "label");
            var diag = new SyntaxDiagnosticInfo(info, Offset: 0, Width: result.Width);
            // Wrap up the result in an error node
            result = new UnexpectedDeclarationSyntax(null, SyntaxList.Create(result as SyntaxNode));
            // Add diagnostic
            this.AddDiagnostic(result, diag);
        }
        return result;
    }

    /// <summary>
    /// Parses a function parameter.
    /// </summary>
    /// <returns>The parsed <see cref="ParameterSyntax"/>.</returns>
    private ParameterSyntax ParseParameter()
    {
        this.Matches(TokenKind.Ellipsis, out var variadic);
        var name = this.Expect(TokenKind.Identifier);
        var colon = this.Expect(TokenKind.Colon);
        var type = this.ParseType();
        return new(variadic, name, colon, type);
    }

    /// <summary>
    /// Parses a generic parameter list.
    /// </summary>
    /// <returns>The parsed <see cref="GenericParameterListSyntax"/>.</returns>
    private GenericParameterListSyntax ParseGenericParameterList()
    {
        var openBracket = this.Expect(TokenKind.LessThan);
        var parameters = this.ParseSeparatedSyntaxList(
            elementParser: this.ParseGenericParameter,
            separatorKind: TokenKind.Comma,
            stopKind: TokenKind.GreaterThan);
        var closeBracket = this.Expect(TokenKind.GreaterThan);
        return new(openBracket, parameters, closeBracket);
    }

    /// <summary>
    /// Parses a single generic parameter in a generic parameter list.
    /// </summary>
    /// <returns>The parsed <see cref="GenericParameterSyntax"/>.</returns>
    private GenericParameterSyntax ParseGenericParameter()
    {
        var name = this.Expect(TokenKind.Identifier);
        return new(name);
    }

    /// <summary>
    /// Parses a function body.
    /// </summary>
    /// <returns>The parsed <see cref="FunctionBodySyntax"/>.</returns>
    private FunctionBodySyntax ParseFunctionBody()
    {
        if (this.Matches(TokenKind.Assign, out var assign))
        {
            var expr = this.ParseExpression();
            var semicolon = this.Expect(TokenKind.Semicolon);
            return new InlineFunctionBodySyntax(assign, expr, semicolon);
        }
        else if (this.Peek() == TokenKind.CurlyOpen)
        {
            var block = this.ParseBlock(ControlFlowContext.Stmt);
            return new BlockFunctionBodySyntax(
                openBrace: block.OpenCurly,
                statements: block.Statements,
                closeBrace: block.CloseCurly);
        }
        else
        {
            // NOTE: I'm not sure what's the best strategy here
            // Maybe if we get to a '=' or '{' we could actually try to re-parse and prepend with the bogus input
            var input = this.Synchronize(t => t switch
            {
                TokenKind.Semicolon or TokenKind.CurlyClose => false,
                // NOTE: We don't consider label here
                _ when declarationStarters.Contains(t) => false,
                _ => true,
            });
            var info = DiagnosticInfo.Create(SyntaxErrors.UnexpectedInput, formatArgs: "function body");
            var diag = new SyntaxDiagnosticInfo(info, Offset: 0, Width: input.FullWidth);
            var node = new UnexpectedFunctionBodySyntax(input);
            this.AddDiagnostic(node, diag);
            return node;
        }
    }

    /// <summary>
    /// Parses a type specifier.
    /// </summary>
    /// <returns>The parsed <see cref="TypeSpecifierSyntax"/>.</returns>
    private TypeSpecifierSyntax ParseTypeSpecifier()
    {
        var colon = this.Expect(TokenKind.Colon);
        var type = this.ParseType();
        return new(colon, type);
    }

    /// <summary>
    /// Parses a type expression.
    /// </summary>
    /// <returns>The parsed <see cref="TypeSyntax"/>.</returns>
    private TypeSyntax ParseType() => this.ParseGenericLevelType();

    /// <summary>
    /// Parses a type expression with potential postfix notations, like member access or generics.
    /// </summary>
    /// <returns>The parsed <see cref="TypeSyntax"/>.</returns>
    private TypeSyntax ParseGenericLevelType()
    {
        var result = this.ParseAtomType();
        while (true)
        {
            var peek = this.Peek();
            if (peek == TokenKind.Dot)
            {
                // Member access
                var dot = this.Advance();
                var member = this.Expect(TokenKind.Identifier);
                result = new MemberTypeSyntax(result, dot, member);
            }
            else if (peek == TokenKind.LessThan)
            {
                // Generic instantiation
                var openBracket = this.Advance();
                var args = this.ParseSeparatedSyntaxList(
                    elementParser: this.ParseType,
                    separatorKind: TokenKind.Comma,
                    stopKind: TokenKind.GreaterThan);
                var closeBracket = this.Expect(TokenKind.GreaterThan);
                result = new GenericTypeSyntax(result, openBracket, args, closeBracket);
            }
            else
            {
                break;
            }
        }
        return result;
    }

    /// <summary>
    /// Parses an atomic type expression.
    /// </summary>
    /// <returns>The parsed <see cref="TypeSyntax"/>.</returns>
    private TypeSyntax ParseAtomType()
    {
        if (this.Matches(TokenKind.Identifier, out var typeName))
        {
            return new NameTypeSyntax(typeName);
        }
        else
        {
            var input = this.Synchronize(t => t switch
            {
                TokenKind.Semicolon or TokenKind.Comma
                or TokenKind.ParenClose or TokenKind.BracketClose
                or TokenKind.CurlyClose or TokenKind.InterpolationEnd
                or TokenKind.Assign => false,
                _ when IsExpressionStarter(t) => false,
                _ => true,
            });
            var info = DiagnosticInfo.Create(SyntaxErrors.UnexpectedInput, formatArgs: "type");
            var diag = new SyntaxDiagnosticInfo(info, Offset: 0, Width: input.FullWidth);
            var node = new UnexpectedTypeSyntax(input);
            this.AddDiagnostic(node, diag);
            return node;
        }
    }

    /// <summary>
    /// Parses any kind of control-flow expression, like a block, if or while expression.
    /// </summary>
    /// <param name="ctx">The current context we are in.</param>
    /// <returns>The parsed <see cref="ExpressionSyntax"/>.</returns>
    private ExpressionSyntax ParseControlFlowExpression(ControlFlowContext ctx)
    {
        var peekKind = this.Peek();
        Debug.Assert(peekKind is TokenKind.CurlyOpen
                              or TokenKind.KeywordIf
                              or TokenKind.KeywordWhile);
        return peekKind switch
        {
            TokenKind.CurlyOpen => this.ParseBlockExpression(ctx),
            TokenKind.KeywordIf => this.ParseIfExpression(ctx),
            TokenKind.KeywordWhile => this.ParseWhileExpression(ctx),
            _ => throw new InvalidOperationException(),
        };
    }

    /// <summary>
    /// Parses the body of a control-flow expression.
    /// </summary>
    /// <param name="ctx">The current context we are in.</param>
    /// <returns>The parsed <see cref="ExpressionSyntax"/>.</returns>
    private ExpressionSyntax ParseControlFlowBody(ControlFlowContext ctx)
    {
        if (ctx == ControlFlowContext.Expr)
        {
            // Only expressions, no semicolon needed
            return this.ParseExpression();
        }
        else
        {
            // Just a statement
            // Since this is a one-liner, we don't allow declarations as for example
            // if (x) var y = z;
            // makes no sense!
            var stmt = this.ParseStatement(allowDecl: false);
            return new StatementExpressionSyntax(stmt);
        }
    }

    /// <summary>
    /// Parses a code-block.
    /// </summary>
    /// <param name="ctx">The current context we are in.</param>
    /// <returns>The parsed <see cref="BlockExpressionSyntax"/>.</returns>
    private BlockExpressionSyntax ParseBlockExpression(ControlFlowContext ctx)
    {
        var parsed = this.ParseBlock(ctx);
        return new BlockExpressionSyntax(
            openBrace: parsed.OpenCurly,
            statements: parsed.Statements,
            value: parsed.Value,
            closeBrace: parsed.CloseCurly);
    }

    private ParsedBlock ParseBlock(ControlFlowContext ctx)
    {
        var openBrace = this.Expect(TokenKind.CurlyOpen);
        var stmts = SyntaxList.CreateBuilder<StatementSyntax>();
        ExpressionSyntax? value = null;
        while (true)
        {
            switch (this.Peek())
            {
            case TokenKind.EndOfInput:
            case TokenKind.CurlyClose:
                // On a close curly or out of input, we can immediately exit
                goto end_of_block;

            case TokenKind t when this.IsDeclarationStarter(t):
            {
                var decl = this.ParseDeclaration(DeclarationContext.Local);
                stmts.Add(new DeclarationStatementSyntax(decl));
                break;
            }

            case TokenKind.CurlyOpen:
            case TokenKind.KeywordIf:
            case TokenKind.KeywordWhile:
            {
                var expr = this.ParseControlFlowExpression(ctx);
                if (ctx == ControlFlowContext.Expr && this.Peek() == TokenKind.CurlyClose)
                {
                    // Treat as expression
                    value = expr;
                    goto end_of_block;
                }
                // Just a statement
                stmts.Add(new ExpressionStatementSyntax(expr, null));
                break;
            }

            default:
            {
                if (IsExpressionStarter(this.Peek()))
                {
                    // Some expression
                    var expr = this.ParseExpression();
                    if (ctx == ControlFlowContext.Stmt || this.Peek() != TokenKind.CurlyClose)
                    {
                        // Likely just a statement, can continue
                        var semicolon = this.Expect(TokenKind.Semicolon);
                        stmts.Add(new ExpressionStatementSyntax(expr, semicolon));
                    }
                    else
                    {
                        // This is the value of the block
                        value = expr;
                        goto end_of_block;
                    }
                }
                else
                {
                    // Error, synchronize
                    var input = this.Synchronize(kind => kind switch
                    {
                        TokenKind.CurlyClose => false,
                        _ when this.IsDeclarationStarter(kind) => false,
                        _ when IsExpressionStarter(kind) => false,
                        _ => true,
                    });
                    var info = DiagnosticInfo.Create(SyntaxErrors.UnexpectedInput, formatArgs: "statement");
                    var diag = new SyntaxDiagnosticInfo(info, Offset: 0, Width: input.FullWidth);
                    var errNode = new UnexpectedStatementSyntax(input);
                    this.AddDiagnostic(errNode, diag);
                    stmts.Add(errNode);
                }
                break;
            }
            }
        }
    end_of_block:
        var closeBrace = this.Expect(TokenKind.CurlyClose);
        return new(openBrace, stmts.ToSyntaxList(), value, closeBrace);
    }

    /// <summary>
    /// Parses an if-expression.
    /// </summary>
    /// <param name="ctx">The current context we are in.</param>
    /// <returns>The parsed <see cref="IfExpressionSyntax"/>.</returns>
    private IfExpressionSyntax ParseIfExpression(ControlFlowContext ctx)
    {
        var ifKeyword = this.Expect(TokenKind.KeywordIf);
        var openParen = this.Expect(TokenKind.ParenOpen);
        var condition = this.ParseExpression();
        var closeParen = this.Expect(TokenKind.ParenClose);
        var thenBody = this.ParseControlFlowBody(ctx);

        ElseClauseSyntax? elsePart = null;
        if (this.Matches(TokenKind.KeywordElse, out var elseKeyword))
        {
            var elseBody = this.ParseControlFlowBody(ctx);
            elsePart = new(elseKeyword, elseBody);
        }

        return new(ifKeyword, openParen, condition, closeParen, thenBody, elsePart);
    }

    /// <summary>
    /// Parses a while-expression.
    /// </summary>
    /// <param name="ctx">The current context we are in.</param>
    /// <returns>The parsed <see cref="WhileExpressionSyntax"/>.</returns>
    private WhileExpressionSyntax ParseWhileExpression(ControlFlowContext ctx)
    {
        var whileKeyword = this.Expect(TokenKind.KeywordWhile);
        var openParen = this.Expect(TokenKind.ParenOpen);
        var condition = this.ParseExpression();
        var closeParen = this.Expect(TokenKind.ParenClose);
        var body = this.ParseControlFlowBody(ctx);
        return new(whileKeyword, openParen, condition, closeParen, body);
    }

    /// <summary>
    /// Parses an expression.
    /// </summary>
    /// <returns>The parsed <see cref="ExpressionSyntax"/>.</returns>
    internal ExpressionSyntax ParseExpression() => this.ParseExpression(0);

    /// <summary>
    /// Parses an expression.
    /// </summary>
    /// <param name="level">The current precedence level.</param>
    /// <returns>The parsed <see cref="ExpressionSyntax"/>.</returns>
    private ExpressionSyntax ParseExpression(int level) => level switch
    {
        // Finally the pseudo-statement-like constructs
        0 => this.ParsePseudoStatementLevelExpression(level),
        // Then assignment and compound assignment, which are **RIGHT ASSOCIATIVE**
        1 => this.BinaryRight(
            TokenKind.Assign,
            TokenKind.PlusAssign, TokenKind.MinusAssign,
            TokenKind.StarAssign, TokenKind.SlashAssign)(level),
        // Then binary or
        2 => this.BinaryLeft(TokenKind.KeywordOr)(level),
        // Then binary and
        3 => this.BinaryLeft(TokenKind.KeywordAnd)(level),
        // Then unary not
        4 => this.Prefix(TokenKind.KeywordNot)(level),
        // Then relational operators
        5 => this.ParseRelationalLevelExpression(level),
        // Then binary +, -
        6 => this.BinaryLeft(TokenKind.Plus, TokenKind.Minus)(level),
        // Then binary *, /, mod, rem
        7 => this.BinaryLeft(TokenKind.Star, TokenKind.Slash, TokenKind.KeywordMod, TokenKind.KeywordRem)(level),
        // Then prefix unary + and -
        8 => this.Prefix(TokenKind.Plus, TokenKind.Minus)(level),
        // Then comes call, indexing and member access
        9 => this.ParseCallLevelExpression(level),
        // Max precedence is atom
        10 => this.ParseAtomExpression(level),
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    // Plumbing code for precedence parsing

    private ExpressionSyntax ParsePseudoStatementLevelExpression(int level)
    {
        switch (this.Peek())
        {
        case TokenKind.KeywordReturn:
        {
            var returnKeyword = this.Advance();
            ExpressionSyntax? value = null;
            if (IsExpressionStarter(this.Peek())) value = this.ParseExpression();
            return new ReturnExpressionSyntax(returnKeyword, value);
        }
        case TokenKind.KeywordGoto:
        {
            var gotoKeyword = this.Advance();
            var labelName = this.Expect(TokenKind.Identifier);
            return new GotoExpressionSyntax(gotoKeyword, new NameLabelSyntax(labelName));
        }
        default:
            return this.ParseExpression(level + 1);
        }
    }

    private ExpressionSyntax ParseRelationalLevelExpression(int level)
    {
        var left = this.ParseExpression(level + 1);
        var comparisons = SyntaxList.CreateBuilder<ComparisonElementSyntax>();
        while (true)
        {
            var opKind = this.Peek();
            if (!SyntaxFacts.IsRelationalOperator(opKind)) break;
            var op = this.Advance();
            var right = this.ParseExpression(level + 1);
            comparisons.Add(new(op, right));
        }
        return comparisons.Count == 0
            ? left
            : new RelationalExpressionSyntax(left, comparisons.ToSyntaxList());
    }

    private ExpressionSyntax ParseCallLevelExpression(int level)
    {
        var result = this.ParseExpression(level + 1);
        while (true)
        {
            var peek = this.Peek();
            if (peek == TokenKind.ParenOpen)
            {
                var openParen = this.Expect(TokenKind.ParenOpen);
                var args = this.ParseSeparatedSyntaxList(
                    elementParser: this.ParseExpression,
                    separatorKind: TokenKind.Comma,
                    stopKind: TokenKind.ParenClose);
                var closeParen = this.Expect(TokenKind.ParenClose);
                result = new CallExpressionSyntax(result, openParen, args, closeParen);
            }
            else if (peek == TokenKind.BracketOpen)
            {
                var openBracket = this.Expect(TokenKind.BracketOpen);
                var args = this.ParseSeparatedSyntaxList(
                    elementParser: this.ParseExpression,
                    separatorKind: TokenKind.Comma,
                    stopKind: TokenKind.BracketClose);
                var closeBracket = this.Expect(TokenKind.BracketClose);
                result = new IndexExpressionSyntax(result, openBracket, args, closeBracket);
            }
            else if (peek == TokenKind.LessThan
                  && CanBeGenericInstantiated(result)
                  && this.DisambiguateLessThan() == LessThanDisambiguation.Generics)
            {
                // Generic instantiation
                var openBracket = this.Expect(TokenKind.LessThan);
                var args = this.ParseSeparatedSyntaxList(
                    elementParser: this.ParseType,
                    separatorKind: TokenKind.Comma,
                    stopKind: TokenKind.GreaterThan);
                var closeBracket = this.Expect(TokenKind.GreaterThan);
                result = new GenericExpressionSyntax(result, openBracket, args, closeBracket);
            }
            else if (this.Matches(TokenKind.Dot, out var dot))
            {
                var name = this.Expect(TokenKind.Identifier);
                result = new MemberExpressionSyntax(result, dot, name);
            }
            else
            {
                break;
            }
        }
        return result;
    }

    private ExpressionSyntax ParseAtomExpression(int level)
    {
        switch (this.Peek())
        {
        case TokenKind.LiteralInteger:
        case TokenKind.LiteralFloat:
        case TokenKind.LiteralCharacter:
        {
            var value = this.Advance();
            return new LiteralExpressionSyntax(value);
        }
        case TokenKind.KeywordTrue:
        case TokenKind.KeywordFalse:
        {
            var value = this.Advance();
            return new LiteralExpressionSyntax(value);
        }
        case TokenKind.LineStringStart:
            return this.ParseLineString();
        case TokenKind.MultiLineStringStart:
            return this.ParseMultiLineString();
        case TokenKind.Identifier:
        {
            var name = this.Advance();
            return new NameExpressionSyntax(name);
        }
        case TokenKind.ParenOpen:
        {
            var openParen = this.Expect(TokenKind.ParenOpen);
            var expr = this.ParseExpression();
            var closeParen = this.Expect(TokenKind.ParenClose);
            return new GroupingExpressionSyntax(openParen, expr, closeParen);
        }
        case TokenKind.CurlyOpen:
        case TokenKind.KeywordIf:
        case TokenKind.KeywordWhile:
            return this.ParseControlFlowExpression(ControlFlowContext.Expr);
        default:
        {
            var input = this.Synchronize(t => t switch
            {
                TokenKind.Semicolon or TokenKind.Comma
                or TokenKind.ParenClose or TokenKind.BracketClose
                or TokenKind.CurlyClose or TokenKind.InterpolationEnd => false,
                var kind when IsExpressionStarter(kind) => false,
                _ => true,
            });
            var info = DiagnosticInfo.Create(SyntaxErrors.UnexpectedInput, formatArgs: "expression");
            var diag = new SyntaxDiagnosticInfo(info, Offset: 0, Width: input.FullWidth);
            var node = new UnexpectedExpressionSyntax(input);
            this.AddDiagnostic(node, diag);
            return node;
        }
        }
    }

    /// <summary>
    /// Parses a line string expression.
    /// </summary>
    /// <returns>The parsed <see cref="StringExpressionSyntax"/>.</returns>
    private StringExpressionSyntax ParseLineString()
    {
        var openQuote = this.Expect(TokenKind.LineStringStart);
        var content = SyntaxList.CreateBuilder<StringPartSyntax>();
        while (true)
        {
            var peek = this.Peek();
            if (peek == TokenKind.StringContent)
            {
                var part = this.Advance();
                content.Add(new TextStringPartSyntax(part));
            }
            else if (peek == TokenKind.InterpolationStart)
            {
                var start = this.Advance();
                var expr = this.ParseExpression();
                var end = this.Expect(TokenKind.InterpolationEnd);
                content.Add(new InterpolationStringPartSyntax(start, expr, end));
            }
            else
            {
                // We need a close quote for line strings then
                break;
            }
        }
        var closeQuote = this.Expect(TokenKind.LineStringEnd);
        return new(openQuote, content.ToSyntaxList(), closeQuote);
    }

    /// <summary>
    /// Parses a multi-line string expression.
    /// </summary>
    /// <returns>The parsed <see cref="StringExpressionSyntax"/>.</returns>
    private StringExpressionSyntax ParseMultiLineString()
    {
        var openQuote = this.Expect(TokenKind.MultiLineStringStart);
        var content = SyntaxList.CreateBuilder<StringPartSyntax>();
        // We check if there's a newline
        if (!openQuote.TrailingTrivia.Any(t => t.Kind == TriviaKind.Newline))
        {
            // Possible stray tokens inline
            var input = this.Synchronize(t => t switch
            {
                TokenKind.MultiLineStringEnd or TokenKind.StringNewline => false,
                _ => true,
            });
            var info = DiagnosticInfo.Create(SyntaxErrors.ExtraTokensInlineWithOpenQuotesOfMultiLineString);
            var diag = new SyntaxDiagnosticInfo(info, Offset: 0, Width: input.FullWidth);
            var unexpected = new UnexpectedStringPartSyntax(input);
            this.AddDiagnostic(unexpected, diag);
            content.Add(unexpected);
        }
        while (true)
        {
            var peek = this.Peek();
            if (peek == TokenKind.StringContent || peek == TokenKind.StringNewline)
            {
                var part = this.Advance();
                content.Add(new TextStringPartSyntax(part));
            }
            else if (peek == TokenKind.InterpolationStart)
            {
                var start = this.Advance();
                var expr = this.ParseExpression();
                var end = this.Expect(TokenKind.InterpolationEnd);
                content.Add(new InterpolationStringPartSyntax(start, expr, end));
            }
            else
            {
                // We need a close quote for line strings then
                break;
            }
        }
        var closeQuote = this.Expect(TokenKind.MultiLineStringEnd);
        // We need to check if the close quote is on a newline
        // There are 2 cases:
        //  - the leading trivia of the closing quotes contains a newline
        //  - the string is empty and the opening quotes trailing trivia contains a newline
        var isClosingQuoteOnNewline =
               closeQuote.LeadingTrivia.Count > 0
            || (content.Count == 0 && openQuote.TrailingTrivia.Any(t => t.Kind == TriviaKind.Newline));
        if (isClosingQuoteOnNewline)
        {
            Debug.Assert(closeQuote.LeadingTrivia.Count <= 2);
            Debug.Assert(openQuote.TrailingTrivia.Any(t => t.Kind == TriviaKind.Newline)
                      || closeQuote.LeadingTrivia.Any(t => t.Kind == TriviaKind.Newline));
            if (closeQuote.LeadingTrivia.Count == 2)
            {
                // The first trivia was newline, the second must be spaces
                Debug.Assert(closeQuote.LeadingTrivia[1].Kind == TriviaKind.Whitespace);
                // We take the whitespace text and check if every line in the string obeys that as a prefix
                var prefix = closeQuote.LeadingTrivia[1].Text;
                var nextIsNewline = true;
                foreach (var part in content)
                {
                    if (part is TextStringPartSyntax textPart)
                    {
                        if (textPart.Content.Kind == TokenKind.StringNewline)
                        {
                            // Also a newline, don't care, even an empty line is fine
                            nextIsNewline = true;
                            continue;
                        }
                        // Actual text content
                        if (nextIsNewline && !textPart.Content.Text.StartsWith(prefix))
                        {
                            // We are in a newline and the prefixes don't match, that's an error
                            var whitespaceLength = textPart.Content.Text.TakeWhile(char.IsWhiteSpace).Count();
                            var info = DiagnosticInfo.Create(SyntaxErrors.InsufficientIndentationInMultiLinString);
                            var diag = new SyntaxDiagnosticInfo(info, Offset: 0, Width: whitespaceLength);
                            this.AddDiagnostic(textPart, diag);
                        }
                        else
                        {
                            // Indentation was ok
                        }
                        nextIsNewline = false;
                    }
                    else
                    {
                        // Interpolation, don't care
                        nextIsNewline = false;
                    }
                }
            }
        }
        else
        {
            // Error, the closing quotes are not on a newline
            var info = DiagnosticInfo.Create(SyntaxErrors.ClosingQuotesOfMultiLineStringNotOnNewLine);
            var diag = new SyntaxDiagnosticInfo(info, Offset: 0, Width: closeQuote.FullWidth);
            this.AddDiagnostic(closeQuote, diag);
        }
        return new(openQuote, content.ToSyntaxList(), closeQuote);
    }

    /// <summary>
    /// Checks, if a given syntax can be followed by a generic argument list.
    /// </summary>
    /// <param name="syntaxNode">The node to check.</param>
    /// <returns>True, if <paramref name="syntaxNode"/> can be followed by a generic argument list.</returns>
    private static bool CanBeGenericInstantiated(SyntaxNode syntaxNode) => syntaxNode
        is NameExpressionSyntax
        or NameTypeSyntax
        or MemberExpressionSyntax
        or MemberTypeSyntax;

    /// <summary>
    /// Attempts to disambiguate the upcoming less-than token.
    /// </summary>
    /// <returns>The result of the disambiguation.</returns>
    private LessThanDisambiguation DisambiguateLessThan()
    {
        var offset = 0;
        return this.DisambiguateLessThan(ref offset);
    }

    /// <summary>
    /// Attempts to disambiguate the upcoming less-than token.
    /// </summary>
    /// <param name="offset">The offset to start disambiguation from. The value will be updated to the farthest
    /// offset that was peeked to disambiguate. If the token turns out to be a generic argument list, it is set
    /// to the offset after the matching '>'.</param>
    /// <returns>The result of the disambiguation.</returns>
    private LessThanDisambiguation DisambiguateLessThan(ref int offset)
    {
        Debug.Assert(this.Peek(offset) == TokenKind.LessThan);

        // Skip '<'
        ++offset;
        while (true)
        {
            var peek = this.Peek(offset);

            switch (peek)
            {
            case TokenKind.Dot:
            case TokenKind.Comma:
            {
                // Just skip, legal here
                ++offset;
                break;
            }
            case TokenKind.Identifier:
            {
                ++offset;
                // We can have a nested generic here
                if (this.Peek(offset) == TokenKind.LessThan)
                {
                    // Judge this list then
                    var judgement = this.DisambiguateLessThan(ref offset);
                    // If a nested thing is not a generic list, we are not either
                    if (judgement == LessThanDisambiguation.Operator) return LessThanDisambiguation.Operator;
                    // Otherwise, it's still fair game to be both
                }
                break;
            }
            case TokenKind.GreaterThan:
            {
                // We could not decide, we peek one ahead to determine
                ++offset;
                var next = this.Peek(offset);
                return next switch
                {
                    TokenKind.ParenOpen => LessThanDisambiguation.Generics,
                    _ when IsExpressionStarter(next) => LessThanDisambiguation.Operator,
                    _ => LessThanDisambiguation.Generics,
                };
            }
            default:
                // Illegal in generics
                return LessThanDisambiguation.Operator;
            }
        }
    }

    // General utilities

    /// <summary>
    /// Parses a <see cref="SeparatedSyntaxList{TNode}"/>.
    /// </summary>
    /// <typeparam name="TNode">The element type of the list.</typeparam>
    /// <param name="elementParser">The parser function that parses a single element.</param>
    /// <param name="separatorKind">The kind of the separator token.</param>
    /// <param name="stopKind">The kind of the token that definitely ends this construct.</param>
    /// <returns>The parsed <see cref="SeparatedSyntaxList{TNode}"/>.</returns>
    private SeparatedSyntaxList<TNode> ParseSeparatedSyntaxList<TNode>(
        Func<TNode> elementParser,
        TokenKind separatorKind,
        TokenKind stopKind)
        where TNode : SyntaxNode
    {
        var elements = SeparatedSyntaxList.CreateBuilder<TNode>();
        while (true)
        {
            // Stop token met, don't go further
            if (this.Peek() == stopKind) break;
            // Parse an element
            var element = elementParser();
            elements.Add(element);
            // If the next token is not a punctuation, we are done
            if (!this.Matches(separatorKind, out var punct)) break;
            // We had a punctuation, we can continue
            elements.Add(punct);
        }
        return elements.ToSeparatedSyntaxList();
    }

    private SyntaxToken? ParseVisibilityModifier() => IsVisibilityModifier(this.Peek()) ? this.Advance() : null;

    // Token-level operators

    /// <summary>
    /// Performs synchronization, meaning it consumes <see cref="SyntaxToken"/>s from the input
    /// while a given condition is met.
    /// </summary>
    /// <param name="keepGoing">The predicate that dictates if the consumption should keep going.</param>
    /// <returns>The consumed list of <see cref="SyntaxToken"/>s as <see cref="SyntaxNode"/>s.</returns>
    private SyntaxList<SyntaxNode> Synchronize(Func<TokenKind, bool> keepGoing)
    {
        // NOTE: A possible improvement could be to track opening and closing token pairs optionally
        var input = SyntaxList.CreateBuilder<SyntaxNode>();
        while (true)
        {
            var peek = this.Peek();
            if (peek == TokenKind.EndOfInput) break;
            if (!keepGoing(peek)) break;
            input.Add(this.Advance());
        }
        return input.ToSyntaxList();
    }

    /// <summary>
    /// Expects a certain kind of token to be at the current position.
    /// If it is, the token is consumed.
    /// </summary>
    /// <param name="kind">The expected <see cref="TokenKind"/>.</param>
    /// <returns>The consumed <see cref="SyntaxToken"/>.</returns>
    private SyntaxToken Expect(TokenKind kind)
    {
        if (!this.Matches(kind, out var token))
        {
            // We construct an empty token that signals that this is missing from the tree
            // The attached diagnostic message describes what is missing
            var friendlyName = SyntaxFacts.GetUserFriendlyName(kind);
            var info = DiagnosticInfo.Create(SyntaxErrors.ExpectedToken, formatArgs: friendlyName);
            var diag = new SyntaxDiagnosticInfo(info, Offset: 0, Width: 0);
            token = SyntaxToken.From(kind, string.Empty);
            this.AddDiagnostic(token, diag);
        }
        return token;
    }

    /// <summary>
    /// Checks if the upcoming token has kind <paramref name="kind"/>.
    /// If it is, the token is consumed.
    /// </summary>
    /// <param name="kind">The <see cref="TokenKind"/> to match.</param>
    /// <param name="token">The matched token is written here.</param>
    /// <returns>True, if the upcoming token is of kind <paramref name="kind"/>.</returns>
    private bool Matches(TokenKind kind, [MaybeNullWhen(false)] out SyntaxToken token)
    {
        if (this.Peek() == kind)
        {
            token = this.Advance();
            return true;
        }
        else
        {
            token = null;
            return false;
        }
    }

    /// <summary>
    /// Peeks ahead the kind of a token in the token source.
    /// </summary>
    /// <param name="offset">The amount to peek ahead.</param>
    /// <returns>The <see cref="TokenKind"/> of the <see cref="SyntaxToken"/> that is <paramref name="offset"/>
    /// ahead.</returns>
    private TokenKind Peek(int offset = 0) =>
        this.tokenSource.Peek(offset).Kind;

    /// <summary>
    /// Advances the parser in the token source with one token.
    /// </summary>
    /// <returns>The consumed <see cref="SyntaxToken"/>.</returns>
    private SyntaxToken Advance()
    {
        var token = this.tokenSource.Peek();
        this.tokenSource.Advance();
        return token;
    }

    private void AddDiagnostic(SyntaxNode node, SyntaxDiagnosticInfo diagnostic) =>
        this.diagnostics.Add(node, diagnostic);
}
