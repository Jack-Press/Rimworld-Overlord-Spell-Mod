using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace OverlordMagic
{
    /// <summary>
    /// Harmony patches. Each patch class is applied separately, so one failing (for example after a game
    /// update) is logged without disabling the others.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class HarmonyInit
    {
        static HarmonyInit()
        {
            Harmony harmony = new Harmony("yourname.overlordmagic");
            Type[] patches =
            {
                typeof(Patch_PawnTick), typeof(Patch_ProjectileTick), typeof(Patch_FireTick),
                typeof(Patch_PawnGizmos), typeof(Patch_OrderedJob), typeof(Patch_CasterCannotAttack), typeof(Patch_TemporaryStatusEndsOnDeath),
                typeof(Patch_CharmBreaksOnDamage)
            };
            foreach (Type t in patches)
            {
                try
                {
                    harmony.CreateClassProcessor(t).Patch();
                }
                catch (Exception e)
                {
                    Log.Error("[OverlordMagic] Could not apply " + t.Name + ": " + e);
                }
            }
        }

        /// <summary>The per-tick methods of a type (Tick, and TickInterval if this game version has it).</summary>
        public static IEnumerable<MethodBase> TickMethods(Type type)
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (MethodInfo m in type.GetMethods(flags))
            {
                if (m.Name == "Tick" || m.Name == "TickInterval") yield return m;
            }
        }
    }

    // ------------------------------------------------------------ TIME STOP
    /// <summary>Frozen pawns do not tick at all: no movement, jobs, needs, bleeding or aging.</summary>
    [HarmonyPatch]
    public static class Patch_PawnTick
    {
        public static IEnumerable<MethodBase> TargetMethods() { return HarmonyInit.TickMethods(typeof(Pawn)); }
        public static bool Prefix(Pawn __instance) { return !TimeStopManager.ShouldFreeze(__instance); }
    }

    /// <summary>Projectiles hang in the air.</summary>
    [HarmonyPatch]
    public static class Patch_ProjectileTick
    {
        public static IEnumerable<MethodBase> TargetMethods() { return HarmonyInit.TickMethods(typeof(Projectile)); }
        public static bool Prefix() { return !TimeStopManager.Active; }
    }

    /// <summary>Fire does not spread.</summary>
    [HarmonyPatch]
    public static class Patch_FireTick
    {
        public static IEnumerable<MethodBase> TargetMethods() { return HarmonyInit.TickMethods(typeof(Fire)); }
        public static bool Prefix() { return !TimeStopManager.Active; }
    }

    /// <summary>Frozen pawns show no buttons (no drafting, no spells).</summary>
    [HarmonyPatch(typeof(Pawn), "GetGizmos")]
    public static class Patch_PawnGizmos
    {
        public static void Postfix(Pawn __instance, ref IEnumerable<Gizmo> __result)
        {
            if (TimeStopManager.ShouldFreeze(__instance))
                __result = Enumerable.Empty<Gizmo>();
        }
    }

    /// <summary>
    /// Frozen pawns cannot be ordered. The free caster can only walk and cast during the stop;
    /// other orders (attacking, hauling, ...) are refused because they would act on a stopped world.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), "TryTakeOrderedJob")]
    public static class Patch_OrderedJob
    {
        private static readonly FieldInfo pawnField = AccessTools.Field(typeof(Pawn_JobTracker), "pawn");

        public static bool Prefix(Pawn_JobTracker __instance, Job job, ref bool __result)
        {
            if (!TimeStopManager.Active) return true;
            Pawn pawn = pawnField.GetValue(__instance) as Pawn;
            if (pawn == null) return true;

            if (TimeStopManager.ShouldFreeze(pawn))
            {
                Messages.Message(pawn.LabelShort + " is frozen in time and cannot act.", MessageTypeDefOf.RejectInput, false);
                __result = false;
                return false;
            }
            if (pawn == TimeStopManager.Caster && job != null
                && job.def.defName != "Goto" && job.def.defName != "OM_CastSpell")
            {
                Messages.Message("Cannot interact with the world while time is stopped. Walk or cast.", MessageTypeDefOf.RejectInput, false);
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// The free caster cannot attack while time is stopped (melee or ranged). The attack waits and
    /// goes ahead once time resumes.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_CasterCannotAttack
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            MethodInfo burst = typeof(Verb).GetMethod("TryCastNextBurstShot", flags);
            if (burst != null) yield return burst;
            MethodInfo melee = typeof(Verb_MeleeAttack).GetMethod("TryCastShot", flags);
            if (melee != null) yield return melee;
            MethodInfo launch = typeof(Verb_LaunchProjectile).GetMethod("TryCastShot", flags);
            if (launch != null) yield return launch;
        }

        public static bool Prefix(Verb __instance)
        {
            return !(TimeStopManager.Active && __instance.CasterPawn != null && __instance.CasterPawn == TimeStopManager.Caster);
        }
    }

    /// <summary>Temporary magical conditions (charm, haste, paralysis, time stop) are lost when the pawn dies.</summary>
    [HarmonyPatch(typeof(Pawn), "Kill")]
    public static class Patch_TemporaryStatusEndsOnDeath
    {
        public static void Postfix(Pawn __instance)
        {
            if (__instance == null || __instance.health == null || !__instance.Dead) return;
            List<Hediff> temporary = __instance.health.hediffSet.hediffs
                .Where(h => h.def.HasModExtension<DefModExt_Temporary>())
                .ToList();
            foreach (Hediff h in temporary)
                __instance.health.RemoveHediff(h);
        }
    }

    // ---------------------------------------------------------------- CHARM
    /// <summary>Hurting a charmed pawn (by the player's side) breaks the charm, so it fights back.</summary>
    [HarmonyPatch(typeof(Pawn), "PostApplyDamage")]
    public static class Patch_CharmBreaksOnDamage
    {
        public static void Postfix(Pawn __instance, DamageInfo dinfo)
        {
            if (__instance.Dead || __instance.health == null) return;
            Thing instigator = dinfo.Instigator;
            if (instigator == null || instigator.Faction != Faction.OfPlayer) return;

            Hediff_Charm charm = __instance.health.hediffSet.GetFirstHediff<Hediff_Charm>();
            if (charm != null)
                __instance.health.RemoveHediff(charm);
        }
    }
}
