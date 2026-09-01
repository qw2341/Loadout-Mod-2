#nullable enable

namespace Loadout.Keywords;

using MegaCrit.Sts2.Core.Entities.Cards;

public sealed class JokeInfiniteUpgradeKeyword : LoadoutKeywordModel
{
    public static JokeInfiniteUpgradeKeyword Instance { get; } = new();

    private JokeInfiniteUpgradeKeyword()
    {
    }

    public override CardKeyword Keyword => LoadoutKeywords.JokeInfiniteUpgrade;

    public override string StorageKey => LoadoutKeywords.JokeInfiniteUpgradeKey;

    public override string TitleLocKey =>
        "LOADOUT-JOKE_INFINITE_UPGRADE.title";

    public override LoadoutKeywordEditorSection EditorSection =>
        LoadoutKeywordEditorSection.Joke;
}
