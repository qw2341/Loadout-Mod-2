#nullable enable

namespace Loadout.Services.CardModification;

using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;

public enum PermanentCardCustomizationScope
{
    Global,
    Profile
}

public static class PermanentCardCustomizationScopeService
{
    private static PermanentCardCustomizationScope _configuredScope = PermanentCardCustomizationScope.Global;
    private static PermanentCardCustomizationScope _effectiveScope = PermanentCardCustomizationScope.Global;

    public static event Action<PermanentCardCustomizationScope>? EffectiveScopeChanged;

    public static PermanentCardCustomizationScope ConfiguredScope
    {
        get => _configuredScope;
        set
        {
            if (_configuredScope == value)
                return;

            _configuredScope = value;
            ApplyConfiguredScopeIfSafe();
        }
    }

    public static PermanentCardCustomizationScope EffectiveScope => _effectiveScope;

    public static bool ApplyConfiguredScopeIfSafe()
    {
        if (RunManager.Instance.IsInProgress || _effectiveScope == _configuredScope)
            return false;

        _effectiveScope = _configuredScope;
        EffectiveScopeChanged?.Invoke(_effectiveScope);
        return true;
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
public static class RunManagerCardCustomizationScopeCleanupPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        PermanentCardCustomizationScopeService.ApplyConfiguredScopeIfSafe();
    }
}
