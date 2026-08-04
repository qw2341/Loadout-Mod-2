#nullable enable

namespace Loadout.Companions;

public sealed class CustomLoadoutCompanion : LoadoutCompanion
{
    private string _companionId = "custom-template";
    private string _displayName = "Custom Companion";
    private string _spritePath = string.Empty;

    public override string CompanionId => _companionId;
    public override string DisplayName => _displayName;
    public override string TooltipDescription => $"Custom companion: {_displayName}";
    public override string SpritePath => _spritePath;
    public override string ConfigLocalizationKey => "CustomCompanion";
    public override bool IsCustom => true;
    public override bool UsesLocalizedConfigText => false;

    public static CustomLoadoutCompanion Create(string companionId, string displayName, string spritePath)
    {
        return new CustomLoadoutCompanion
        {
            _companionId = companionId,
            _displayName = displayName,
            _spritePath = spritePath
        };
    }
}
