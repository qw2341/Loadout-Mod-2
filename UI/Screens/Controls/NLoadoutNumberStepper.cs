#nullable enable

namespace Loadout.UI.Screens.Controls;

using System;
using System.Globalization;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Helpers;

public partial class NLoadoutNumberStepper : HBoxContainer
{
    private const int ButtonWidth = 42;
    private const int EntryWidth = 82;
    private const int RowHeight = 42;

    private Button? _downButton;
    private Button? _upButton;
    private LineEdit? _entry;
    private int _value;
    private bool _isSyncing;
    private bool _suppressNextFocusCommit;

    public event Action<int>? ValueChanged;

    public int Minimum { get; private set; } = int.MinValue;
    public int Maximum { get; private set; } = int.MaxValue;
    public int Step { get; private set; } = 1;
    public int Value => _value;

    public override void _Ready()
    {
        BuildControlTree();
        SyncText();
    }

    public override void _ExitTree()
    {
        if (_downButton is not null)
            _downButton.Pressed -= Decrement;

        if (_upButton is not null)
            _upButton.Pressed -= Increment;

        if (_entry is not null)
        {
            _entry.TextSubmitted -= OnTextSubmitted;
            _entry.FocusExited -= OnFocusExited;
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (_entry is null || !_entry.HasFocus())
            return;

        if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouseButton)
            return;

        if (_entry.GetGlobalRect().HasPoint(mouseButton.GlobalPosition))
            return;

        CommitAndReleaseFocus();
    }

    public void Init(int value, int minimum = -999, int maximum = 999, int step = 1)
    {
        Minimum = minimum;
        Maximum = Math.Max(minimum, maximum);
        Step = Math.Max(1, step);
        SetValue(value, emit: false);
    }

    public void SetValue(int value, bool emit = true)
    {
        SetValueFromLong(value, emit);
    }

    private void SetValueFromLong(long value, bool emit = true)
    {
        int next = (int)Math.Clamp(value, (long)Minimum, (long)Maximum);
        if (_value == next && _entry is not null && _entry.Text == next.ToString())
            return;

        _value = next;
        SyncText();

        if (emit)
            ValueChanged?.Invoke(_value);
    }

    private void Increment()
    {
        SetValueFromLong((long)_value + GetCurrentStepAmount());
    }

    private void Decrement()
    {
        SetValueFromLong((long)_value - GetCurrentStepAmount());
    }

    private long GetCurrentStepAmount()
    {
        return (long)Step * Math.Max(1, Loadout.UI.Screens.NGenericSelectScreen.GetCurrentInputMultiplier());
    }

    private void OnTextSubmitted(string _)
    {
        CommitAndReleaseFocus();
    }

    private void OnFocusExited()
    {
        if (_suppressNextFocusCommit)
        {
            _suppressNextFocusCommit = false;
            return;
        }

        CommitText();
    }

    private void CommitAndReleaseFocus()
    {
        CommitText();
        _suppressNextFocusCommit = true;
        _entry?.ReleaseFocus();
    }

    private void CommitText()
    {
        if (_isSyncing || _entry is null)
            return;

        if (TryParseEntryValue(_entry.Text, out int parsed))
        {
            SetValue(parsed);
            return;
        }

        SyncText();
    }

    private void SyncText()
    {
        if (_entry is null)
            return;

        _isSyncing = true;
        _entry.Text = _value.ToString();
        _isSyncing = false;
    }

    private bool TryParseEntryValue(string? text, out int value)
    {
        value = _value;
        string trimmed = (text ?? string.Empty).Trim();
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            value = (int)Math.Clamp(parsed, (long)Minimum, (long)Maximum);
            return true;
        }

        if (LooksLikeIntegerOverflow(trimmed, out bool isNegative))
        {
            value = isNegative ? Minimum : Maximum;
            return true;
        }

        return false;
    }

    private static bool LooksLikeIntegerOverflow(string text, out bool isNegative)
    {
        isNegative = false;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        int index = 0;
        if (text[0] is '+' or '-')
        {
            isNegative = text[0] == '-';
            index = 1;
        }

        return index < text.Length && text[index..].All(char.IsDigit);
    }

    private void BuildControlTree()
    {
        if (_entry is not null)
            return;

        CustomMinimumSize = new Vector2(ButtonWidth * 2f + EntryWidth + 10f, RowHeight);
        SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        AddThemeConstantOverride("separation", 5);

        _downButton = CreateButton("-");
        _downButton.Pressed += Decrement;
        AddChild(_downButton);

        _entry = new LineEdit
        {
            CustomMinimumSize = new Vector2(EntryWidth, RowHeight),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            Alignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Stop
        };
        _entry.AddThemeFontOverride("font", LoadFont("res://themes/kreon_bold_glyph_space_one.tres"));
        _entry.AddThemeFontSizeOverride("font_size", 22);
        _entry.AddThemeColorOverride("font_color", StsColors.cream);
        _entry.AddThemeColorOverride("font_focus_color", StsColors.gold);
        _entry.TextSubmitted += OnTextSubmitted;
        _entry.FocusExited += OnFocusExited;
        AddChild(_entry);

        _upButton = CreateButton("+");
        _upButton.Pressed += Increment;
        AddChild(_upButton);
    }

    private static Button CreateButton(string text)
    {
        Button button = new()
        {
            Text = text,
            CustomMinimumSize = new Vector2(ButtonWidth, RowHeight),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.All
        };
        button.AddThemeFontOverride("font", LoadFont("res://themes/kreon_bold_glyph_space_one.tres"));
        button.AddThemeFontSizeOverride("font_size", 24);
        button.AddThemeColorOverride("font_color", StsColors.gold);
        return button;
    }

    private static Font? LoadFont(string path)
    {
        string localPath = path.Replace("res://themes/", "res://Loadout/themes/default/");
        if (ResourceLoader.Exists(localPath))
            return GD.Load<Font>(localPath);

        return ResourceLoader.Exists(path) ? GD.Load<Font>(path) : null;
    }
}
