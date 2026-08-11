#nullable enable

namespace Loadout.UI.Screens.Controls;

using System;

public partial class NLoadoutNumberStepper : NLoadoutDecimalStepper
{
    public new event Action<int>? ValueChanged;

    public new int Minimum => decimal.ToInt32(base.Minimum);
    public new int Maximum => decimal.ToInt32(base.Maximum);
    public new int Step => decimal.ToInt32(base.Step);
    public new int Value => decimal.ToInt32(base.Value);

    public void Init(int value, int minimum = -999, int maximum = 999, int step = 1)
    {
        base.Init(value, minimum, maximum, Math.Max(1, step));
    }

    public void SetValue(int value, bool emit = true)
    {
        base.SetValue(value, emit);
    }

    protected override void EmitValueChanged(decimal value)
    {
        base.EmitValueChanged(value);
        ValueChanged?.Invoke(decimal.ToInt32(value));
    }
}
