#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

public sealed record FatalPowerSnapshot(
    PowerModel Power,
    PowerType Type);

public sealed record FatalTargetSnapshot(
    int MaxHp,
    ModelId MonsterId,
    IReadOnlyList<FatalPowerSnapshot> Powers);

public sealed record FatalKeywordContext(
    int FatalCount,
    IReadOnlyList<FatalTargetSnapshot> Targets);

public abstract class LoadoutFatalKeywordModel : LoadoutKeywordModel
{
    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override LoadoutKeywordEditorSection EditorSection =>
        LoadoutKeywordEditorSection.Fatal;

    public override bool HasFatalEffect => true;

    protected static decimal GetTotalAmount(
        CardModel card,
        string name,
        int fatalCount)
    {
        if (fatalCount <= 0
            || !LoadoutKeywordRegistry.TryGetValue(
                card,
                name,
                out DynamicVar amount))
        {
            return 0m;
        }

        return (decimal)Math.Max(0, amount.IntValue) * fatalCount;
    }
}
