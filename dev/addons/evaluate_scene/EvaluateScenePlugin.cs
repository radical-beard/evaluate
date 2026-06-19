#if TOOLS
using Godot;

namespace EvaluateDev;

// Editor-only addon: registers the importer that turns Evaluate `.scene` files
// (TOML) into PackedScenes, so the Godot editor recognizes and renders their node
// trees. This is the ONLY editor glue a game needs — the runtime parses `.scene`
// directly, so this plugin is purely for editor visualization.
[Tool]
public partial class EvaluateScenePlugin : EditorPlugin
{
    private EvaluateSceneImporter? _importer;

    public override void _EnterTree()
    {
        _importer = new EvaluateSceneImporter();
        AddImportPlugin(_importer);
    }

    public override void _ExitTree()
    {
        if (_importer is not null) RemoveImportPlugin(_importer);
        _importer = null;
    }
}
#endif
