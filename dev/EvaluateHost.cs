using Godot;

namespace EvaluateDev;

// Host shim. Godot resolves scene scripts only from .cs under res://, and
// resolving a script whose base type lives in a *referenced* assembly is
// fragile — so instead of subclassing the runtime, we instantiate it in code.
// This is the ONLY C# a game needs (a game's copy is identical).
public partial class EvaluateHost : Node
{
    public override void _Ready() => AddChild(new Evaluate.EvaluateRuntime());
}
