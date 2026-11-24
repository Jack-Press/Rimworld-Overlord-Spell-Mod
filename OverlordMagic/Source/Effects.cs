using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace OverlordMagic
{
    /// <summary>One thing a spell does. A spell holds a list of these.</summary>
    public abstract class SpellEffect
    {
        public abstract EffectKind Kind { get; }
        public abstract void Apply(SpellContext ctx);
    }

    /// <summary>
    /// Base for effects that hit pawns. radius &lt;= 0 means "only the targeted pawn"
    /// (filters ignored); radius &gt; 0 hits every pawn in range that passes the filters.
    /// </summary>
    public abstract class SpellPawnEffect : SpellEffect
    {
        public float radius = 0f;
        public bool affectCaster = false;
        public bool affectAllies = false;
        public bool affectEnemies = true;
        public bool affectNeutral = true;

        protected IEnumerable<Pawn> TargetPawns(SpellContext ctx)
        {
            var result = new List<Pawn>();
            if (radius <= 0f)
            {
                Pawn single = ctx.TargetThing as Pawn;
                if (single != null && !single.Dead) result.Add(single);
                return result;
            }

            float r = radius * ctx.mods.area;
            IntVec3 center = ctx.Cell;
            foreach (Pawn p in ctx.map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (p.Dead) continue;
                if ((p.Position - center).LengthHorizontal > r) continue;
                if (PassesFilter(ctx.caster, p)) result.Add(p);
            }
            return result;
        }

        private bool PassesFilter(Pawn caster, Pawn p)
        {
            if (p == caster) return affectCaster;
            if (caster.Faction != null && p.Faction == caster.Faction) return affectAllies;
            if (p.HostileTo(caster)) return affectEnemies;
            return affectNeutral;
        }
    }

    // ---------------------------------------------------------------- DAMAGE
    public class Effect_Damage : SpellPawnEffect
    {
        public override EffectKind Kind { get { return EffectKind.Damage; } }

        public DamageDef damageDef;
        public float amount = 10f;               // damage at modifier 1.0 for the spell's tier
        public float armorPenetration = -1f;     // -1 = the damage def's default
        public bool instantKill = false;         // Cry of the Banshee, Death, ...
        public BodyPartDef targetPart;           // optional: aim at a part (limb removal etc.)
        public bool affectThings = false;        // also damage non-pawn things in radius
        public float flashScale = 0f;

        public override void Apply(SpellContext ctx)
        {
            float amt = amount * ctx.mods.power;

            foreach (Pawn p in TargetPawns(ctx))
            {
                BodyPartRecord part = null;
                if (targetPart != null)
                    part = p.health.hediffSet.GetNotMissingParts().FirstOrDefault(x => x.def == targetPart);

                DamageInfo dinfo = new DamageInfo(damageDef, amt, armorPenetration, -1f, ctx.caster, part);
                if (instantKill) p.Kill(dinfo);
                else p.TakeDamage(dinfo);
            }

            if (affectThings && radius > 0f)
            {
                float r = radius * ctx.mods.area;
                foreach (Thing t in GenRadial.RadialDistinctThingsAround(ctx.Cell, ctx.map, r, true).ToList())
                {
                    if (t is Pawn || t.Destroyed || !t.def.useHitPoints) continue;
                    t.TakeDamage(new DamageInfo(damageDef, amt, armorPenetration, -1f, ctx.caster));
                }
            }

            if (flashScale > 0f) SpellUtility.Flash(ctx.map, ctx.Cell, flashScale * ctx.mods.area);
        }
    }

    // ------------------------------------------------------------------ HEAL
    public class Effect_Heal : SpellPawnEffect
    {
        public override EffectKind Kind { get { return EffectKind.Heal; } }

        public float amount = 20f;            // total injury severity healed at modifier 1.0 (the "limit")
        public bool healPermanent = false;    // scars and other permanent injuries
        public bool regrowParts = false;      // restore missing limbs / organs (higher-tier healing)
        public bool resurrect = false;        // target must be a corpse

        // Resurrection strength (lower-tier spells bring people back in worse shape).
        public bool requireFreshCorpse = true;    // corpses that have started to rot cannot be raised
        public float reviveHoursToDeath = 0f;     // 0 = returns healthy; otherwise about how long they last untreated
        public float reviveBloodLoss = 0.6f;      // blood loss they return with (0..1); applied to every revived pawn
        public int reviveUnconsciousTicks = 0;    // how long they stay unconscious after returning (0 = not at all)

        // BloodLoss shrinks by about a third per day on its own, which slightly slows a bleed-out.
        private const float NaturalBloodRecoveryPerDay = 0.33f;

        public override void Apply(SpellContext ctx)
        {
            if (resurrect)
            {
                Corpse corpse = ctx.TargetThing as Corpse;
                if (corpse != null && corpse.InnerPawn != null)
                    Resurrect(corpse.InnerPawn);
                return;
            }

            float amt = amount * ctx.mods.power;
            foreach (Pawn p in TargetPawns(ctx))
                HealPawn(p, amt);
        }

        // How much of the pawn's damage this resurrection can undo. Every level restores life-critical parts;
        // higher levels also restore less important ones.
        //   0 = only parts that would be fatal to lose (head, neck, torso, heart, lungs, ...)
        //   1 = also large limbs     2 = everything
        public int reviveRestoreLevel = 0;

        private const float CutDamage = 8f;

        private static bool ContainsVital(BodyPartRecord part)
        {
            if (part.def == BodyPartDefOf.Head || part.def == BodyPartDefOf.Neck || part.def == BodyPartDefOf.Torso)
                return true;
            if (part.def.tags != null)
            {
                foreach (BodyPartTagDef tag in part.def.tags)
                {
                    if (tag == BodyPartTagDefOf.ConsciousnessSource || tag == BodyPartTagDefOf.BloodPumpingSource
                        || tag == BodyPartTagDefOf.BreathingSource || tag == BodyPartTagDefOf.BloodFiltrationSource)
                        return true;
                }
            }
            foreach (BodyPartRecord child in part.parts)
                if (ContainsVital(child)) return true;
            return false;
        }

        private bool ShouldRestore(BodyPartRecord part)
        {
            if (ContainsVital(part)) return true;
            if (reviveRestoreLevel >= 2) return true;
            return reviveRestoreLevel >= 1 && part.def.hitPoints >= 25;
        }

        // A wound that was bleeding when the pawn died. Dead pawns report no bleeding, so this is worked out
        // from the wound's definition.
        private static bool WasBleeding(Hediff_Injury injury)
        {
            return injury.Part != null && !injury.IsPermanent() && !injury.IsTended() && injury.Severity > 0f
                && injury.def.injuryProps != null && injury.def.injuryProps.bleedRate > 0f
                && injury.Part.def.bleedRate > 0f;
        }

        /// <summary>
        /// The only conditions a resurrection may remove: those that decide whether the pawn lives or for how
        /// long (blood loss, bleeding wounds, anything that can kill). Everything else (a missing foot, scars,
        /// bruises, chronic conditions, implants) is left exactly as it was.
        /// </summary>
        private static bool MayBeFixed(Hediff h)
        {
            if (h.def == HediffDefOf.BloodLoss) return true;
            if (h.def.lethalSeverity > 0f) return true;
            Hediff_Injury injury = h as Hediff_Injury;
            return injury != null && WasBleeding(injury);
        }

        private static bool IsUnderRestoredPart(BodyPartRecord part, List<BodyPartRecord> restored)
        {
            for (BodyPartRecord p = part; p != null; p = p.parent)
                if (restored.Contains(p)) return true;
            return false;
        }

        /// <summary>
        /// Brings the pawn back, limited by the spell's level:
        ///  1. missing parts the spell is strong enough to restore are restored (a lost head, but not a lost foot);
        ///  2. the pawn is resurrected;
        ///  3. anything vanilla resurrection healed that this spell may not fix is put back;
        ///  4. severe blood loss, plus bleeding wounds on the restored parts and on parts that were already
        ///     bleeding, tuned from the game's bleed rate so that, untreated, they bleed out in about
        ///     reviveHoursToDeath hours;
        ///  5. they are unconscious for a while.
        /// </summary>
        private void Resurrect(Pawn pawn)
        {
            HediffSet set = pawn.health.hediffSet;
            List<Hediff> before = set.hediffs.ToList();   // the same Hediff objects, to put back later

            List<BodyPartRecord> bleedingParts = before.OfType<Hediff_Injury>()
                .Where(WasBleeding).Select(i => i.Part).Distinct().ToList();
            List<Hediff_MissingPart> missingRoots = set.GetMissingPartsCommonAncestors().ToList();

            // Restore before resurrecting, otherwise a pawn missing their head would simply die again.
            List<BodyPartRecord> restored = new List<BodyPartRecord>();
            foreach (Hediff_MissingPart m in missingRoots)
            {
                if (m.Part != null && ShouldRestore(m.Part)) restored.Add(m.Part);
            }
            foreach (BodyPartRecord part in restored)
                pawn.health.RestorePart(part, null, false);

            ResurrectionUtility.TryResurrect(pawn);
            if (pawn.Dead || !pawn.Spawned) return;

            RestoreKept(pawn, before, restored);

            if (reviveHoursToDeath > 0f) MakeFragile(pawn, restored, bleedingParts);
            if (reviveUnconsciousTicks > 0) ApplyStupor(pawn);

            bool bleeding = pawn.health.hediffSet.BleedRateTotal > 0.01f;
            Messages.Message(pawn.LabelShort + (bleeding
                ? " has been raised, but is barely alive and bleeding badly. Treat the wounds quickly."
                : " has been raised, but is barely alive."), pawn, MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>Put back everything the vanilla resurrection removed that this spell is not allowed to fix.</summary>
        private void RestoreKept(Pawn pawn, List<Hediff> before, List<BodyPartRecord> restored)
        {
            HediffSet set = pawn.health.hediffSet;
            foreach (Hediff h in before)
            {
                if (set.hediffs.Contains(h) || MayBeFixed(h)) continue;
                if (IsUnderRestoredPart(h.Part, restored)) continue;   // went away with the restored part
                try
                {
                    pawn.health.AddHediff(h, h.Part);
                }
                catch (Exception e)
                {
                    Log.Warning("[OverlordMagic] Could not put back " + h.def.defName + " on " + pawn.LabelShort + ": " + e.Message);
                }
            }
        }

        private static void AddSite(List<BodyPartRecord> sites, HediffSet set, BodyPartRecord part, bool includeChildren)
        {
            if (part == null || set.PartIsMissing(part)) return;
            if (part.depth == BodyPartDepth.Outside && part.def.hitPoints >= 25 && part.def.bleedRate > 0f && !sites.Contains(part))
                sites.Add(part);
            if (!includeChildren) return;
            foreach (BodyPartRecord child in part.parts)
                AddSite(sites, set, child, true);
        }

        private void MakeFragile(Pawn pawn, List<BodyPartRecord> restoredParts, List<BodyPartRecord> bleedingParts)
        {
            HediffSet set = pawn.health.hediffSet;

            // Replace blood loss and bleeding wounds. Everything else stays as it was.
            foreach (Hediff_Injury injury in set.hediffs.OfType<Hediff_Injury>().Where(i => !i.IsPermanent() && (i.Bleeding || WasBleeding(i))).ToList())
                pawn.health.RemoveHediff(injury);
            foreach (Hediff oldLoss in set.hediffs.Where(h => h.def == HediffDefOf.BloodLoss).ToList())
                pawn.health.RemoveHediff(oldLoss);

            // Severe blood loss for every revived pawn, whatever killed them.
            Hediff loss = HediffMaker.MakeHediff(HediffDefOf.BloodLoss, pawn);
            loss.Severity = Mathf.Clamp(reviveBloodLoss, 0.01f, 0.9f);
            pawn.health.AddHediff(loss);

            // BleedRateTotal is the fraction of the pawn's blood lost per day. Dying at 100% blood loss in
            // reviveHoursToDeath hours needs (1 - startingLoss) / days, plus a little for natural recovery.
            float days = reviveHoursToDeath / 24f;
            float targetRate = (1f - loss.Severity) / days + NaturalBloodRecoveryPerDay;

            // Bleeding only on parts that were brought back (neck, head...) or that were already bleeding.
            // Each site is capped so that its cuts cannot destroy it. If there are no such parts, the pawn
            // has the blood loss but no bleeding wound.
            List<BodyPartRecord> sites = new List<BodyPartRecord>();
            foreach (BodyPartRecord r in restoredParts) AddSite(sites, set, r, true);
            foreach (BodyPartRecord r in bleedingParts) AddSite(sites, set, r, false);

            int cuts = 0;
            foreach (BodyPartRecord site in sites)
            {
                int cap = Mathf.FloorToInt(site.def.hitPoints * 0.8f / CutDamage);
                for (int i = 0; i < cap && set.BleedRateTotal < targetRate && !pawn.Dead; i++)
                {
                    pawn.TakeDamage(new DamageInfo(DamageDefOf.Cut, CutDamage, 999f, -1f, null, site));
                    cuts++;
                }
                if (set.BleedRateTotal >= targetRate || pawn.Dead) break;
            }

            if (Prefs.DevMode)
                Log.Message("[OverlordMagic] " + pawn.LabelShort + " raised: restored " + restoredParts.Count + " part(s), "
                    + sites.Count + " bleeding site(s), blood loss " + loss.Severity.ToStringPercent() + ", bleeding "
                    + set.BleedRateTotal.ToStringPercent() + "/day (target " + targetRate.ToStringPercent() + "/day, " + cuts + " cuts).");
        }

        private void ApplyStupor(Pawn pawn)
        {
            HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail("OM_ReviveStupor");
            if (def == null) return;
            Hediff h = HediffMaker.MakeHediff(def, pawn);
            HediffWithComps withComps = h as HediffWithComps;
            HediffComp_Disappears timer = withComps != null ? withComps.TryGetComp<HediffComp_Disappears>() : null;
            if (timer != null) timer.ticksToDisappear = reviveUnconsciousTicks;
            pawn.health.AddHediff(h);
        }

        private void HealPawn(Pawn p, float amt)
        {
            float remaining = amt;

            // Worst wounds first, until the healing budget runs out.
            List<Hediff_Injury> injuries = p.health.hediffSet.hediffs
                .OfType<Hediff_Injury>()
                .Where(i => healPermanent || !i.IsPermanent())
                .OrderByDescending(i => i.Severity)
                .ToList();

            foreach (Hediff_Injury injury in injuries)
            {
                if (remaining <= 0f) break;
                float h = Mathf.Min(injury.Severity, remaining);
                injury.Heal(h);
                remaining -= h;
            }

            if (regrowParts)
            {
                List<Hediff_MissingPart> missing = p.health.hediffSet.hediffs
                    .OfType<Hediff_MissingPart>()
                    .ToList();
                foreach (Hediff_MissingPart m in missing)
                {
                    // RestorePart also restores child parts, so re-check each time.
                    if (m.Part != null && p.health.hediffSet.PartIsMissing(m.Part))
                        p.health.RestorePart(m.Part);
                }
            }
        }
    }

    // ---------------------------------------------------------------- SUMMON
    public enum SummonType
    {
        Being,   // spawn a pawn
        Object,  // spawn a thing
        Self     // the caster "summons" themself at the target cell (teleport)
    }

    public class Effect_Summon : SpellEffect
    {
        public override EffectKind Kind { get { return EffectKind.Summon; } }

        public SummonType summonType = SummonType.Being;
        public PawnKindDef pawnKind;
        public ThingDef thingDef;
        public ThingDef stuffDef;
        public int count = 1;
        public float spawnRadius = 3f;     // only used for 2nd+ summons of a group
        public int lifetimeTicks = 0;      // 0 = permanent

        public override void Apply(SpellContext ctx)
        {
            switch (summonType)
            {
                case SummonType.Being: SummonBeings(ctx); break;
                case SummonType.Object: SummonObjects(ctx); break;
                case SummonType.Self: Teleport(ctx); break;
            }
        }

        private int Lifetime(SpellContext ctx)
        {
            return Mathf.RoundToInt(lifetimeTicks * ctx.mods.duration);
        }

        /// <summary>The first summon appears exactly on the target cell; extras fill nearby free cells.</summary>
        private IntVec3 NextCell(SpellContext ctx, int index)
        {
            if (index == 0)
                return SpellUtility.IsCellFree(ctx.map, ctx.Cell) ? ctx.Cell : IntVec3.Invalid;
            return SpellUtility.FindFreeCellNear(ctx.map, ctx.Cell, spawnRadius);
        }

        private void SummonBeings(SpellContext ctx)
        {
            if (pawnKind == null) return;
            for (int i = 0; i < count; i++)
            {
                IntVec3 cell = NextCell(ctx, i);
                if (!cell.IsValid) continue;
                Pawn p = PawnGenerator.GeneratePawn(pawnKind, ctx.caster.Faction);
                GenSpawn.Spawn(p, cell, ctx.map);
                SpellUtility.Flash(ctx.map, cell, 1f);
                if (lifetimeTicks > 0) SpellManager.Register(p, Lifetime(ctx));
            }
        }

        private void SummonObjects(SpellContext ctx)
        {
            if (thingDef == null) return;
            for (int i = 0; i < count; i++)
            {
                IntVec3 cell = NextCell(ctx, i);
                if (!cell.IsValid) continue;
                ThingDef stuff = thingDef.MadeFromStuff ? (stuffDef ?? GenStuff.DefaultStuffFor(thingDef)) : null;
                Thing t = ThingMaker.MakeThing(thingDef, stuff);
                GenSpawn.Spawn(t, cell, ctx.map);
                if (lifetimeTicks > 0) SpellManager.Register(t, Lifetime(ctx));
            }
        }

        private void Teleport(SpellContext ctx)
        {
            Pawn caster = ctx.caster;
            IntVec3 cell = ctx.Cell;
            if (!SpellUtility.IsCellFree(ctx.map, cell))
            {
                Messages.Message("There is no room to teleport there.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            SpellUtility.Flash(ctx.map, caster.Position, 1.5f);
            caster.Position = cell;
            caster.Notify_Teleported(true, true);
            if (caster.pather != null) caster.pather.StopDead();
            SpellUtility.Flash(ctx.map, cell, 1.5f);
        }
    }

    // -------------------------------------------------------------- CREATION
    public enum CreationShape { Single, Radial, Ring, Line }

    /// <summary>Permanent changes to the world: things (walls, fire...) and/or terrain.</summary>
    public class Effect_Creation : SpellEffect
    {
        public override EffectKind Kind { get { return EffectKind.Creation; } }

        public ThingDef thingDef;
        public ThingDef stuffDef;
        public TerrainDef terrainDef;
        public CreationShape shape = CreationShape.Single;
        public float radius = 0f;          // Radial / Ring
        public int halfLength = 2;         // Line, when only clicked: cells each side of the target (2 => 5 wide)
        public int maxLength = 7;          // Line, when dragged: longest wall allowed
        public float chance = 1f;          // per cell
        public float fireSize = 0.5f;      // used when thingDef is a Fire
        public int lifetimeTicks = 0;      // 0 = permanent

        /// <summary>Solid creations (walls) cannot start on an occupied cell.</summary>
        public bool NeedsFreeStart
        {
            get { return thingDef != null && thingDef.passability == Traversability.Impassable; }
        }

        public override void Apply(SpellContext ctx)
        {
            foreach (IntVec3 c in Cells(ctx).ToList())
            {
                if (!c.InBounds(ctx.map)) continue;
                if (chance < 1f && Rand.Value > chance) continue;

                if (terrainDef != null)
                    ctx.map.terrainGrid.SetTerrain(c, terrainDef);

                if (thingDef != null)
                    SpawnThing(ctx, c);
            }
        }

        private IEnumerable<IntVec3> Cells(SpellContext ctx)
        {
            IntVec3 c = ctx.Cell;
            float r = radius * ctx.mods.area;
            switch (shape)
            {
                case CreationShape.Single:
                    yield return c;
                    break;
                case CreationShape.Radial:
                    foreach (IntVec3 x in GenRadial.RadialCellsAround(c, r, true)) yield return x;
                    break;
                case CreationShape.Ring:
                    foreach (IntVec3 x in GenRadial.RadialCellsAround(c, r, true))
                        if ((x - c).LengthHorizontal > r - 1.5f) yield return x;
                    break;
                case CreationShape.Line:
                    bool dragged = ctx.hasLineEnd && ctx.lineEnd != c;
                    foreach (IntVec3 x in LineCells(ctx.map, ctx.caster.Position, c, ctx.lineEnd, dragged))
                        yield return x;
                    break;
            }
        }

        private bool Blocked(Map map, IntVec3 c)
        {
            if (!c.InBounds(map)) return true;
            if (!NeedsFreeStart) return false;
            return SpellUtility.IsOccupied(map, c) || !c.Walkable(map);
        }

        /// <summary>
        /// Cells of a line-shaped creation, in build order, stopping at the first obstacle.
        /// Dragged: from the start cell toward the end cell along the dominant axis (up to maxLength).
        /// Not dragged: a line through the start cell, perpendicular to the direction from the caster,
        /// growing outward each way until blocked.
        /// Also used for the live drag preview.
        /// </summary>
        public List<IntVec3> LineCells(Map map, IntVec3 casterPos, IntVec3 start, IntVec3 end, bool dragged)
        {
            List<IntVec3> result = new List<IntVec3>();
            if (Blocked(map, start)) return result;

            if (dragged)
            {
                IntVec3 d = end - start;
                IntVec3 step = Mathf.Abs(d.x) >= Mathf.Abs(d.z)
                    ? new IntVec3(d.x >= 0 ? 1 : -1, 0, 0)
                    : new IntVec3(0, 0, d.z >= 0 ? 1 : -1);
                int length = Mathf.Min(Mathf.Max(Mathf.Abs(d.x), Mathf.Abs(d.z)) + 1, maxLength);
                for (int i = 0; i < length; i++)
                {
                    IntVec3 c = start + step * i;
                    if (Blocked(map, c)) break;
                    result.Add(c);
                }
            }
            else
            {
                IntVec3 d = start - casterPos;
                bool alongZ = Mathf.Abs(d.x) >= Mathf.Abs(d.z);
                IntVec3 step = alongZ ? new IntVec3(0, 0, 1) : new IntVec3(1, 0, 0);
                result.Add(start);
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    for (int i = 1; i <= halfLength; i++)
                    {
                        IntVec3 c = start + step * (i * sign);
                        if (Blocked(map, c)) break;
                        result.Add(c);
                    }
                }
            }
            return result;
        }

        private void SpawnThing(SpellContext ctx, IntVec3 c)
        {
            Map map = ctx.map;
            bool isFire = typeof(Fire).IsAssignableFrom(thingDef.thingClass);

            if (isFire)
            {
                if (!c.Walkable(map) || c.GetFirstThing<Fire>(map) != null) return;
            }
            else if (NeedsFreeStart)
            {
                if (Blocked(map, c)) return;   // never entomb pawns, overwrite buildings, or delete items
            }

            ThingDef stuff = thingDef.MadeFromStuff ? (stuffDef ?? GenStuff.DefaultStuffFor(thingDef)) : null;
            Thing t = ThingMaker.MakeThing(thingDef, stuff);

            Fire fire = t as Fire;
            if (fire != null) fire.fireSize = fireSize;
            if (t.def.CanHaveFaction && ctx.caster.Faction != null) t.SetFaction(ctx.caster.Faction);

            GenSpawn.Spawn(t, c, map);
            if (lifetimeTicks > 0)
                SpellManager.Register(t, Mathf.RoundToInt(lifetimeTicks * ctx.mods.duration));
        }
    }

    // ---------------------------------------------------------------- STATUS
    /// <summary>
    /// Buffs, debuffs and control, all as hediffs. Charm is a hediff of class Hediff_Charm;
    /// Paralysis uses Hediff_Frozen.
    /// </summary>
    public class Effect_Status : SpellPawnEffect
    {
        public override EffectKind Kind { get { return EffectKind.Status; } }

        public HediffDef hediffDef;
        public float severity = 1f;
        public int durationTicks = 0;   // 0 = use the hediff's own duration

        public override void Apply(SpellContext ctx)
        {
            if (hediffDef == null) return;
            foreach (Pawn p in TargetPawns(ctx))
            {
                Hediff existing = p.health.hediffSet.GetFirstHediffOfDef(hediffDef);
                if (existing != null) p.health.RemoveHediff(existing);

                Hediff h = HediffMaker.MakeHediff(hediffDef, p);
                h.Severity = severity;

                Hediff_Charm charm = h as Hediff_Charm;
                if (charm != null) charm.charmer = ctx.caster;

                HediffWithComps withComps = h as HediffWithComps;
                if (durationTicks > 0 && withComps != null)
                {
                    HediffComp_Disappears timer = withComps.TryGetComp<HediffComp_Disappears>();
                    if (timer != null)
                        timer.ticksToDisappear = Mathf.RoundToInt(durationTicks * ctx.mods.duration);
                }
                p.health.AddHediff(h);
            }
        }
    }
}
