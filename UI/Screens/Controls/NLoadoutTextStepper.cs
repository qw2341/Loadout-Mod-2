#nullable enable

namespace Loadout.UI.Screens.Controls;

using System;
using Godot;
using Loadout.PanelItems;
using Loadout.UI.Managers;
using MegaCrit.Sts2.Core.Helpers;

public partial class NLoadoutTextStepper : NLoadoutVariableControl
{
    private Button? _button;
    private LineEdit? _entry;
    private Action<string>? _changed;
    private string _value = string.Empty;
    private bool _editing;

    public void Init(string value, Action<string> changed)
    {
        _value = value;
        _changed = changed;
        if (_button is not null)
            UpdateButton();
    }

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(218f, 42f);
        _button = new Button
        {
            CustomMinimumSize = CustomMinimumSize,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = MouseFilterEnum.Stop
        };
        _button.AddThemeFontOverride("font", CommonHelpers.LoadGameFont());
        _button.AddThemeFontSizeOverride("font_size", 18);
        _button.AddThemeColorOverride("font_color", StsColors.gold);
        _button.Pressed += BeginEdit;
        AddChild(_button);
        _entry = new LineEdit
        {
            CustomMinimumSize = CustomMinimumSize,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Stop,
            Visible = false
        };
        _entry.AddThemeFontOverride("font", CommonHelpers.LoadGameFont());
        _entry.AddThemeFontSizeOverride("font_size", 18);
        _entry.AddThemeColorOverride("font_color", StsColors.cream);
        _entry.TextSubmitted += OnSubmitted;
        _entry.FocusExited += Commit;
        _entry.GuiInput += OnEntryInput;
        AddChild(_entry);
        UpdateButton();
    }

    public override void _ExitTree()
    {
        _editing = false;
        if (_button is not null)
            _button.Pressed -= BeginEdit;
        if (_entry is not null)
        {
            _entry.TextSubmitted -= OnSubmitted;
            _entry.FocusExited -= Commit;
            _entry.GuiInput -= OnEntryInput;
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (_editing && _entry is not null
            && @event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouse
            && !_entry.GetGlobalRect().HasPoint(mouse.GlobalPosition))
            Commit();
    }

    private void BeginEdit()
    {
        if (_button is null || _entry is null)
            return;
        _editing = true;
        _entry.Text = _value;
        _button.Hide();
        _entry.Show();
        _entry.GrabFocus();
        _entry.SelectAll();
    }

    private void OnSubmitted(string _) => Commit();

    private void OnEntryInput(InputEvent @event)
    {
        if (@event is InputEventKey { Keycode: Key.Escape, Pressed: true } && _entry is not null)
        {
            _entry.AcceptEvent();
            EndEdit();
        }
    }

    private void Commit()
    {
        if (!_editing || _entry is null)
            return;
        string next = _entry.Text.Trim();
        EndEdit();
        if (next == _value)
            return;
        _value = next;
        UpdateButton();
        _changed?.Invoke(next);
    }

    private void EndEdit()
    {
        _editing = false;
        _entry?.Hide();
        _button?.Show();
    }

    private void UpdateButton()
    {
        if (_button is null)
            return;
        _button.Text = string.IsNullOrEmpty(_value)
            ? LocMan.Loc("CARD_MOD_EDIT_COMMAND", "Edit Command")
            : _value;
        _button.TooltipText = _value;
    }
}
