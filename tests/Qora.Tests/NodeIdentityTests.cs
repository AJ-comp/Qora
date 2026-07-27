using System.Collections.Generic;
using System.Linq;
using Qora.Compiler;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// The stable-node-Id + persistent <see cref="HirSemanticModel"/> architecture: every IR node carries an
/// <c>Id</c> minted at <c>new</c> and inherited by <c>with</c>; subtree copiers re-mint via
/// <see cref="ReId"/> and return explicit lineage edges; the validation model remains bound to its exact
/// HIR snapshot; and target names live in the OpenQASM artifact. These tests pin record semantics, Id
/// uniqueness, the Id→Symbol join, returned copy lineage, and target naming ownership.
/// </summary>
public class NodeIdentityTests
{
    private static QQubitArg Q(string reg, int i) => new(reg, i.ToString());
    private static QGate Gate(string name, params QArg[] args) => new(new List<string>(), name, args.ToList());

    // --- 1. record semantics: the whole design rests on `with` inheriting Id and `new` minting one ---

    [Fact]
    public void WithCopyKeepsId_NewNodeGetsFreshId()
    {
        var gate = Gate("X", Q("q", 0));
        var renamed = gate with { Name = "Y" };
        Assert.Equal(gate.Id, renamed.Id);          // `with` = same node, edited — identity preserved

        var other = Gate("X", Q("q", 0));
        Assert.NotEqual(gate.Id, other.Id);          // `new` = a different node — fresh identity
    }

    // --- 2. ReId re-mints recursively and reports (sourceId, freshId) lineage ---

    [Fact]
    public void ReIdMintsFreshIdsRecursivelyAndRecordsLineage()
    {
        var inner = Gate("X", Q("q", 0));
        var loop = new QFor("i", new QNumLit(0), new QNumLit(1), new List<QStmt> { inner });
        var lineage = new Dictionary<int, int>();    // freshId -> sourceId

        var fresh = ReId.Run(new List<QStmt> { loop }, (src, fr) => lineage[fr] = src);

        var freshLoop = Assert.IsType<QFor>(fresh.Single());
        var freshInner = Assert.IsType<QGate>(freshLoop.Body.Single());
        Assert.NotEqual(loop.Id, freshLoop.Id);
        Assert.NotEqual(inner.Id, freshInner.Id);
        Assert.Equal(loop.Id, lineage[freshLoop.Id]);    // lineage reaches the source, per node
        Assert.Equal(inner.Id, lineage[freshInner.Id]);
    }

    // --- 3. whole-pipeline uniqueness: two specializations of one generic + a whole-op Adjoint is the
    //        maximal subtree-copying workload (Monomorphizer + AdjointMaterializer). ReferentialCheck's
    //        Id-uniqueness sweep runs before emission, so a clean compile IS the uniqueness assertion —
    //        any duplicated Id would surface as QINTERNAL and fail this. ---

