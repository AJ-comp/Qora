namespace Qora.Ir;

/// <summary>
/// An OpenQASM-only unsigned-width cast produced after common HIR validation. It is deliberately not
/// represented as a <see cref="QCallNode"/>: a target type constructor is not a Qora callable and must
/// never enter user-callable ID lookup, call graphs, hidden-parameter threading, or name mangling.
/// </summary>
public sealed record OpenQasmUnsignedCastNode : QNode
{
    internal OpenQasmUnsignedCastNode(
        int width,
        QNode operand)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                "an OpenQASM unsigned cast requires a positive width");

        Width = width;
        Operand = operand ?? throw new ArgumentNullException(nameof(operand));
    }

    public int Width { get; }
    public QNode Operand { get; init; }
}
