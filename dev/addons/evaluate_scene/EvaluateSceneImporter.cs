#if TOOLS
using Godot;
using Evaluate;

namespace EvaluateDev;

// Imports a `.scene` file (TOML content) into a PackedScene for the editor. It
// reuses the exact same SceneFile parser + SceneBuilder the runtime uses, so the
// editor preview can never drift from what the game actually instantiates. Lua
// behavior (`script = "*.node.evt"`) is not run in-editor — only the visual node
// tree (types, properties, hierarchy) is built.
[Tool]
public partial class EvaluateSceneImporter : EditorImportPlugin
{
    public override string _GetImporterName() => "evaluate.scene";
    public override string _GetVisibleName() => "Evaluate Scene";
    public override string[] _GetRecognizedExtensions() => new[] { "scene" };
    public override string _GetSaveExtension() => "scn";
    public override string _GetResourceType() => "PackedScene";
    public override float _GetPriority() => 1.0f;
    public override int _GetImportOrder() => 0;

    public override int _GetPresetCount() => 0;
    public override string _GetPresetName(int presetIndex) => "Default";
    public override Godot.Collections.Array<Godot.Collections.Dictionary> _GetImportOptions(string path, int presetIndex)
        => new();
    public override bool _GetOptionVisibility(string path, StringName optionName, Godot.Collections.Dictionary options)
        => true;

    public override Error _Import(string sourceFile, string savePath, Godot.Collections.Dictionary options,
        Godot.Collections.Array<string> platformVariants, Godot.Collections.Array<string> genFiles)
    {
        using var file = FileAccess.Open(sourceFile, FileAccess.ModeFlags.Read);
        if (file is null) return FileAccess.GetOpenError();

        try
        {
            var spec = SceneFile.Parse(file.GetAsText());
            // Same builder the runtime uses; a throwaway Lua state satisfies the
            // binder (no scripts run here). An unknown node type, bad property, etc.
            // throws here — keep it out of the editor's face.
            var binder = new GodotBinder(Lua.LuaState.Create());
            var packed = SceneBuilder.BuildPackedScene(spec, binder);
            return ResourceSaver.Save(packed, $"{savePath}.{_GetSaveExtension()}");
        }
        catch (System.Exception e)
        {
            GD.PushError($"[evaluate] failed to import {sourceFile}: {e.Message}");
            return Error.Failed;
        }
    }
}
#endif
