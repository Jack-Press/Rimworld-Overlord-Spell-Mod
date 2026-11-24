using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace OverlordMagic
{
    public class CompProperties_SpellCaster : CompProperties
    {
        public CompProperties_SpellCaster()
        {
            compClass = typeof(CompSpellCaster);
        }
    }

    /// <summary>
    /// Added to humanlike pawns by an XML patch. Gives player-controlled pawns a
    /// "Cast spell" gizmo. Also the future home of the pawn's MP pool and known spells.
    /// </summary>
    public class CompSpellCaster : ThingComp
    {
        // ---- Mana: infinite for the test, but all casts go through these two methods. ----
        // (A static field rather than a const, so the compiler does not treat the finite-mana path as unreachable.)
        public static bool InfiniteMana = true;
        private float mana = 0f;

        public bool HasMana(float cost)
        {
            return InfiniteMana || mana >= cost;
        }

        public void ConsumeMana(float cost)
        {
            if (!InfiniteMana) mana -= cost;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref mana, "om_mana", 0f);
        }

        // ---- Who can cast: only player-controlled colonists. ----
        private static bool IsPlayerCaster(Pawn p)
        {
            return p.Spawned && !p.Dead && !p.Downed
                && p.Faction != null && p.Faction.IsPlayer
                && p.RaceProps.Humanlike && !p.IsSlave;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
                yield return g;

            Pawn pawn = parent as Pawn;
            if (pawn == null || !IsPlayerCaster(pawn)) yield break;

            Command_Action cast = new Command_Action();
            cast.defaultLabel = "Cast spell";
            cast.defaultDesc = "Choose a spell to cast. Casting takes a short incantation; giving the pawn another order cancels it. (Test build: no mana cost, no requirements.)";
            cast.icon = ContentFinder<Texture2D>.Get("OverlordMagic/UI/CastSpell", false) ?? BaseContent.BadTex;
            cast.action = delegate { OpenSpellMenu(pawn); };
            yield return cast;
        }

        private void OpenSpellMenu(Pawn pawn)
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            foreach (SpellDef spell in DefDatabase<SpellDef>.AllDefsListForReading.OrderBy(s => s.tier).ThenBy(s => s.label))
            {
                SpellDef s = spell;
                // Hovering a spell shows its range (or, for caster-centered spells, how far it reaches).
                options.Add(new FloatMenuOption(s.MenuLabel, delegate { BeginCast(pawn, s); },
                    MenuOptionPriority.Default, delegate { SpellTargetingState.Hover(pawn, s); }));
            }
            if (options.Count == 0)
                options.Add(new FloatMenuOption("(no spells defined)", null));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void BeginCast(Pawn pawn, SpellDef spell)
        {
            string reason;
            if (!SpellCaster.CanCast(pawn, spell, out reason))
            {
                Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (spell.targetKind == TargetKind.Self)
            {
                SpellCaster.OrderCast(pawn, spell, new LocalTargetInfo(pawn), LocalTargetInfo.Invalid);
                return;
            }

            SpellTargetingState.Begin(pawn, spell);   // draws the range ring and area preview while aiming
            Find.Targeter.BeginTargeting(MakeParams(pawn, spell),
                delegate (LocalTargetInfo t) { OnTargetChosen(pawn, spell, t); },
                pawn, null, TexCommand.Attack);
        }

        private static void OnTargetChosen(Pawn pawn, SpellDef spell, LocalTargetInfo t)
        {
            if (spell.targetKind == TargetKind.Line)
            {
                string reason;
                if (!SpellCaster.Validate(pawn, spell, t, out reason))
                {
                    Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                    return;
                }
                // Keep the mouse button held and drag to choose the wall's direction and length.
                SpellTargetingState.StartDrag(pawn, spell, t.Cell);
                return;
            }
            SpellCaster.OrderCast(pawn, spell, t, LocalTargetInfo.Invalid);
        }

        /// <summary>Which things the targeter lets the player click on (invalid ones show the "blocked" cursor).</summary>
        private static TargetingParameters MakeParams(Pawn pawn, SpellDef spell)
        {
            TargetingParameters parms = new TargetingParameters();
            // By default the targeter refuses items (including corpses) that are not auto-attackable map objects.
            parms.mapObjectTargetsMustBeAutoAttackable = false;
            switch (spell.targetKind)
            {
                case TargetKind.Pawn:
                    parms.canTargetPawns = true;
                    parms.canTargetSelf = true;
                    break;
                case TargetKind.Corpse:
                    parms.canTargetItems = true;   // corpses are items; the validator below keeps only corpses
                    break;
                case TargetKind.Cell:
                case TargetKind.Line:
                    parms.canTargetLocations = true;
                    break;
                default:
                    parms.canTargetPawns = true;
                    parms.canTargetSelf = true;
                    parms.canTargetBuildings = true;
                    parms.canTargetItems = true;
                    parms.canTargetLocations = true;
                    break;
            }
            parms.validator = delegate (TargetInfo x) { return IsValidTarget(pawn, spell, x); };
            return parms;
        }

        private static bool IsValidTarget(Pawn pawn, SpellDef spell, TargetInfo x)
        {
            IntVec3 cell = x.Cell;
            if (spell.range > 0f && (cell - pawn.Position).LengthHorizontal > spell.range) return false;

            switch (spell.targetKind)
            {
                case TargetKind.Corpse:
                    Corpse corpse = x.Thing as Corpse;
                    return corpse != null && (!spell.CorpseMustBeFresh || corpse.GetRotStage() == RotStage.Fresh);
                case TargetKind.Pawn:
                    return x.HasThing && x.Thing is Pawn;
                case TargetKind.Cell:
                case TargetKind.Line:
                    return !spell.NeedsFreeCell || SpellUtility.IsCellFree(pawn.Map, cell);
                default:
                    return true;
            }
        }
    }
}
