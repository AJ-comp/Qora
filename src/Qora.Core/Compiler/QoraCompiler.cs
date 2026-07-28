using Qora.Ir;
using Qora.Ir.Mir;
using Qora.Ir.Passes;

namespace Qora.Compiler;

/// <summary>
/// Whole-program compiler entry point. Parsing is delegated to <see cref="QoraParser"/>; this type owns
/// HIR revisions, semantic analysis, MIR, cross-stage links, and target artifacts.
/// </summary>
public static class QoraCompiler
{
    public static Compilation Compile(
        string source,
        CompilationOptions? options = null) =>
        new CompilationSession().Compile(source, options);

    /// <summary>
    /// Compile a new immutable revision of the same logical compilation. The previous snapshot is never
    /// mutated; stable compilation identity plus a greater revision keeps all stage IDs unambiguous.
    /// </summary>
    public static Compilation Recompile(
        Compilation previous,
        string source,
        CompilationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(previous);
        return previous.Session.Recompile(previous, source, options);
    }

    internal static Compilation CompileSnapshot(
        string source,
        CompilationOptions options,
        CompilationSession session,
        CompilationRevision compilationRevision,
        CompilationRevision? parentRevision)
    {
        ArgumentNullException.ThrowIfNull(session);
        Compilation? compilation = null;
        Exception? failure = null;
        var worker = new Thread(
            () =>
            {
                try
                {
                    compilation = CompileOnCurrentThread(
                        source ?? string.Empty,
                        options,
                        session,
                        compilationRevision,
                        parentRevision);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            },
            maxStackSize: 64 * 1024 * 1024);

        worker.Start();
        worker.Join();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return compilation!;
    }

    private static Compilation CompileOnCurrentThread(
        string source,
        CompilationOptions options,
        CompilationSession session,
        CompilationRevision compilationRevision,
        CompilationRevision? parentRevision)
    {
        var compilationId = session.Id;
        var loadedSources = SourceGraphLoader.Load(
            source,
            options,
            compilationId,
            compilationRevision);
        var sources = loadedSources.Sources;
        var syntax = sources.EntrySyntax;
        var hirBuilder = new HirPipelineBuilder(
            compilationId,
            compilationRevision,
            loadedSources.ConstructionCore);
        var diagnostics = new List<CompilationDiagnostic>();
        MirSnapshot? mir = null;
        var targetArtifacts = new List<ITargetArtifact>();

        Compilation Finish()
        {
            var hir = hirBuilder.Build();
            return new Compilation(
                compilationId,
                compilationRevision,
                session,
                parentRevision,
                options,
                sources,
                hir,
                mir,
                new CrossStageLinks(hir, mir?.Links),
                new TargetArtifactSet(targetArtifacts),
                diagnostics);
        }

        void AddSourceDiagnostics(
            CompilationStage stage,
            IEnumerable<QoraError> errors,
            SourceDocumentRef fallback)
        {
            diagnostics.AddRange(
                errors.Select(error => new CompilationDiagnostic(
                    stage,
                    error,
                    new DiagnosticOrigin.Source(
                        error.Span?.Document ?? fallback))));
        }

        void AddHirDiagnostics(
            CompilationStage stage,
            IEnumerable<QoraError> errors,
            HirSnapshot snapshot)
        {
            diagnostics.AddRange(
                errors.Select(error => new CompilationDiagnostic(
                    stage,
                    error,
                    new DiagnosticOrigin.Hir(snapshot.Id))));
        }

        void AddValidationDiagnostics(HirSemanticArtifact validation)
        {
            ArgumentNullException.ThrowIfNull(validation);
            if (validation.Phase != HirSemanticPhase.Validation)
                throw new ArgumentException(
                    "Only a validation artifact can publish HIR validation diagnostics.",
                    nameof(validation));
            AddHirDiagnostics(
                CompilationStage.HirValidation,
                validation.Diagnostics,
                validation.Source);
        }

        if (!syntax.Succeeded)
        {
            AddSourceDiagnostics(
                CompilationStage.Syntax,
                syntax.Diagnostics,
                syntax.Document.Ref);
            return Finish();
        }

        if (loadedSources.EntryProgram is not { } loweredProgram)
        {
            AddSourceDiagnostics(
                CompilationStage.HirPreflight,
                new[]
                {
                    new QoraError(
                        "the program contains no operation or function",
                        "QSEM040"),
                },
                sources.Entry);
            return Finish();
        }

        // A document tree is checked before it can become a published HIR snapshot. Import expansion only
        // joins top-level declarations, so it cannot create a deeper expression/block nesting than one of
        // these exact document roots already contains.
        foreach (var documentProgram in loadedSources.LoweredDocuments.Values)
        {
            if (!QoraValidator.ExceedsDepthLimit(documentProgram, out var deepDocumentCallable))
                continue;
            AddSourceDiagnostics(
                CompilationStage.HirPreflight,
                new[]
                {
                    new QoraError(
                        $"in `{deepDocumentCallable}`: an expression or nested-block structure is too deep for the compiler; simplify or split it",
                        "QSEM031"),
                },
                sources.Entry);
            return Finish();
        }

        var lowered = hirBuilder.PublishLowered(loweredProgram);
        var moduleRewrite = hirBuilder.BeginRewrite(
            lowered,
            nameof(ModuleLoader));
        var expansion = ModuleLoader.Expand(
            loadedSources,
            moduleRewrite);
        var expanded = hirBuilder.Advance(
            HirStage.ImportsExpanded,
            expansion.Rewrite);
        var expandedProgram = expansion.Program;

        var importedSyntaxFailed = sources.SyntaxByDocument.Values
            .Where(tree => tree.Document.Ref != sources.Entry)
            .Where(tree => !tree.Succeeded)
            .ToArray();
        if (importedSyntaxFailed.Length > 0
            || loadedSources.ImportDiagnostics.Count > 0)
        {
            foreach (var tree in importedSyntaxFailed)
            {
                AddSourceDiagnostics(
                    CompilationStage.Syntax,
                    tree.Diagnostics,
                    tree.Document.Ref);
            }
            AddSourceDiagnostics(
                CompilationStage.ImportExpansion,
                loadedSources.ImportDiagnostics,
                sources.Entry);
            return Finish();
        }

        var resolverRewrite = hirBuilder.BeginRewrite(
            expanded,
            nameof(Resolver));
        var resolution = Resolver.Resolve(
            expandedProgram,
            resolverRewrite);
        var resolvedProgram = resolution.Program;
        var resolutionErrors = resolution.Errors;
        if (resolutionErrors.Count > 0)
        {
            var rejected = hirBuilder.Advance(
                HirStage.Resolved,
                resolution.Rewrite);
            AddHirDiagnostics(CompilationStage.HirResolution, resolutionErrors, rejected);
            return Finish();
        }

        var resolved = hirBuilder.Advance(
            HirStage.Resolved,
            resolution.Rewrite);
        var resolvedValidation = hirBuilder.ValidateSnapshot(resolved);
        if (!resolvedValidation.IsAccepted)
        {
            AddValidationDiagnostics(resolvedValidation);
            return Finish();
        }

        // Common specialization always preserves .Count reads. Literal Count substitution is an
        // OpenQASM-only rewrite and therefore belongs exclusively to that backend.
        var specializationRewrite = hirBuilder.BeginRewrite(
            resolved,
            nameof(Monomorphizer));
        var specialization = Monomorphizer.Run(
            resolved.Program,
            specializationRewrite);
        var specializedProgram = specialization.Program;
        HirSemanticArtifact specializedValidation;

        var specialized = hirBuilder.Advance(
            HirStage.Specialized,
            specialization.Rewrite);
        if (ReferenceEquals(specialized, resolved))
        {
            specializedValidation = resolvedValidation;
        }
        else
        {
            specializedValidation = hirBuilder.ValidateSnapshot(specialized);
            if (!specializedValidation.IsAccepted)
            {
                AddValidationDiagnostics(specializedValidation);
                return Finish();
            }
        }

        var analyzed = hirBuilder.AnalyzeEffects(specializedValidation);

        if (options.OutputPlan.RequiresMir)
        {
            var loweringHir = hirBuilder.Build();
            var semanticContext = new HirSemanticContext(
                loweringHir,
                specialized,
                analyzed);
            var mirLowering = QoraMirLowering.Lower(
                semanticContext,
                revision: 0);
            mir = mirLowering.CreateSnapshot();

            // MIR analyses run on the canonical lowered graph before any internal inverse request can
            // be injected. The future cleanup scheduler will consume these revision-bound facts, mark
            // exact MIR call sites, and then hand the resulting snapshot to the materializer below.
            _ = mir.Analyses.Effects;
            var materialization = MirAdjointMaterializer.Run(mir);
            if (!materialization.Succeeded)
            {
                foreach (var error in materialization.Errors)
                {
                    var span = mir.Origins.ResolveHir(error.Origin).Span;
                    diagnostics.Add(
                        new CompilationDiagnostic(
                            CompilationStage.MirLowering,
                            new QoraError(error.Message, error.Code, span),
                            new DiagnosticOrigin.Mir(mir.Id, error.Origin)));
                }
                return Finish();
            }
            mir = materialization.Snapshot;
        }

        if (!options.OutputPlan.Requests(TargetBackend.OpenQasm))
            return Finish();

        var targetMir = mir
            ?? throw new InvalidOperationException(
                "QINTERNAL: a requested target has no canonical MIR input");
        var backend = QasmBackend.Run(targetMir);
        if (backend.Diagnostics.Count > 0)
        {
            foreach (var diagnostic in backend.Diagnostics)
            {
                diagnostics.Add(
                    new CompilationDiagnostic(
                        CompilationStage.OpenQasm,
                        diagnostic.Error,
                        new DiagnosticOrigin.Target(
                            TargetBackend.OpenQasm,
                            targetMir.Id,
                            diagnostic.Location)));
            }
            return Finish();
        }

        targetArtifacts.Add(
            new OpenQasmArtifact(backend));
        return Finish();
    }
}
