#if TOOLS
using System.Collections.Generic;
using Godot;

namespace EvaluateDev;

// The "Evaluate" editor dock: pick a `.scene`, Open it for editing (native tree),
// edit with the normal Godot tools, then Save it back to the `.scene` TOML. The
// plugin does the actual build/serialize (EvaluateScenePlugin); this is just UI.
[Tool]
public partial class EvaluateSceneDock : VBoxContainer
{
    public EvaluateScenePlugin? Plugin { get; set; }

    private ItemList _list = null!;
    private Label _status = null!;
    private EditorFileDialog? _dialog;

    public override void _Ready()
    {
        Name = "Evaluate";

        var title = new Label { Text = "Evaluate Scenes" };
        AddChild(title);

        var refresh = new Button { Text = "Refresh list" };
        refresh.Pressed += Refresh;
        AddChild(refresh);

        _list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 160) };
        _list.ItemActivated += _ => OpenSelected();
        AddChild(_list);

        var open = new Button { Text = "Open for editing" };
        open.Pressed += OpenSelected;
        AddChild(open);

        var save = new Button { Text = "Save to .scene" };
        save.Pressed += () => ReportStatus(Plugin?.SaveEditedScene() ?? "plugin unavailable");
        AddChild(save);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        AddChild(_status);

        // The project filesystem may still be scanning when the dock first loads, so an
        // immediate scan can come up empty. Repopulate once the editor finishes scanning
        // (but only while the list is empty, so a later change doesn't clear a selection
        // the user is working with — the Refresh button handles intentional updates).
        EditorInterface.Singleton.GetResourceFilesystem().FilesystemChanged += OnFilesystemChanged;
        Refresh();
    }

    public override void _ExitTree()
    {
        EditorInterface.Singleton.GetResourceFilesystem().FilesystemChanged -= OnFilesystemChanged;
    }

    private void OnFilesystemChanged()
    {
        if (_list.ItemCount == 0) Refresh();
    }

    public void ReportStatus(string message)
    {
        if (_status is not null) _status.Text = message;
        GD.Print($"[evaluate] {message}");
    }

    private void OpenSelected()
    {
        var sel = _list.GetSelectedItems();
        if (sel.Length == 0) { ReportStatus("select a .scene in the list first"); return; }
        var path = _list.GetItemMetadata(sel[0]).AsString();
        ReportStatus(Plugin?.OpenForEditing(path) ?? "plugin unavailable");
    }

    private void Refresh()
    {
        _list.Clear();
        var found = new List<string>();
        Collect("res://", found);
        found.Sort();
        foreach (var p in found)
        {
            var idx = _list.AddItem(p.Replace("res://", ""));
            _list.SetItemMetadata(idx, p);
        }
        ReportStatus($"{found.Count} .scene file(s)");
    }

    // Tool-menu entry point: pick a `.scene` via a file dialog, then open it.
    public void PromptOpen()
    {
        _dialog ??= BuildDialog();
        _dialog.PopupCentered(new Vector2I(700, 500));
    }

    private EditorFileDialog BuildDialog()
    {
        var d = new EditorFileDialog
        {
            FileMode = EditorFileDialog.FileModeEnum.OpenFile,
            Access = EditorFileDialog.AccessEnum.Resources,
            Title = "Open an Evaluate .scene for editing",
        };
        d.ClearFilters();
        d.AddFilter("*.scene", "Evaluate scenes");
        d.FileSelected += path => ReportStatus(Plugin?.OpenForEditing(path) ?? "plugin unavailable");
        AddChild(d);
        return d;
    }

    private static void Collect(string dir, List<string> outp)
    {
        using var da = DirAccess.Open(dir);
        if (da is null) return;
        da.ListDirBegin();
        for (var name = da.GetNext(); name != ""; name = da.GetNext())
        {
            if (name.StartsWith(".")) continue;   // skip .godot, .edit working copies, etc.
            var path = dir.EndsWith("/") ? dir + name : dir + "/" + name;   // keep res:// intact
            if (da.CurrentIsDir()) Collect(path, outp);
            else if (name.EndsWith(".scene")) outp.Add(path);
        }
        da.ListDirEnd();
    }
}
#endif
