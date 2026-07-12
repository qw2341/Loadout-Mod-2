#nullable enable

namespace Loadout.Services.Morphing;

using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

public partial class NMorphedMerchantCharacter : NMerchantCharacter
{
    // Base-game merchant character bodies use roughly 0.47 scale versus about
    // 0.28 for their combat bodies. Preserve that authored ~1.7x presentation.
    private const float MerchantScaleMultiplier = 1.7f;

    private MonsterModel? _monster;
    private NCreatureVisuals? _visuals;

    public void Initialize(MonsterModel monster, NCreatureVisuals visuals)
    {
        _monster = monster;
        _visuals = visuals;
        visuals.Position = Vector2.Zero;
        AddChild(visuals);
    }

    public override void _Ready()
    {
        if (_monster is null || _visuals is null)
            return;

        _visuals.UpdatePhobiaMode(_monster);
        _visuals.SetUpSkin(_monster);
        FlipBody(_visuals.GetNodeOrNull<Node2D>("%Visuals"));
        FlipBody(_visuals.GetNodeOrNull<Node2D>("%PhobiaModeVisuals"));
        _visuals.Scale *= MerchantScaleMultiplier;
        _visuals.Position = new Vector2(0, 50);
        PlayMorphAnimation("relaxed_loop", loop: true);
    }

    public void PlayMorphAnimation(string animation, bool loop = false)
    {
        if (_visuals?.SpineBody is not { } spine)
            return;

        string[] candidates = animation.Equals("die", System.StringComparison.OrdinalIgnoreCase)
            ? ["die", "death", "dead"]
            : ["relaxed_loop", "idle_loop", "idle", "awake_loop"];

        foreach (string candidate in candidates)
        {
            if (!spine.HasAnimation(candidate))
                continue;

            spine.GetAnimationState().SetAnimation(candidate, loop || !animation.Equals("die", System.StringComparison.OrdinalIgnoreCase));
            return;
        }
    }

    private static void FlipBody(Node2D? body)
    {
        if (body is not null)
            body.Scale = new Vector2(-body.Scale.X, body.Scale.Y);
    }
}
