using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace OverlordMagic
{
    /// <summary>
    /// Time Stop. While active, every pawn on the caster's map except the caster does not tick at all
    /// (see Patches.cs): they cannot move, act, bleed or age, projectiles and fire stand still, and the
    /// player cannot give frozen colonists orders. The caster can move and cast, but spells cast
    /// during the stop are queued and go off when time resumes.
    /// </summary>
    public class TimeStopManager : GameComponent
    {
        public class QueuedSpell : IExposable
        {
            public SpellDef spell;
            public Pawn caster;
            public LocalTargetInfo target;
            public LocalTargetInfo lineEnd;
            public IntVec3 origin;   // where the caster stood when casting
            public float power = 1f, area = 1f, duration = 1f;

            public void ExposeData()
            {
                Scribe_Defs.Look(ref spell, "spell");
                Scribe_References.Look(ref caster, "caster");
                Scribe_TargetInfo.Look(ref target, "target");
                Scribe_TargetInfo.Look(ref lineEnd, "lineEnd");
                Scribe_Values.Look(ref origin, "origin");
                Scribe_Values.Look(ref power, "power", 1f);
                Scribe_Values.Look(ref area, "area", 1f);
                Scribe_Values.Look(ref duration, "duration", 1f);
            }
        }

        public static TimeStopManager Instance;

        private int endTick = -1;
        private Pawn caster;
        private List<QueuedSpell> queue = new List<QueuedSpell>();

        public TimeStopManager(Game game)
        {
            Instance = this;
        }

        public static bool Active
        {
            get { return Instance != null && Instance.endTick >= 0; }
        }

        public static Pawn Caster
        {
            get { return Instance != null ? Instance.caster : null; }
        }

        /// <summary>True if this pawn is frozen right now.</summary>
        public static bool ShouldFreeze(Pawn p)
        {
            TimeStopManager i = Instance;
            if (i == null || i.endTick < 0 || p == null || p == i.caster) return false;
            Map map = i.caster != null ? i.caster.Map : null;
            return map != null && p.Map == map;
        }

        public static void Begin(Pawn caster, int ticks)
        {
            TimeStopManager i = Instance;
            if (i == null || i.endTick >= 0) return;
            i.caster = caster;
            i.endTick = Find.TickManager.TicksGame + Mathf.Max(1, ticks);
            Messages.Message(caster.LabelShort + " has stopped time.", caster, MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>Ticks of Time Stop left, or -1 if time is not stopped.</summary>
        public static int RemainingTicks
        {
            get { return Active ? Mathf.Max(0, Instance.endTick - Find.TickManager.TicksGame) : -1; }
        }

        public static void Enqueue(Pawn caster, SpellDef spell, LocalTargetInfo target, LocalTargetInfo lineEnd, SpellModifiers mods, IntVec3 origin)
        {
            if (Instance == null) return;
            Instance.queue.Add(new QueuedSpell
            {
                spell = spell,
                caster = caster,
                target = target,
                lineEnd = lineEnd,
                origin = origin,
                power = mods.power,
                area = mods.area,
                duration = mods.duration
            });
        }

        public override void GameComponentTick()
        {
            if (endTick < 0) return;
            if (caster == null || caster.Dead || !caster.Spawned || Find.TickManager.TicksGame >= endTick)
                End();
        }

        private void End()
        {
            endTick = -1;
            caster = null;

            List<QueuedSpell> pending = queue.ToList();
            queue.Clear();
            Messages.Message("Time resumes.", MessageTypeDefOf.NeutralEvent, false);

            foreach (QueuedSpell q in pending)
            {
                if (q.spell == null || q.caster == null || q.caster.Dead || !q.caster.Spawned) continue;
                SpellModifiers mods = new SpellModifiers { power = q.power, area = q.area, duration = q.duration };
                SpellCaster.Execute(q.caster, q.spell, q.target, q.lineEnd, mods, q.origin);
            }
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref endTick, "endTick", -1);
            Scribe_References.Look(ref caster, "caster");
            Scribe_Collections.Look(ref queue, "queue", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && queue == null)
                queue = new List<QueuedSpell>();
        }
    }

    /// <summary>Time Stop is a status effect on everyone else; the freezing itself is done by TimeStopManager.</summary>
    public class Effect_TimeStop : Effect_Status
    {
        public override void Apply(SpellContext ctx)
        {
            TimeStopManager.Begin(ctx.caster, Mathf.RoundToInt(durationTicks * ctx.mods.duration));
            base.Apply(ctx);   // marker hediff on every affected pawn, so the player can see why they are frozen
        }
    }
}
