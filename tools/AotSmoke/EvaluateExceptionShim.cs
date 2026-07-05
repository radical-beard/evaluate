namespace Evaluate;

// Shim for the harness: the real EvaluateException lives in Loader.cs, which
// is Godot-coupled; Frontmatter.cs only needs the type to throw it.
public sealed class EvaluateException : System.Exception
{
    public EvaluateException(string message) : base(message) { }
}
