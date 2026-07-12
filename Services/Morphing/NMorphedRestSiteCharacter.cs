#nullable enable

namespace Loadout.Services.Morphing;

using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;

public partial class NMorphedRestSiteCharacter : Node2D
{
    private const float RestSiteScaleMultiplier = 2.7f;

    private MonsterModel? _monster;
    private NCreatureVisuals? _visuals;
    private bool _flippedSlot;

    public void Initialize(MonsterModel monster, NCreatureVisuals visuals, bool flippedSlot)
    {
        _monster = monster;
        _visuals = visuals;
        _flippedSlot = flippedSlot;
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
        _visuals.Scale *= RestSiteScaleMultiplier;
        if (_flippedSlot)
            Scale = new Vector2(-Scale.X, Scale.Y);
        PlayIdle();
    }

    public void HideFlameGlow()
    {
        if (_visuals?.SpineBody is not { } spine || !spine.HasAnimation("_tracks/light_off"))
            return;

        spine.GetAnimationState().SetAnimation("_tracks/light_off", true, 1);
    }

    private void PlayIdle()
    {
        if (_visuals?.SpineBody is not { } spine)
            return;

        foreach (string animation in new[] { "sleep_loop","relaxed_loop", "idle_loop", "idle", "awake_loop" })
        {
            if (!spine.HasAnimation(animation))
                continue;

            spine.GetAnimationState().SetAnimation(animation, true);
            return;
        }
    }

    private static void FlipBody(Node2D? body)
    {
        if (body is not null)
            body.Scale = new Vector2(-body.Scale.X, body.Scale.Y);
    }
}
