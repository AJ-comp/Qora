using System.Collections.Generic;
using System.Linq;
using Qora.Ir;
using Qora.Ir.Passes;

namespace Qora.Tests;

/// <summary>
/// The stable-node-Id + persistent <see cref="SemanticModel"/> architecture: every IR node carries an
/// <c>Id</c> minted at <c>new</c> and inherited by <c>with</c>; subtree copiers re-mint via
/// <see cref="ReId"/> (recording lineage into the model when they run after validation); and the model
/// built at the final Validate joins a node's FINAL name (post-mangling) to its validation-time facts.
/// These pin the record-semantics foundation, Id uniqueness through the whole pipeline, the
/// Id→Symbol join, and the DerivedFrom lineage chain.
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
        var r = QoraParser.Parse(
            "operation Flip(q: Qubit[]){ for i in 0..q.Count-1 { X(q[i]); } }\n" +
            "operation Main(){ use a=Qubit[2]; use b=Qubit[3]; Flip(a); Flip(b); Adjoint Flip(a); }");
        Assert.True(r.Success, string.Join(" | ", r.Errors));
        Assert.DoesNotContain(r.Errors, e => e.Code == "QINTERNAL");
    }

    // --- 4. the model joins a final node Id to its validation-time symbol — even when the SURFACE name
    //        will be renamed at emission (`x` collides with the gate x and gets mangled). ---

    [Fact]
    public void SemanticsFindSymbolReturnsValidationTimeTypeById()
    {
        var r = QoraParser.Parse("operation Main(){ use q=Qubit[1]; const x: int = 1; H(q[0]); }");
        Assert.True(r.Success, string.Join(" | ", r.Errors));
        Assert.NotNull(r.Semantics);

        var decl = r.Ir!.Operations.SelectMany(o => o.Body).OfType<QDecl>().Single(d => d.Name == "x");
        var sym = r.Semantics!.FindSymbol(decl.Id);
        Assert.NotNull(sym);
        Assert.Equal(QType.Int, sym!.Type);
        Assert.True(sym.IsConst);
    }

    // --- 4b. the `--stages` symbols view: rendering from the persisted model must produce the exact
    //         text the on-demand rebuild produced (the generic op exercises the still-generic-op
    //         fallback, since the model keys the post-monomorphize specializations). ---

    [Fact]
    public void ModelBasedSymbolFormatMatchesRebuildText()
    {
        var r = QoraParser.Parse(
            "operation Flip(q: Qubit[]){ for i in 0..q.Count-1 { X(q[i]); } }\n" +
            "operation Main(){ use a=Qubit[2]; const x: int = 1; Flip(a); }");
        Assert.True(r.Success, string.Join(" | ", r.Errors));
        Assert.Equal(SymbolTableBuilder.Format(r.Ir), SymbolTableBuilder.Format(r.Ir, r.Semantics));
    }

    // --- 4c. the two name domains, each with ONE home: Symbol.SourceName keeps the user's spelling
    //         forever (diagnostics, symbol views), while the emitted (post-mangling) name is the
    //         mangler's own fact recorded on the model. `x` collides with the stdgates gate x → x_;
    //         `q` doesn't collide and is still recorded, because a null FindEmittedName must mean
    //         "the mangler never saw this node", never "unchanged". ---

    [Fact]
    public void ManglerRecordsEmittedNamesAndSourceNameStaysUserSpelling()
    {
        var r = QoraParser.Parse("operation Main(){ use q=Qubit[1]; const x: int = 1; H(q[0]); }");
        Assert.True(r.Success, string.Join(" | ", r.Errors));
        Assert.NotNull(r.Semantics);

        var decl = r.Ir!.Operations.SelectMany(o => o.Body).OfType<QDecl>().Single(d => d.Name == "x");
        var use = r.Ir!.Operations.SelectMany(o => o.Body).OfType<QUse>().Single();

        var sym = r.Semantics!.FindSymbol(decl.Id);
        Assert.Equal("x", sym!.SourceName);                          // source domain: frozen user spelling
        Assert.Equal("x_", r.Semantics.FindEmittedName(decl.Id));    // emitted domain: renamed past the gate x
        Assert.Equal("q", r.Semantics.FindEmittedName(use.Id));      // an unchanged name is recorded too
        Assert.Contains("x_", r.Qasm);                               // and the QASM really uses the emitted name
    }

    // --- 4d. every declaring NODE gets its OWN emitted name. Same-name declarations in DISJOINT sibling
    //         blocks are different variables, and QASM's def scope is flat, so they must not share one
    //         emitted identifier — sharing merged two variables into one storage (silently wrong results).
    //         Each still records a fact; a null would read as "the mangler never saw this node". ---

    [Fact]
    public void SiblingSameNameDeclsGetDistinctEmittedNames()
    {
        var r = QoraParser.Parse(
            "operation Main(){ use q=Qubit[2]; for i in 0..1 { H(q[i]); } for i in 0..1 { X(q[i]); } }");
        Assert.True(r.Success, string.Join(" | ", r.Errors));

        var fors = r.Ir!.Operations.Single().Body.OfType<QFor>().ToList();
        Assert.Equal(2, fors.Count);
        var first = r.Semantics!.FindEmittedName(fors[0].Id);
        var second = r.Semantics.FindEmittedName(fors[1].Id);
        Assert.False(string.IsNullOrEmpty(first));    // every declaring node records a fact
        Assert.False(string.IsNullOrEmpty(second));
        Assert.NotEqual(first, second);               // ...and two distinct declarations never share one
    }

    // --- 4e. the parameter and operation record sites (separate code from CollectDecls): a def-local
    //         param colliding with the stdgates gate x lands as x_ on the PARAM node, and both op nodes
    //         carry their def names — the entry keeps its own name yet is still recorded. ---

    [Fact]
    public void ParameterAndOperationNodesCarryEmittedNameFacts()
    {
        var r = QoraParser.Parse(
            "operation Foo(x: Qubit[]){ H(x[0]); }\noperation Main(){ use q=Qubit[1]; Foo(q); }");
        Assert.True(r.Success, string.Join(" | ", r.Errors));

        var foo = r.AnalyzedIr!.Operations.Single(o => o.DisplayName == "Foo");
        var main = r.AnalyzedIr.Operations.Single(o => o.Name == "Main");
        Assert.Equal("x_", r.Semantics!.FindEmittedName(foo.Params.Single().Id));
        Assert.Equal(foo.Name, r.Semantics.FindEmittedName(foo.Id));
        Assert.Equal("Main", r.Semantics.FindEmittedName(main.Id));
        Assert.Contains($"def {foo.Name}(qubit[1] x_)", r.Qasm);
    }

    // --- 4f. unit-level Mangle: a namespaced op's DOT-FLATTENED def name lands on the op node's Id, and
    //         null-before / non-null-after pins the "null means the mangler has not seen this node,
    //         never 'unchanged'" half of the FindEmittedName contract. ---

    [Fact]
    public void MangleFlattensNamespacedOpNameOntoOpNodeAndNullMeansNotYetMangled()
    {
        var bell = new QOperation("MyLib.Bell", new List<QParam>(), new List<QStmt>());
        var main = new QOperation("Main", new List<QParam>(), new List<QStmt>
        {
            new QUse("a", 1), Gate("H", Q("a", 0)),
        });
        var program = new QProgram(new List<QOperation> { bell, main });
        var model = new SemanticModel();

        Assert.Null(model.FindEmittedName(bell.Id));                    // before mangling: never seen
        NameMangler.Mangle(program, model);
        Assert.Equal("MyLib_Bell", model.FindEmittedName(bell.Id));     // dots flatten onto the op node
        Assert.Equal("Main", model.FindEmittedName(main.Id));           // entry keeps its name, still recorded
    }

    // --- 4g. operations are symbols too: FindSymbol(op.Id) resolves to an Operation-kind symbol whose
    //         SourceName is the op name, and whose Uses accrue one entry per call site across all callers.
    //         An uncalled entry op has zero uses. ---

    [Fact]
    public void OperationIsASymbolWithOneUsePerCallSite()
    {
        var r = QoraParser.Parse(
            "operation Foo(q: Qubit[]){ H(q[0]); }\n" +
            "operation Main(){ use a=Qubit[1]; use b=Qubit[1]; Foo(a); Foo(b); }");
        Assert.True(r.Success, string.Join(" | ", r.Errors));

        var foo = r.AnalyzedIr!.Operations.Single(o => o.DisplayName == "Foo");
        var main = r.AnalyzedIr.Operations.Single(o => o.Name == "Main");

        var fooSym = r.Semantics!.FindSymbol(foo.Id);
        Assert.NotNull(fooSym);
        Assert.Equal(SymbolKind.Operation, fooSym!.Kind);
        Assert.Equal(foo.Name, fooSym.SourceName);
        Assert.Equal(2, fooSym.Uses.Count);                          // called from Main twice

        Assert.Empty(r.Semantics.FindSymbol(main.Id)!.Uses);         // the entry op is never called
    }

    // --- 5. ConjugationLowering installs the inverse NEXT TO the originals in the same body: the copies
    //        must carry fresh Ids (else every Id-keyed table sees each within-statement twice), and the
    //        DerivedFrom chain must resolve a copy's Id back to the source statement's symbol. ---

    [Fact]
    public void ConjugationInverseHasFreshIdsAndLineageReachesSource()
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

        var (lowered, errors) = ConjugationLowering.Run(program, model);
        Assert.Empty(errors);

        // use a; k; X; H; k(copy); Adjoint X(copy) — inverse re-emits decls first, then reversed gates.
        var body = lowered.Operations.Single().Body;
        Assert.Equal(6, body.Count);
        var declCopy = Assert.IsType<QDecl>(body[4]);
        var gateCopy = Assert.IsType<QGate>(body[5]);
        Assert.Equal("Adjoint", Assert.Single(gateCopy.Functors));

        Assert.NotEqual(withinDecl.Id, declCopy.Id);                       // fresh identity for the copy
        Assert.NotEqual(withinGate.Id, gateCopy.Id);
        Assert.Equal(body.Count, body.Select(s => s.Id).Distinct().Count());  // no duplicate in the body

        // lineage: the copy's Id resolves — through the DerivedFrom chain — to the SOURCE decl's symbol.
        var viaCopy = model!.FindSymbol(declCopy.Id);
        Assert.NotNull(viaCopy);
        Assert.Same(model.FindSymbol(withinDecl.Id), viaCopy);
    }
}
