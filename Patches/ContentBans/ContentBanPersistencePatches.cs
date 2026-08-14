#nullable enable

namespace Loadout.Patches.ContentBans;

using BaseLib.Patches.Saves;
using HarmonyLib;
using Loadout.Services.ContentBans;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using System;
using System.Reflection;

[HarmonyPatch]
internal static class ContentBanExtendedSavePatch
{
    private const string EmbeddedSaveKey = "Loadout.content_bans.run_v1";
    private static bool _registered;

    internal static MethodBase TargetMethod()
    {
        Type type = AccessTools.TypeByName("BaseLib.Patches.PostModInitPatch")
                    ?? throw new TypeLoadException("BaseLib.Patches.PostModInitPatch");
        return AccessTools.Method(type, "LatePostInit")
               ?? throw new MissingMethodException(type.FullName, "LatePostInit");
    }

    [HarmonyPrefix]
    internal static void Prefix()
    {
        if (_registered)
            return;

        _registered = true;
        ExtendedSaveHandlers<IRunState, SerializableRun>.RegisterSave<RunState, string>(
            EmbeddedSaveKey,
            ContentBanService.GetSerializedRunState,
            ContentBanService.LoadSerializedRunState,
            static (payload, writer) => writer.WriteString(payload ?? string.Empty),
            static reader => reader.ReadString());
    }
}
