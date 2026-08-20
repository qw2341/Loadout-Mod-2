#nullable enable

namespace Loadout.Services.CardPortraits;

using System.Collections.Generic;
using HarmonyLib;
using Loadout.Services.Saving;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

internal static class CardPortraitTemporaryRunLifecycle
{
    private static readonly Dictionary<int, long> SingleplayerRunByProfile = new();
    private static readonly Dictionary<int, long> MultiplayerRunByProfile = new();

    public static void Remember(ReadSaveResult<SerializableRun> result, bool multiplayer)
    {
        if (!SaveManager.Instance.IsProfileInitialized || !result.Success || result.SaveData is null)
            return;

        Dictionary<int, long> target = multiplayer ? MultiplayerRunByProfile : SingleplayerRunByProfile;
        target[SaveManager.Instance.CurrentProfileId] = result.SaveData.StartTime;
    }

    public static CardPortraitRunDeletion PrepareDeletion(bool multiplayer)
    {
        if (!SaveManager.Instance.IsProfileInitialized)
            return default;

        int profileId = SaveManager.Instance.CurrentProfileId;
        Dictionary<int, long> source = multiplayer ? MultiplayerRunByProfile : SingleplayerRunByProfile;
        long? runStartTime = SaveUtility.GetCurrentRunStartTime();
        if (!runStartTime.HasValue && source.TryGetValue(profileId, out long rememberedStartTime))
            runStartTime = rememberedStartTime;
        return new CardPortraitRunDeletion(profileId, runStartTime);
    }

    public static void Deleted(bool multiplayer, CardPortraitRunDeletion deletion)
    {
        if (deletion.RunStartTime.HasValue)
            CardPortraitRuntime.DeleteTemporaryRun(deletion.RunStartTime.Value, deletion.ProfileId);

        Dictionary<int, long> source = multiplayer ? MultiplayerRunByProfile : SingleplayerRunByProfile;
        source.Remove(deletion.ProfileId);
    }
}

internal readonly record struct CardPortraitRunDeletion(int ProfileId, long? RunStartTime);

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.LoadRunSave))]
internal static class CardPortraitRememberSingleplayerRunPatch
{
    [HarmonyPostfix]
    public static void Postfix(ReadSaveResult<SerializableRun> __result) =>
        CardPortraitTemporaryRunLifecycle.Remember(__result, multiplayer: false);
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.LoadAndCanonicalizeMultiplayerRunSave))]
internal static class CardPortraitRememberMultiplayerRunPatch
{
    [HarmonyPostfix]
    public static void Postfix(ReadSaveResult<SerializableRun> __result) =>
        CardPortraitTemporaryRunLifecycle.Remember(__result, multiplayer: true);
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.DeleteCurrentRun))]
internal static class CardPortraitDeleteSingleplayerRunPatch
{
    [HarmonyPrefix]
    public static void Prefix(out CardPortraitRunDeletion __state) =>
        __state = CardPortraitTemporaryRunLifecycle.PrepareDeletion(multiplayer: false);

    [HarmonyPostfix]
    public static void Postfix(CardPortraitRunDeletion __state) =>
        CardPortraitTemporaryRunLifecycle.Deleted(multiplayer: false, __state);
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.DeleteCurrentMultiplayerRun))]
internal static class CardPortraitDeleteMultiplayerRunPatch
{
    [HarmonyPrefix]
    public static void Prefix(out CardPortraitRunDeletion __state) =>
        __state = CardPortraitTemporaryRunLifecycle.PrepareDeletion(multiplayer: true);

    [HarmonyPostfix]
    public static void Postfix(CardPortraitRunDeletion __state) =>
        CardPortraitTemporaryRunLifecycle.Deleted(multiplayer: true, __state);
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded))]
internal static class CardPortraitEndedRunPatch
{
    [HarmonyPostfix]
    public static void Postfix(SerializableRun __result)
    {
        if (SaveManager.Instance.IsProfileInitialized)
            CardPortraitRuntime.DeleteTemporaryRun(__result.StartTime, SaveManager.Instance.CurrentProfileId);
    }
}
