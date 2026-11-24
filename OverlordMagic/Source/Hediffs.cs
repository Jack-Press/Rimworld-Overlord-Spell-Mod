using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace OverlordMagic
{
    /// <summary>
    /// Status effect, control type. For the duration the pawn leaves its old group and joins the
    /// caster's faction. Pawns the player cannot order around (animals, mechanoids) also get a
    /// lord that makes them follow the caster and fight enemies. Ends when the timer runs out,
    /// or when the player's side damages the charmed pawn.
    /// </summary>
    /// <summary>
    /// Put on the HediffDefs of temporary magical conditions (Haste, Charm, Paralysis, Time Stop).
    /// They are removed when the pawn dies (see Patch_TemporaryStatusEndsOnDeath).
    /// </summary>
    public class DefModExt_Temporary : DefModExtension
    {
    }

    public class Hediff_Charm : HediffWithComps
    {
        public Pawn charmer;
        private Faction originalFaction;   // null is valid: wild animals have no faction
        private bool factionSwapped;
        private Lord originalLord;

        public override void PostAdd(DamageInfo? dinfo)
        {
            base.PostAdd(dinfo);
            if (pawn.Faction == Faction.OfPlayer) return;   // nothing to charm: already on the player's side

            factionSwapped = true;
            originalFaction = pawn.Faction;

            // Leave the old group first, otherwise its orders keep running (this was the "wanders aimlessly
            // while its entourage attacks" problem).
            originalLord = pawn.GetLord();
            if (originalLord != null)
                originalLord.Notify_PawnLost(pawn, PawnLostCondition.ChangedFaction);

            pawn.SetFaction(Faction.OfPlayer);
            if (pawn.jobs != null) pawn.jobs.StopAll();

            // Colonist-like pawns are ordered by the player, and start out drafted. Others need their own AI.
            if (pawn.RaceProps.Humanlike)
            {
                if (pawn.drafter != null) pawn.drafter.Drafted = true;
            }
            else if (pawn.Spawned)
            {
                LordMaker.MakeNewLord(Faction.OfPlayer, new LordJob_CharmedFight(charmer), pawn.Map, new[] { pawn });
            }
        }

        public override void PostRemoved()
        {
            base.PostRemoved();
            if (!factionSwapped) return;

            Lord ours = pawn.GetLord();
            if (ours != null && ours.LordJob is LordJob_CharmedFight)
                ours.Notify_PawnLost(pawn, PawnLostCondition.ForcedByPlayerAction);

            // Also runs when the charmed pawn dies: the charm is lost and the corpse goes back to its old faction.
            if (pawn.Faction == Faction.OfPlayer)
            {
                if (!pawn.Dead && pawn.drafter != null && pawn.Drafted) pawn.drafter.Drafted = false;
                pawn.SetFaction(originalFaction);
            }
            if (pawn.Dead) return;

            // Back into the old group if it still exists; otherwise hostile pawns get a fresh assault lord.
            if (pawn.Spawned && pawn.GetLord() == null)
            {
                if (originalLord != null && pawn.Map.lordManager.lords.Contains(originalLord))
                {
                    originalLord.AddPawn(pawn);
                }
                else if (originalFaction != null && originalFaction.HostileTo(Faction.OfPlayer))
                {
                    LordMaker.MakeNewLord(originalFaction, new LordJob_AssaultColony(originalFaction), pawn.Map, new[] { pawn });
                }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref charmer, "charmer");
            Scribe_References.Look(ref originalFaction, "originalFaction");
            Scribe_References.Look(ref originalLord, "originalLord");
            Scribe_Values.Look(ref factionSwapped, "factionSwapped", false);
        }
    }

    /// <summary>
    /// Status effect, debuff type. The pawn cannot move or act (a stun that is renewed every
    /// tick) but stays standing. Used for Paralysis.
    /// </summary>
    public class Hediff_Frozen : HediffWithComps
    {
        public override void Tick()
        {
            base.Tick();
            if (pawn.Spawned && pawn.stances != null && pawn.stances.stunner != null)
            {
                pawn.stances.stunner.StunFor(2, null, false, false);
                if (pawn.pather != null && pawn.pather.Moving) pawn.pather.StopDead();
            }
        }
    }

    /// <summary>
    /// Shows the remaining time of a timed condition (anything with a Disappears comp) in the
    /// condition's label and tooltip, so the player knows when it ends.
    /// </summary>
    public class HediffCompProperties_ShowTimer : HediffCompProperties
    {
        public HediffCompProperties_ShowTimer()
        {
            compClass = typeof(HediffComp_ShowTimer);
        }
    }

    public class HediffComp_ShowTimer : HediffComp
    {
        private int TicksLeft
        {
            get
            {
                HediffComp_Disappears timer = parent.TryGetComp<HediffComp_Disappears>();
                return timer != null ? timer.ticksToDisappear : -1;
            }
        }

        public override string CompLabelInBracketsExtra
        {
            get
            {
                int t = TicksLeft;
                return t >= 0 ? t.ToStringTicksToPeriod() : null;
            }
        }

        public override string CompTipStringExtra
        {
            get
            {
                int t = TicksLeft;
                return t >= 0 ? "Ends in " + t.ToStringTicksToPeriod() + "." : null;
            }
        }
    }

    /// <summary>
    /// Marker shown on pawns caught in Time Stop. The freezing itself is done in Patches.cs;
    /// this hediff just tells the player why, and removes itself when time resumes.
    /// </summary>
    public class Hediff_TimeStopped : HediffWithComps
    {
        public override bool ShouldRemove
        {
            get { return !TimeStopManager.Active || base.ShouldRemove; }
        }

        // Time Stop is timed by the manager, not by a hediff timer, so show its remaining time here.
        public override string LabelInBrackets
        {
            get
            {
                int t = TimeStopManager.RemainingTicks;
                string extra = t >= 0 ? t.ToStringTicksToPeriod() : null;
                string baseLabel = base.LabelInBrackets;
                if (string.IsNullOrEmpty(baseLabel)) return extra;
                return extra == null ? baseLabel : baseLabel + ", " + extra;
            }
        }
    }
}
