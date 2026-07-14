using System;
using System.Collections.Generic;
using System.Linq;
using Qora.Ir;

namespace Qora.Tests;

/// <summary>
/// The inversion kernel (<see cref="Inverter.InvertBody"/>) is the exact tool the auto-uncompute injector
/// (rung ④) calls on an ancilla's write list to synthesize its cleanup, so these pin the two properties it
/// depends on DIRECTLY on the Inverter (ConjugationTests only reach it through ConjugationLowering):
///
///   1. GATE CATALOG — every gate inverts by toggling the <c>Adjoint</c> functor, uniformly. The design docs
///      say "self-inverse gates come back as themselves (X→X)"; that is the PHYSICAL reading. The IR/QASM
///      representation is <c>Adjoint X</c> → <c>inv @ x</c>, which for a self-inverse X equals X and is the
///      very form hand-written within/apply cleanup already emits. Y is not self-inverse, so its
///      <c>Adjoint Y</c> (→ <c>inv @ y</c>) is a genuinely different gate, Y† — but the toggle is identical.
///   2. REVERSE ORDER — a list inverts socks-and-shoes: (U₂U₁)† = U₁†U₂†. The last write is undone first.
///
/// The toggle is an involution (<c>Adjoint X</c> ↔ <c>X</c>), so a cleanup composed with its own inverse
/// cancels; and an irreversible statement refuses with a reason rather than a silent empty inverse.
/// </summary>
public class InverterTests
{
    private static QQubitArg Q(string reg, int i) => new(reg, i.ToString());
    private static QGate Gate(string name, params QArg[] args) => new(new List<string>(), name, args.ToList());
    // gate-only inversion needs no user-op table
    private static Inverter Kernel() => new(Array.Empty<QOperation>());

    private static IReadOnlyList<QStmt> Invert(params QStmt[] body)
    {
        var (inverse, reason) = Kernel().InvertBody(body);
        Assert.True(inverse is not null, $"expected an inverse, got refusal: {reason}");
        return inverse!;
    }

    // --- 1. gate catalog: X / CNOT / CCX / Y all toggle to `Adjoint <name>`, arguments carried through ---

    [Theory]
    [InlineData("X")]      // self-inverse:      inv @ x  == x
    [InlineData("Y")]      // NOT self-inverse:  inv @ y  == y†  (a different gate) — same toggle
    public void SingleQubitGateTogglesAdjoint(string name)
    {
        var g = Assert.IsType<QGate>(Assert.Single(Invert(Gate(name, Q("a", 0)))));
        Assert.Equal(name, g.Name);
        Assert.Equal("Adjoint", Assert.Single(g.Functors));
        Assert.Equal("a", ((QQubitArg)g.Args[0]).Reg);        // args untouched
    }

    [Fact]
    public void CnotTogglesAdjointKeepingBothQubits()
    {
        var g = Assert.IsType<QGate>(Assert.Single(Invert(Gate("CNOT", Q("a", 0), Q("b", 0)))));
        Assert.Equal("CNOT", g.Name);
        Assert.Equal("Adjoint", Assert.Single(g.Functors));
        Assert.Equal(2, g.Args.Count);
    }

    [Fact]
    public void CcxTogglesAdjointKeepingAllThreeQubits()
    {
        var g = Assert.IsType<QGate>(Assert.Single(Invert(Gate("CCX", Q("a", 0), Q("b", 0), Q("c", 0)))));
        Assert.Equal("CCX", g.Name);
        Assert.Equal("Adjoint", Assert.Single(g.Functors));
        Assert.Equal(3, g.Args.Count);
    }

    // --- 2. reverse order: the injector's core — the last write is undone first ---

    [Fact]
    public void ListInvertsInReverseOrder()
    {
        // forward: X(a) then CNOT(a,b).  inverse: Adjoint CNOT(a,b) then Adjoint X(a).
        var inv = Invert(Gate("X", Q("a", 0)), Gate("CNOT", Q("a", 0), Q("b", 0)));
        Assert.Equal(2, inv.Count);

        var first = Assert.IsType<QGate>(inv[0]);
        Assert.Equal("CNOT", first.Name);                     // last forward gate, undone first
        Assert.Equal("Adjoint", Assert.Single(first.Functors));

        var second = Assert.IsType<QGate>(inv[1]);
        Assert.Equal("X", second.Name);
        Assert.Equal("Adjoint", Assert.Single(second.Functors));
    }

    // --- 3. involution: Adjoint X inverts back to a bare X (cleanup ∘ its own inverse = identity) ---

    [Fact]
    public void AdjointTogglesBackToBareGate()
    {
        var adjX = new QGate(new List<string> { "Adjoint" }, "X", new List<QArg> { Q("a", 0) });
        var g = Assert.IsType<QGate>(Assert.Single(Invert(adjX)));
        Assert.Equal("X", g.Name);
        Assert.Empty(g.Functors);
    }

    // --- 4. irreversibles refuse with a reason, never a silent empty inverse ---

    [Fact]
    public void LocalAllocationRefusesWithReason()
    {
        var (inverse, reason) = Kernel().InvertBody(new QStmt[] { new QUse("t", 1) });
        Assert.Null(inverse);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}
