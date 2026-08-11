#nullable enable

namespace Loadout.UI.Screens.Controls;

using System;
using System.Globalization;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Helpers;

public partial class NLoadoutDecimalStepper : HBoxContainer
{
    private const int ButtonWidth = 42;
    private const int EntryWidth = 104;
    private const int RowHeight = 42;

    private Button? _downButton;
    private Button? _upButton;
    private LineEdit? _entry;
    private double _value;
    private int _doublePlaces;
    private bool _isSyncing;
    private bool _suppressNextFocusCommit;

    public event Action<double>? ValueChanged;

    public double Minimum { get; private set; } = Double.MinValue;
    public double Maximum { get; private set; } = Double.MaxValue;
    public double Step { get; private set; } = 1d;
    public double Value => _value;

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

    public void Init(
        double value,
        double minimum = Double.MinValue,
        double maximum = Double.MaxValue,
        double step = 1d)
    {
        Minimum = minimum;
        Maximum = Math.Max(minimum, maximum);
        
        Step = step == 0 ? 1d : Math.Abs(step);
        
        _doublePlaces = GetDecimalPlaces(Step);
        BuildControlTree();
        SetValue(value, emit: false);
    }

    public void SetValue(double value, bool emit = true)
    {
        double next = QuantizeAndClamp(value);
        if (_value == next && _entry is not null && _entry.Text == FormatValue(next))
            return;

        _value = next;
        SyncText();

        if (emit)
            EmitValueChanged(_value);
    }

    protected virtual void EmitValueChanged(double value)
    {
        ValueChanged?.Invoke(value);
    }

    private void Increment()
    {
        SetValue(_value + GetCurrentStepAmount());
    }

    private void Decrement()
    {
        SetValue(_value - GetCurrentStepAmount());
    }

    private double GetCurrentStepAmount()
    {
        return Step * Math.Max(1, Loadout.UI.Screens.NGenericSelectScreen.GetCurrentInputMultiplier());
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

        if (TryParseEntryValue(_entry.Text, out double parsed))
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
        _entry.Text = FormatValue(_value);
        _isSyncing = false;
    }

    private bool TryParseEntryValue(string? text, out double value)
    {
        value = _value;
        string trimmed = (text ?? string.Empty).Trim();
        if (double.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out double parsed))
        {
            value = parsed;
            return true;
        }

        if (LooksLikeDecimalOverflow(trimmed, out bool isNegative))
        {
            value = isNegative ? Minimum : Maximum;
            return true;
        }

        return false;
    }

    private double QuantizeAndClamp(double value)
    {
        double clamped = Math.Clamp(value, Minimum, Maximum);
        double stepped = Math.Round(clamped / Step, 0, MidpointRounding.AwayFromZero) * Step;
        return Math.Clamp(double.Round(stepped, _doublePlaces), Minimum, Maximum);
    }

    

    private static int GetDecimalPlaces(double value)
    {
        int flags = double.GetBits(value)[3];
        return (flags >> 16) & 0xFF;
    }

    private static bool LooksLikeDecimalOverflow(string text, out bool isNegative)
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

        bool sawDigit = false;
        bool sawDecimalPoint = false;
        for (; index < text.Length; index++)
        {
            char character = text[index];
            if (char.IsDigit(character))
            {
                sawDigit = true;
                continue;
            }
            if (character == '.' && !sawDecimalPoint)
            {
                sawDecimalPoint = true;
                continue;
            }
            return false;
        }
        return sawDigit;
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
        return ResourceLoader.Exists(path) ? GD.Load<Font>(path) : null;
    }
}
