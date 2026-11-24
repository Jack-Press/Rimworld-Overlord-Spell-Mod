using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace OverlordMagic
{
    /// <summary>
    /// Casting pipeline:
    ///   OrderCast  (player picked a target)  -> validates, gives the caster a CastSpell job
    ///   JobDriver_CastSpell                  -> incantation time (only runs while time moves;
    ///                                           any other order cancels it)
    ///   CompleteCast                         -> re-validates, spends MP, queues (Time Stop) or runs
    ///   Execute / ApplyEffects               -> projectile launch or the effects themselves
    /// </summary>
    public static class SpellCaster
    {
        private static JobDef castJobDef;

        public static JobDef CastJobDef
        {
            get
            {
                if (castJobDef == null) castJobDef = DefDatabase<JobDef>.GetNamed("OM_CastSpell");
                return castJobDef;
            }
        }

        /// <summary>Jobs only carry simple fields, so the spell is stored as its short hash in job.count.</summary>
        public static SpellDef SpellFromJob(Job job)
        {
            ushort hash = (ushort)job.count;
            foreach (SpellDef s in DefDatabase<SpellDef>.AllDefsListForReading)
                if (s.shortHash == hash) return s;
            return null;
        }

        /// <summary>
        /// Gate for casting. For the test this only checks listed requirements and MP (infinite);
        /// cooldowns, class checks, etc. will be enforced here later.
        /// </summary>
        public static bool CanCast(Pawn caster, SpellDef spell, out string reason)
        {
            reason = null;

            if (spell.requirements != null)
            {
                foreach (SpellRequirement req in spell.requirements)
                {
                    if (!req.IsSatisfied(caster, out reason)) return false;
                }
            }

            CompSpellCaster comp = caster.TryGetComp<CompSpellCaster>();
            if (comp != null && !comp.HasMana(spell.mpCost))
            {
                reason = "Not enough mana.";
                return false;
            }
            return true;
        }

        /// <summary>Is this a legal target for the spell? Used when ordering and again when the spell goes off.</summary>
        public static bool Validate(Pawn caster, SpellDef spell, LocalTargetInfo target, out string reason)
        {
            reason = null;
            if (caster == null || caster.Map == null || caster.Dead)
            {
                reason = "The caster cannot cast right now.";
                return false;
            }
            if (!CanCast(caster, spell, out reason)) return false;

            Map map = caster.Map;
            switch (spell.targetKind)
            {
                case TargetKind.Pawn:
                    if (!(target.Thing is Pawn)) { reason = "That spell needs a creature as its target."; return false; }
                    break;
                case TargetKind.Corpse:
                    Corpse corpse = target.Thing as Corpse;
                    if (corpse == null) { reason = "That spell can only target a corpse."; return false; }
                    if (spell.CorpseMustBeFresh && corpse.GetRotStage() != RotStage.Fresh)
                    {
                        reason = "That corpse has already started to rot.";
                        return false;
                    }
                    break;
                case TargetKind.Cell:
                case TargetKind.Line:
                    if (!target.Cell.IsValid || !target.Cell.InBounds(map)) { reason = "Pick a location."; return false; }
                    break;
            }

            IntVec3 cell = target.HasThing ? target.Thing.PositionHeld : target.Cell;
            if (spell.targetKind != TargetKind.Self && spell.range > 0f
                && (cell - caster.Position).LengthHorizontal > spell.range)
            {
                reason = "Target is out of range.";
                return false;
            }
            if (spell.NeedsFreeCell && !SpellUtility.IsCellFree(map, cell))
            {
                reason = "That location is occupied.";
                return false;
            }
            return true;
        }

        /// <summary>The player has chosen a target: start the incantation (a job).</summary>
        public static void OrderCast(Pawn caster, SpellDef spell, LocalTargetInfo target, LocalTargetInfo lineEnd)
        {
            string reason;
            if (TimeStopManager.ShouldFreeze(caster))
            {
                Messages.Message("Time is stopped; " + caster.LabelShort + " cannot move or cast.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (!Validate(caster, spell, target, out reason))
            {
                Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                return;
            }

            Job job = JobMaker.MakeJob(CastJobDef, target);
            job.targetB = lineEnd;
            job.targetC = new LocalTargetInfo(caster);   // the progress bar is drawn over the caster
            job.count = spell.shortHash;
            caster.jobs.TryTakeOrderedJob(job);
        }

        /// <summary>The incantation finished without being interrupted: the spell goes off.</summary>
        public static void CompleteCast(Pawn caster, SpellDef spell, LocalTargetInfo target, LocalTargetInfo lineEnd)
        {
            string reason;
            if (!Validate(caster, spell, target, out reason))
            {
                Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                return;
            }

            CompSpellCaster comp = caster.TryGetComp<CompSpellCaster>();
            if (comp != null) comp.ConsumeMana(spell.mpCost);

            SpellModifiers mods = new SpellModifiers { power = spell.damageModifier };
            IntVec3 origin = caster.Position;   // where the spell is cast from, even if it goes off later

            // While time is stopped, the free caster's spells wait for time to resume.
            if (TimeStopManager.Active)
            {
                TimeStopManager.Enqueue(caster, spell, target, lineEnd, mods, origin);
                Messages.Message(spell.LabelCap.ToString() + " will take effect when time resumes.", caster, MessageTypeDefOf.NeutralEvent, false);
                return;
            }
            Execute(caster, spell, target, lineEnd, mods, origin);
        }

        public static void Execute(Pawn caster, SpellDef spell, LocalTargetInfo target, LocalTargetInfo lineEnd, SpellModifiers mods, IntVec3 origin)
        {
            if (spell.castType == CastType.Projectile)
                Launch(caster, spell, target, mods, origin);
            else
                ApplyEffects(spell, caster, target, mods, lineEnd, origin);
        }

        private static void Launch(Pawn caster, SpellDef spell, LocalTargetInfo target, SpellModifiers mods, IntVec3 origin)
        {
            if (caster.Map == null) return;
            if (!origin.IsValid || !origin.InBounds(caster.Map)) origin = caster.Position;
            for (int i = 0; i < Mathf.Max(1, spell.projectileCount); i++)
            {
                Thing thing = GenSpawn.Spawn(spell.projectileDef, origin, caster.Map);
                SpellProjectile proj = thing as SpellProjectile;
                if (proj == null)
                {
                    Log.Error("[OverlordMagic] " + spell.defName + ": projectileDef must use thingClass OverlordMagic.SpellProjectile.");
                    thing.Destroy();
                    return;
                }
                proj.spell = spell;
                proj.power = mods.power;
                proj.area = mods.area;
                proj.duration = mods.duration;
                proj.Launch(caster, origin.ToVector3Shifted(), target, target, ProjectileHitFlags.All);
            }
        }

        /// <summary>Runs every effect of the spell at the target. Used by direct casts and projectile impacts.</summary>
        public static void ApplyEffects(SpellDef spell, Pawn caster, LocalTargetInfo target, SpellModifiers mods, LocalTargetInfo lineEnd, IntVec3 origin)
        {
            if (caster == null || caster.Map == null) return;
            SpellContext ctx = new SpellContext
            {
                spell = spell,
                caster = caster,
                map = caster.Map,
                target = target,
                mods = mods,
                hasLineEnd = lineEnd.IsValid,
                lineEnd = lineEnd.IsValid ? lineEnd.Cell : IntVec3.Invalid,
                hasOrigin = origin.IsValid,
                origin = origin
            };

            foreach (SpellEffect effect in spell.effects)
            {
                try
                {
                    effect.Apply(ctx);
                }
                catch (Exception e)
                {
                    Log.Error("[OverlordMagic] Error applying " + effect.GetType().Name + " of " + spell.defName + ": " + e);
                }
            }
        }
    }

    /// <summary>The incantation: chant for a short time, then the spell goes off.</summary>
    public class JobDriver_CastSpell : JobDriver
    {
        private SpellDef Spell { get { return SpellCaster.SpellFromJob(job); } }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        public override string GetReport()
        {
            SpellDef s = Spell;
            return s != null ? "casting " + s.label + "." : base.GetReport();
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            SpellDef spell = Spell;
            this.FailOn(() => spell == null || pawn.Dead || pawn.Downed);

            Toil start = new Toil();
            start.initAction = delegate
            {
                MoteMaker.ThrowText(pawn.DrawPos, pawn.Map, spell.label);
            };
            start.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return start;

            // Only advances while the game is running; a new order from the player ends the job here
            // and the spell never goes off.
            Toil wait = Toils_General.Wait(spell.IncantationTicks, TargetIndex.A);
            wait.WithProgressBarToilDelay(TargetIndex.C);   // C is the caster (see OrderCast): the bar fills as the incantation completes
            yield return wait;

            Toil cast = new Toil();
            cast.initAction = delegate
            {
                SpellCaster.CompleteCast(pawn, spell, job.targetA, job.targetB);
            };
            cast.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return cast;
        }
    }

    /// <summary>
    /// A projectile that carries a spell. When it lands, it runs the spell's effects at the
    /// impact point instead of doing ordinary bullet damage.
    /// </summary>
    public class SpellProjectile : Projectile
    {
        public SpellDef spell;
        public float power = 1f;
        public float area = 1f;
        public float duration = 1f;

        protected override void Impact(Thing hitThing, bool blockedByShield = false)
        {
            Map map = this.Map;
            Pawn caster = this.launcher as Pawn;

            if (spell != null && caster != null && map != null)
            {
                LocalTargetInfo target = hitThing != null ? new LocalTargetInfo(hitThing) : new LocalTargetInfo(this.Position);
                SpellModifiers mods = new SpellModifiers { power = power, area = area, duration = duration };
                SpellCaster.ApplyEffects(spell, caster, target, mods, LocalTargetInfo.Invalid, caster.Position);
            }
            this.Destroy(DestroyMode.Vanish);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref spell, "spell");
            Scribe_Values.Look(ref power, "power", 1f);
            Scribe_Values.Look(ref area, "area", 1f);
            Scribe_Values.Look(ref duration, "duration", 1f);
        }
    }

    /// <summary>
    /// Destroys summoned / created things when their lifetime ends, and draws the spell-targeting
    /// overlays (range ring, area preview, wall drag preview). Game components are created
    /// automatically, and this one saves with the game.
    /// </summary>
    public class SpellManager : GameComponent
    {
        public class Entry : IExposable
        {
            public Thing thing;
            public int expireTick;

            public void ExposeData()
            {
                Scribe_References.Look(ref thing, "thing");
                Scribe_Values.Look(ref expireTick, "expireTick");
            }
        }

        private List<Entry> entries = new List<Entry>();

        public SpellManager(Game game) { }

        public static void Register(Thing thing, int lifetimeTicks)
        {
            SpellManager mgr = Current.Game.GetComponent<SpellManager>();
            if (mgr == null || thing == null) return;
            mgr.entries.Add(new Entry { thing = thing, expireTick = Find.TickManager.TicksGame + lifetimeTicks });
        }

        public override void GameComponentTick()
        {
            if (entries.Count == 0) return;
            int now = Find.TickManager.TicksGame;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Entry e = entries[i];
                if (e.thing == null || e.thing.Destroyed)
                {
                    entries.RemoveAt(i);
                }
                else if (now >= e.expireTick)
                {
                    SpellUtility.Flash(e.thing.MapHeld, e.thing.PositionHeld, 1f);
                    e.thing.Destroy(DestroyMode.Vanish);
                    entries.RemoveAt(i);
                }
            }
        }

        public override void GameComponentUpdate()
        {
            SpellTargetingState.Update();
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref entries, "entries", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && entries == null)
                entries = new List<Entry>();
        }
    }

    /// <summary>
    /// What the player sees while aiming: the caster's range, the spell's area under the cursor,
    /// and (for Wall of Stone) a click-and-drag line like the architect menu.
    /// </summary>
    public static class SpellTargetingState
    {
        private static Pawn pawn;
        private static SpellDef spell;
        private static bool dragging;
        private static IntVec3 dragStart;
        private static int dragStartFrame;

        private const float MaxDrawnRadius = 56f;

        // Spell currently hovered in the spell menu (set from the menu's GUI, drawn from Update).
        private static Pawn hoverPawn;
        private static SpellDef hoverSpell;
        private static int hoverFrame = -100;

        /// <summary>Called every GUI frame while a spell is hovered in the menu.</summary>
        public static void Hover(Pawn p, SpellDef s)
        {
            hoverPawn = p;
            hoverSpell = s;
            hoverFrame = Time.frameCount;
        }

        private static void DrawHover()
        {
            if (hoverSpell == null || hoverPawn == null || Time.frameCount - hoverFrame > 2) return;
            if (hoverPawn.Dead || hoverPawn.Map == null || hoverPawn.Map != Find.CurrentMap) return;

            if (hoverSpell.targetKind == TargetKind.Self)
            {
                // Caster-centered spell: show how far it reaches.
                float r = hoverSpell.PreviewRadius;
                if (r > 0f) GenDraw.DrawRadiusRing(hoverPawn.Position, Mathf.Min(r, MaxDrawnRadius));
            }
            else if (hoverSpell.range > 0f)
            {
                GenDraw.DrawRadiusRing(hoverPawn.Position, Mathf.Min(hoverSpell.range, MaxDrawnRadius));
            }
        }

        public static void Begin(Pawn p, SpellDef s)
        {
            pawn = p;
            spell = s;
            dragging = false;
        }

        /// <summary>The first click of a line spell: from here the player drags to choose direction and length.</summary>
        public static void StartDrag(Pawn p, SpellDef s, IntVec3 start)
        {
            pawn = p;
            spell = s;
            dragging = true;
            dragStart = start;
            dragStartFrame = Time.frameCount;
        }

        private static void Clear()
        {
            pawn = null;
            spell = null;
            dragging = false;
        }

        public static void Update()
        {
            DrawHover();
            if (spell == null || pawn == null) return;
            if (pawn.Dead || pawn.Map == null || pawn.Map != Find.CurrentMap) { Clear(); return; }

            if (dragging) { UpdateDrag(); return; }
            if (!Find.Targeter.IsTargeting) { Clear(); return; }

            DrawRange();
            float r = spell.PreviewRadius;
            if (r > 0f)
            {
                IntVec3 m = UI.MouseCell();
                if (m.InBounds(pawn.Map))
                    GenDraw.DrawRadiusRing(m, Mathf.Min(r, MaxDrawnRadius));
            }
        }

        private static void DrawRange()
        {
            if (spell.range > 0f && spell.targetKind != TargetKind.Self)
                GenDraw.DrawRadiusRing(pawn.Position, Mathf.Min(spell.range, MaxDrawnRadius));
        }

        private static void UpdateDrag()
        {
            // Right click or Escape cancels.
            if (UnityEngine.Input.GetMouseButtonDown(1) || UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                Clear();
                return;
            }

            IntVec3 mouse = UI.MouseCell();
            Effect_Creation line = spell.LineEffect;
            List<IntVec3> cells = line != null
                ? line.LineCells(pawn.Map, pawn.Position, dragStart, mouse, mouse != dragStart)
                : new List<IntVec3> { dragStart };

            DrawRange();
            if (cells.Count > 0) GenDraw.DrawFieldEdges(cells);

            // Releasing the mouse button (not on the frame it was pressed) confirms the wall.
            if (Time.frameCount > dragStartFrame && !UnityEngine.Input.GetMouseButton(0))
            {
                Pawn p = pawn;
                SpellDef s = spell;
                IntVec3 start = dragStart;
                Clear();
                SpellCaster.OrderCast(p, s, new LocalTargetInfo(start), new LocalTargetInfo(mouse));
            }
        }
    }
}