    [Fact]
    public void PipelineWithSpecializationsAndAdjointHasUniqueIds()
    {
        var compilation = QoraCompiler.Compile(
            "operation Flip(q: Qubit[]){ for i in 0..q.Count-1 { X(q[i]); } }\n" +
            "operation Main(){ use a=Qubit[2]; use b=Qubit[3]; Flip(a); Flip(b); Adjoint Flip(a); }");
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(diagnostic => diagnostic.Error)));
        Assert.DoesNotContain(
            compilation.Diagnostics,
            diagnostic => diagnostic.Error.Code == "QINTERNAL");
    }

    // --- 4. the model joins a final node Id to its validation-time symbol — even when the SURFACE name
    //        will be renamed at emission (`x` collides with the gate x and gets mangled). ---

    [Fact]
    public void SemanticsFindSymbolReturnsValidationTimeTypeById()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main(){ use q=Qubit[1]; const x: int = 1; H(q[0]); }");
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(diagnostic => diagnostic.Error)));

        var analyzed = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        var semantics = analyzed.Model;
        var decl = analyzed.Program.Operations
            .SelectMany(operation => operation.Body)
            .OfType<QDecl>()
            .Single(declaration => declaration.Name == "x");
        var sym = semantics.FindSymbol(decl.Id);
        Assert.NotNull(sym);
        Assert.Equal(QType.Int, sym!.Type);
        Assert.True(sym.IsConst);
    }

    // --- 4b. the `--stages` symbols view reads the exact analyzed HIR/model pair. ---

    [Fact]
    public void SymbolFormatReadsExactAnalyzedSnapshot()
    {
        var compilation = QoraCompiler.Compile(
            "operation Flip(q: Qubit[]){ for i in 0..q.Count-1 { X(q[i]); } }\n" +
            "operation Main(){ use a=Qubit[2]; const x: int = 1; Flip(a); }");
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(diagnostic => diagnostic.Error)));

        var analyzed = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        var semantics = analyzed.Model;
        var formatted = SymbolTableBuilder.Format(analyzed.Program, semantics);
        Assert.Contains("Main: operation", formatted);
        Assert.Contains("x: const int = 1", formatted);
    }

    // --- 4c. the two name domains, each with ONE home: Symbol.SourceName keeps the user's spelling
    //         forever, while emitted names belong exclusively to the OpenQASM target artifact. ---

    [Fact]
    public void TargetSymbolMapOwnsEmittedNamesAndSourceNameStaysUserSpelling()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main(){ use q=Qubit[1]; const x: int = 1; H(q[0]); }");
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(diagnostic => diagnostic.Error)));

        var analyzed = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        var semantics = analyzed.Model;
        var decl = analyzed.Program.Operations
            .SelectMany(operation => operation.Body)
            .OfType<QDecl>()
            .Single(declaration => declaration.Name == "x");
        var use = analyzed.Program.Operations
            .SelectMany(operation => operation.Body)
            .OfType<QUse>()
            .Single();
        var artifact = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);

        var sym = semantics.FindSymbol(decl.Id);
        Assert.Equal("x", sym!.SourceName);                          // source domain: frozen user spelling
        Assert.Equal("x_", artifact.Program.Symbols.GetEmittedName(decl.Id));
        Assert.Equal("q", artifact.Program.Symbols.GetEmittedName(use.Id));
        Assert.Contains("x_", artifact.Text);
    }

    // --- 4d. every declaring NODE gets its OWN emitted name. Same-name declarations in DISJOINT sibling
    //         blocks are different variables, and QASM's def scope is flat, so they must not share one
    //         emitted identifier — sharing merged two variables into one storage (silently wrong results).
    //         Each still records a fact; a null would read as "the mangler never saw this node". ---

    [Fact]
    public void SiblingSameNameDeclsGetDistinctEmittedNames()
    {
        var compilation = QoraCompiler.Compile(
            "operation Main(){ use q=Qubit[2]; for i in 0..1 { H(q[i]); } for i in 0..1 { X(q[i]); } }");
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(diagnostic => diagnostic.Error)));

        var analyzed = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        var artifact = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);
        var fors = analyzed.Program.Operations.Single().Body
            .OfType<QFor>()
            .ToList();
        Assert.Equal(2, fors.Count);
        var first = artifact.Program.Symbols.GetEmittedName(fors[0].Id);
        var second = artifact.Program.Symbols.GetEmittedName(fors[1].Id);
        Assert.NotEqual(first, second);
    }

    // --- 4e. the parameter and operation record sites (separate code from CollectDecls): a def-local
    //         param colliding with the stdgates gate x lands as x_ on the PARAM node, and both op nodes
    //         carry their def names — the entry keeps its own name yet is still recorded. ---

    [Fact]
    public void ParameterAndOperationNodesCarryEmittedNameFacts()
    {
        var compilation = QoraCompiler.Compile(
            "operation Foo(x: Qubit[]){ H(x[0]); }\noperation Main(){ use q=Qubit[1]; Foo(q); }");
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(diagnostic => diagnostic.Error)));

        var analyzed = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        var artifact = Assert.IsType<OpenQasmArtifact>(
            compilation.Targets.OpenQasm);
        var foo = analyzed.Program.Operations
            .Single(operation => operation.DisplayName == "Foo");
        var main = analyzed.Program.Operations
            .Single(operation => operation.Name == "Main");
        var symbols = artifact.Program.Symbols;
        Assert.Equal("x_", symbols.GetEmittedName(foo.Params.Single().Id));
        Assert.Equal(foo.Name, symbols.GetEmittedName(foo.Id));
        Assert.Equal("Main", symbols.GetEmittedName(main.Id));
        Assert.Contains($"def {foo.Name}(qubit[1] x_)", artifact.Text);
    }

    // --- 4f. unit-level Mangle: a namespaced op's dot-flattened def name lands in the returned target map. ---

    [Fact]
    public void MangleFlattensNamespacedOpNameIntoTheTargetSymbolMap()
    {
        var bell = new QOperation("MyLib.Bell", new List<QParam>(), new List<QStmt>());
        var main = new QOperation("Main", new List<QParam>(), new List<QStmt>
        {
            new QUse("a", 1), Gate("H", Q("a", 0)),
        });
        var program = new QProgram(new List<QOperation> { bell, main });
        var result = NameMangler.Mangle(program);

        Assert.Equal(
            "MyLib_Bell",
            result.Symbols.GetEmittedName(bell.Id));
        Assert.Equal(
            "Main",
            result.Symbols.GetEmittedName(main.Id));
    }

    // --- 4g. operations are symbols too: FindSymbol(op.Id) resolves to an Operation-kind symbol whose
    //         SourceName is the op name, and whose Uses accrue one entry per call site across all callers.
    //         An uncalled entry op has zero uses. ---

    [Fact]
    public void OperationIsASymbolWithOneUsePerCallSite()
    {
        var compilation = QoraCompiler.Compile(
            "operation Foo(q: Qubit[]){ H(q[0]); }\n" +
            "operation Main(){ use a=Qubit[1]; use b=Qubit[1]; Foo(a); Foo(b); }");
        Assert.True(
            compilation.Succeeded,
            string.Join(
                " | ",
                compilation.Diagnostics.Select(diagnostic => diagnostic.Error)));

        var analyzed = Assert.IsType<HirSemanticArtifact>(compilation.Hir.EffectAnalysis);
        var semantics = analyzed.Model;
        var foo = analyzed.Program.Operations.Single(
            operation => operation.DisplayName == "Foo");
        var main = analyzed.Program.Operations.Single(
            operation => operation.Name == "Main");

        var fooSym = semantics.FindSymbol(foo.Id);
        Assert.NotNull(fooSym);
        Assert.Equal(SymbolKind.Operation, fooSym!.Kind);
        Assert.Equal(foo.Name, fooSym.SourceName);
        Assert.Equal(2, fooSym.Uses.Count);                          // called from Main twice

        Assert.Empty(semantics.FindSymbol(main.Id)!.Uses);          // the entry op is never called
    }

    // --- 5. ConjugationLowering installs the inverse NEXT TO the originals in the same body: the copies
    //        must carry fresh Ids (else every Id-keyed table sees each within-statement twice), and the
    //        DerivedFrom chain must resolve a copy's Id back to the source statement's symbol. ---

    [Fact]
    public void ConjugationInverseHasFreshIdsAndReturnsLineageToSource()
    {
        var withinDecl = new QDecl(true, QType.Int, "k", new QText(new QNumLit(1)));
        var withinGate = Gate("X", Q("a", 0));
        var program = new QProgram(new List<QOperation>
        {
            new("Main", new List<QParam>(), new List<QStmt>
            {
                new QUse("a", 1),
                new QConjugate(
                    Within: new List<QStmt> { withinDecl, withinGate },
                    Apply: new List<QStmt> { Gate("H", Q("a", 0)) }),
            }),
        });

        var valErrors = QoraValidator.Validate(program, out var model);
        Assert.Empty(valErrors);
        Assert.NotNull(model);

        var lowering = ConjugationLowering.Run(program);
        Assert.Empty(lowering.Errors);

        // use a; k; X; H; k(copy); Adjoint X(copy) — inverse re-emits decls first, then reversed gates.
        var body = lowering.Program.Operations.Single().Body;
        Assert.Equal(6, body.Count);
        var declCopy = Assert.IsType<QDecl>(body[4]);
        var gateCopy = Assert.IsType<QGate>(body[5]);
        Assert.Equal("Adjoint", Assert.Single(gateCopy.Functors));

        Assert.NotEqual(withinDecl.Id, declCopy.Id);                       // fresh identity for the copy
        Assert.NotEqual(withinGate.Id, gateCopy.Id);
        Assert.Equal(body.Count, body.Select(s => s.Id).Distinct().Count());  // no duplicate in the body

        Assert.Contains(
            lowering.Derivations,
            derivation => derivation.SourceNodeId == withinDecl.Id
                && derivation.DerivedNodeId == declCopy.Id);
        Assert.Contains(
            lowering.Derivations,
            derivation => derivation.SourceNodeId == withinGate.Id
                && derivation.DerivedNodeId == gateCopy.Id);
        Assert.NotNull(model!.FindSymbol(withinDecl.Id));
        Assert.Null(model.FindSymbol(declCopy.Id)); // copy lineage does not mutate the exact source model
    }
}
