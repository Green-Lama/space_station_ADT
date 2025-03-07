using Content.Shared.Atmos;
using Content.Shared.Damage;
using Content.Shared.Heretic;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Temperature.Components;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.Temperature.Components;
using Content.Shared.Popups;

namespace Content.Server.Heretic.Abilities;

public sealed partial class HereticAbilitySystem : EntitySystem
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly MobThresholdSystem _mobThresholdSystem = default!;

    private void SubscribeAsh()
    {
        SubscribeLocalEvent<HereticComponent, EventHereticAshenShift>(OnJaunt);
        SubscribeLocalEvent<GhoulComponent, EventHereticAshenShift>(OnJauntGhoul);

        SubscribeLocalEvent<HereticComponent, EventHereticVolcanoBlast>(OnVolcano);
        SubscribeLocalEvent<HereticComponent, EventHereticNightwatcherRebirth>(OnNWRebirth);
        SubscribeLocalEvent<HereticComponent, EventHereticFlames>(OnFlames);
        SubscribeLocalEvent<HereticComponent, EventHereticCascade>(OnCascade);

        SubscribeLocalEvent<HereticComponent, HereticAscensionAshEvent>(OnAscensionAsh);
    }

    private void OnJaunt(Entity<HereticComponent> ent, ref EventHereticAshenShift args)
    {
        var damage = args.Damage;
        if (damage != null && ent.Comp.CurrentPath == "Ash")
            damage *= float.Lerp(1f, 0.6f, ent.Comp.PathStage * 0.1f);

        // If ent will hit their crit threshold, we don't let them jaunt and give them a popup saying so.
        if (damage != null && _entMan.TryGetComponent<DamageableComponent>(ent, out var damageableComp) && _entMan.TryGetComponent<MobThresholdsComponent>(ent, out var thresholdsComp) && _mobThresholdSystem.TryGetThresholdForState(ent, MobState.Critical, out var critThreshold, thresholdsComp))
        {
            if (damageableComp.Damage.GetTotal() + damage.GetTotal() >= critThreshold)
            {
                _popup.PopupEntity(Loc.GetString("heretic-ability-fail-lowhealth", ("damage", damage.GetTotal())), ent, PopupType.LargeCaution);
                return;
            }
        }

        if (TryUseAbility(ent, args) && TryDoJaunt(ent, damage))
            args.Handled = true;
    }
    private void OnJauntGhoul(Entity<GhoulComponent> ent, ref EventHereticAshenShift args)
    {
        if (TryUseAbility(ent, args) && TryDoJaunt(ent, null))
            args.Handled = true;
    }
    private bool TryDoJaunt(EntityUid ent, DamageSpecifier? damage)
    {
        Spawn("PolymorphAshJauntAnimation", Transform(ent).Coordinates);
        var urist = _poly.PolymorphEntity(ent, "AshJaunt");
        if (urist == null)
            return false;

        if (damage != null)
            _dmg.TryChangeDamage(ent, damage, true, false);

        return true;
    }

    private void OnVolcano(Entity<HereticComponent> ent, ref EventHereticVolcanoBlast args)
    {
        if (!TryUseAbility(ent, args))
            return;

        var ignoredTargets = new List<EntityUid>();

        // all ghouls are immune to heretic shittery
        foreach (var e in EntityQuery<GhoulComponent>())
            ignoredTargets.Add(e.Owner);

        // all heretics with the same path are also immune
        foreach (var e in EntityQuery<HereticComponent>())
            if (e.CurrentPath == ent.Comp.CurrentPath)
                ignoredTargets.Add(e.Owner);

        if (!_splitball.Spawn(ent, ignoredTargets))
            return;

        if (ent.Comp is { Ascended: true, CurrentPath: "Ash" }) // will only work on ash path
            _flammable.AdjustFireStacks(ent, 20f, ignite: true);

        args.Handled = true;
    }
    private void OnNWRebirth(Entity<HereticComponent> ent, ref EventHereticNightwatcherRebirth args)
    {
        if (!TryUseAbility(ent, args))
            return;

        var power = ent.Comp.CurrentPath == "Ash" ? ent.Comp.PathStage : 4f;
        var lookup = _lookup.GetEntitiesInRange(ent, power);

        foreach (var look in lookup)
        {
            if ((TryComp<HereticComponent>(look, out var th) && th.CurrentPath == ent.Comp.CurrentPath)
            || HasComp<GhoulComponent>(look))
                continue;

            if (TryComp<FlammableComponent>(look, out var flam))
            {
                if (flam.OnFire && TryComp<DamageableComponent>(ent, out var dmgc))
                {
                    // heals everything by base + power for each burning target
                    _stam.TryTakeStamina(ent, -(10 + power));
                    var dmgdict = dmgc.Damage.DamageDict;
                    foreach (var key in dmgdict.Keys)
                        dmgdict[key] -= 10f + power;

                    var dmgspec = new DamageSpecifier() { DamageDict = dmgdict };
                    _dmg.TryChangeDamage(ent, dmgspec, true, false, dmgc);
                }

                if (flam.OnFire)
                    _flammable.AdjustFireStacks(look, power, flam, true);

                if (TryComp<MobStateComponent>(look, out var mobstat))
                    if (mobstat.CurrentState == MobState.Critical)
                        _mobstate.ChangeMobState(look, MobState.Dead, mobstat);
            }
        }

        args.Handled = true;
    }
    private void OnFlames(Entity<HereticComponent> ent, ref EventHereticFlames args)
    {
        if (!TryUseAbility(ent, args))
            return;

        EnsureComp<HereticFlamesComponent>(ent);

        if (ent.Comp.Ascended)
            _flammable.AdjustFireStacks(ent, 20f, ignite: true);

        args.Handled = true;
    }
    private void OnCascade(Entity<HereticComponent> ent, ref EventHereticCascade args)
    {
        if (!TryUseAbility(ent, args) || !Transform(ent).GridUid.HasValue)
            return;

        // yeah. it just generates a ton of plasma which just burns.
        // lame, but we don't have anything fire related atm, so, it works.
        var tilepos = _transform.GetGridOrMapTilePosition(ent, Transform(ent));
        var enumerator = _atmos.GetAdjacentTileMixtures(Transform(ent).GridUid!.Value, tilepos, false, false);
        while (enumerator.MoveNext(out var mix))
        {
            mix.AdjustMoles(Gas.Plasma, 50f);
            mix.Temperature = Atmospherics.T0C + 125f;
        }

        if (ent.Comp.Ascended)
            _flammable.AdjustFireStacks(ent, 20f, ignite: true);

        args.Handled = true;
    }


    private void OnAscensionAsh(Entity<HereticComponent> ent, ref HereticAscensionAshEvent args)
    {
        RemComp<TemperatureComponent>(ent);
        RemComp<TemperatureSpeedComponent>(ent);
        RemComp<RespiratorComponent>(ent);
        RemComp<BarotraumaComponent>(ent);

        // fire immunity
        var flam = EnsureComp<FlammableComponent>(ent);
        flam.Damage = new(); // reset damage dict
        // this does NOT protect you against lasers and whatnot. for now. when i figure out THIS STUPID FUCKING LIMB SYSTEM!!!
        // regards.
    }
}
