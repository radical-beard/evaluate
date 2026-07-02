#if TOOLS
using System.Collections.Generic;
using Godot;
using Evaluate;

namespace Evaluate.Editor;

// Editor-only addon for Evaluate `.scene` files (TOML). Two halves:
//   * Import — renders a `.scene` as a read-only PackedScene preview so the editor
//     recognizes and thumbnails it (EvaluateSceneImporter, unchanged).
//   * Edit/write-back — opens a `.scene` as an EDITABLE native scene (built with the
//     exact SceneBuilder the runtime uses), and serializes the edited tree back to
//     the `.scene` TOML via SceneWriter. This is the round-trip: edit nodes,
//     positions, properties, create nodes — Save writes your custom scene file.
//
// Editor-only: a running game is never required. If one is running, its
// FileSystemWatcher hot-reload picks up the saved `.scene` for free.
[Tool]
public partial class EvaluateScenePlugin : EditorPlugin
{
    // Stashed on the edited scene's root so Save knows which `.scene` to write back
    // to (and to carry a manifest's `start_scene` across the round-trip). `__evt_`
    // namespaced so SceneWriter never emits it.
    private const string SourceMeta = "__evt_source";
    private const string StartSceneMeta = "__evt_startscene";
    private const string DescriptionMeta = "__evt_description";
    // Transient working copies (regenerated on every Open; never the source of truth).
    private const string EditDir = "res://addons/evaluate_scene/.edit";

    private const string OpenMenu = "Evaluate: Open .scene for editing…";
    private const string SaveMenu = "Evaluate: Save edited scene to .scene";

    private EvaluateSceneImporter? _importer;
    private EvaluateSceneDock? _dock;

    public override void _EnterTree()
    {
        _importer = new EvaluateSceneImporter();
        AddImportPlugin(_importer);

        _dock = new EvaluateSceneDock { Plugin = this };
        // AddControlToDock is marked obsolete in 4.6 in favor of AddDock(EditorDock),
        // but remains fully functional and keeps the dock a plain Control (no editor-
        // dock base type to subclass). Quieted so consumer builds stay clean.
#pragma warning disable CS0618
        AddControlToDock(DockSlot.LeftUr, _dock);
#pragma warning restore CS0618

        AddToolMenuItem(OpenMenu, Callable.From(() => _dock?.PromptOpen()));
        AddToolMenuItem(SaveMenu, Callable.From(() => _dock?.ReportStatus(SaveEditedScene())));
    }

    public override void _ExitTree()
    {
        RemoveToolMenuItem(OpenMenu);
        RemoveToolMenuItem(SaveMenu);
        if (_dock is not null)
        {
#pragma warning disable CS0618
            RemoveControlFromDocks(_dock);
#pragma warning restore CS0618
            _dock.QueueFree();
            _dock = null;
        }
        if (_importer is not null) { RemoveImportPlugin(_importer); _importer = null; }
    }

    // Build an editable native tree from a `.scene` and open it as a scene tab. The
    // tree is the SAME one the runtime instantiates (SceneBuilder), so the editor can
    // never show something the game wouldn't build. Returns a human status string.
    public string OpenForEditing(string scenePath)
    {
        var text = Godot.FileAccess.GetFileAsString(scenePath);
        if (string.IsNullOrEmpty(text)) return $"could not read {scenePath}";

        SceneSpec spec;
        try { spec = SceneFile.Parse(text); }
        catch (System.Exception e) { return $"parse error: {e.Message}"; }

        var binder = new GodotBinder(Lua.LuaState.Create());
        var dir = scenePath.GetBaseDir();
        IReadOnlyList<NodeSpec> Resolve(string name)
        {
            var sub = Godot.FileAccess.GetFileAsString($"{dir}/{name}.scene");
            return string.IsNullOrEmpty(sub) ? new List<NodeSpec>() : SceneFile.Parse(sub).Nodes;
        }

        var rootName = scenePath.GetFile().GetBaseName();   // e.g. "level1"
        var root = new Node { Name = rootName };
        try
        {
            foreach (var n in spec.Nodes)
                root.AddChild(SceneBuilder.BuildNode(n, binder, null, Resolve));
        }
        catch (System.Exception e) { root.Free(); return $"build error: {e.Message}"; }

        root.SetMeta(SourceMeta, scenePath);
        if (spec.StartScene is { } ss) root.SetMeta(StartSceneMeta, ss);
        // Carry the scene's `description` on the root (editable in the inspector's
        // Metadata) so Save writes it back — scene docs survive the round-trip as data.
        if (spec.Description is { } desc) root.SetMeta(DescriptionMeta, desc);
        SetOwner(root, root);   // PackedScene.Pack only captures descendants owned by the root

        DirAccess.MakeDirRecursiveAbsolute(EditDir);
        var tempPath = $"{EditDir}/{rootName}.tscn";
        var packed = new PackedScene();
        packed.Pack(root);
        var err = ResourceSaver.Save(packed, tempPath);
        root.Free();
        if (err != Error.Ok) return $"could not write working copy: {err}";

        EditorInterface.Singleton.OpenSceneFromPath(tempPath);
        return $"editing {scenePath} — edit, then Save to .scene";
    }

    // Serialize the currently edited scene back to its source `.scene` (TOML).
    public string SaveEditedScene()
    {
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root is null) return "no scene is open";
        if (!root.HasMeta(SourceMeta))
            return "this scene was not opened via Evaluate — open a .scene from the dock first";

        var scenePath = root.GetMeta(SourceMeta).AsString();
        var startScene = root.HasMeta(StartSceneMeta) ? root.GetMeta(StartSceneMeta).AsString() : null;
        var description = root.HasMeta(DescriptionMeta) ? root.GetMeta(DescriptionMeta).AsString() : null;
        var binder = new GodotBinder(Lua.LuaState.Create());

        // Re-parse the source for its scene-LEVEL header (controls/scenario/[player])
        // so a node-tree edit in the editor never drops fields the editor can't show.
        SceneSpec? header = null;
        try { header = SceneFile.Parse(Godot.FileAccess.GetFileAsString(scenePath)); }
        catch (System.Exception) { /* unreadable/renamed source: emit without a header */ }

        string toml;
        try { toml = SceneWriter.WriteContainer(root, binder, startScene, description, header); }
        catch (System.Exception e) { return $"serialize error: {e.Message}"; }

        using var f = Godot.FileAccess.Open(scenePath, Godot.FileAccess.ModeFlags.Write);
        if (f is null) return $"could not write {scenePath}: {Godot.FileAccess.GetOpenError()}";
        f.StoreString(toml);
        f.Close();

        EditorInterface.Singleton.GetResourceFilesystem().Scan();   // refresh the read-only preview
        return $"saved {scenePath}";
    }

    private static void SetOwner(Node node, Node owner)
    {
        foreach (var child in node.GetChildren())
        {
            child.Owner = owner;
            SetOwner(child, owner);
        }
    }
}
#endif
