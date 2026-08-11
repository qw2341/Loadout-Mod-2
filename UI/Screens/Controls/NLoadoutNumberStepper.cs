#nullable enable

namespace Loadout.UI.Screens.Controls;

using System;

public partial class NLoadoutNumberStepper : NLoadoutDecimalStepper
{
    public new event Action<int>? ValueChanged;

    public new int Minimum => (int)base.Minimum;
    public new int Maximum => (int)base.Maximum;
    public new int Step => (int)base.Step;
    public new int Value => (int)base.Value;

    public void Init(int value, int minimum = int.MinValue, int maximum = int.MaxValue, int step = 1)
    {
        base.Init(value, minimum, maximum, Math.Max(1, step));
    }

    public void SetValue(int value, bool emit = true)
    {
        base.SetValue(value, emit);
    }

    protected override void EmitValueChanged(double value)
    {
        base.EmitValueChanged(value);
        ValueChanged?.Invoke((int)value);
    }
}
