using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace OverlordMagic
{
    /// <summary>How the spell reaches its target.</summary>
    public enum CastType
    {
        Projectile, // fired from the caster toward the aimed point; effects happen where it lands
        Target      // effects happen immediately at the target (no travel)
    }

    /// <summary>What the player is asked to click on.</summary>
    public enum TargetKind
    {
        Any,    // a pawn, thing, or ground cell
        Pawn,   // a pawn (may be the caster)
        Corpse, // a corpse (and nothing else)
        Cell,   // a ground cell
        Line,   // a ground cell, then drag to choose a direction (like placing walls)
        Self    // no targeting; centered on the caster
    }

    public enum EffectKind { Damage, Heal, Summon, Creation, Status }

    /// <summary>
    /// A spell, defined in XML: a cast type, a targeting kind, and a list of effects.
    /// tier / mpCost / damageModifier / cooldownTicks / requirements are hooks for the
    /// fuller magic system. For the test they are stored but not enforced (infinite MP).
    /// </summary>
    public class SpellDef : Def
    {
        public CastType castType = CastType.Target;
        public TargetKind targetKind = TargetKind.Any;
        public float range = 30f;                 // <= 0 means unlimited

        // Projectile spells
        public ThingDef projectileDef;
        public int projectileCount = 1;

        public List<SpellEffect> effects = new List<SpellEffect>();

        // Hooks for the future system (values come from the spell spreadsheet)
        public int tier = 1;
        public float mpCost = 0f;
        public float damageModifier = 1f;         // multiplies the power of every effect
        public int cooldownTicks = 0;             // not enforced yet
        public List<SpellRequirement> requirements = new List<SpellRequirement>();

        // Incantation: ticks the caster spends chanting before the spell goes off.
        // -1 = automatic (30 + 15 per tier; 60 ticks = 1 second at normal speed).
        public int incantationTicks = -1;

        public int IncantationTicks
        {
            get { return incantationTicks >= 0 ? incantationTicks : 30 + 15 * tier; }
        }

        public IEnumerable<EffectKind> EffectKinds
        {
            get { return effects.Select(e => e.Kind).Distinct(); }
        }

        /// <summary>Radius of the largest area effect, for the targeting preview (0 = none).</summary>
        public float PreviewRadius
        {
            get
            {
                float r = 0f;
                foreach (SpellEffect e in effects)
                {
                    SpellPawnEffect pe = e as SpellPawnEffect;
                    if (pe != null && pe.radius > 0f && pe.radius < 100f) r = Mathf.Max(r, pe.radius);
                    Effect_Creation ce = e as Effect_Creation;
                    if (ce != null && (ce.shape == CreationShape.Radial || ce.shape == CreationShape.Ring))
                        r = Mathf.Max(r, ce.radius);
                }
                return r;
            }
        }

        /// <summary>The line-shaped creation effect, if any (used for the drag preview).</summary>
        public Effect_Creation LineEffect
        {
            get
            {
                foreach (SpellEffect e in effects)
                {
                    Effect_Creation ce = e as Effect_Creation;
                    if (ce != null && ce.shape == CreationShape.Line) return ce;
                }
                return null;
            }
        }

        /// <summary>
        /// Summons, teleports and solid creations need an unoccupied target cell; an occupied
        /// cell is an invalid target rather than being nudged to a nearby one.
        /// </summary>
        public bool NeedsFreeCell
        {
            get
            {
                foreach (SpellEffect e in effects)
                {
                    if (e is Effect_Summon) return true;
                    Effect_Creation ce = e as Effect_Creation;
                    if (ce != null && ce.NeedsFreeStart) return true;
                }
                return false;
            }
        }

        /// <summary>Resurrection spells only work on corpses that have not started to rot.</summary>
        public bool CorpseMustBeFresh
        {
            get
            {
                foreach (SpellEffect e in effects)
                {
                    Effect_Heal heal = e as Effect_Heal;
                    if (heal != null && heal.resurrect && heal.requireFreshCorpse) return true;
                }
                return false;
            }
        }

        public string MenuLabel
        {
            get
            {
                string kinds = string.Join(", ", EffectKinds.Select(k => k.ToString().ToLowerInvariant()).ToArray());
                return LabelCap.ToString() + "  (T" + tier + " " + castType.ToString().ToLowerInvariant() + ": " + kinds + ")";
            }
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
                yield return e;
            if (castType == CastType.Projectile && projectileDef == null)
                yield return "projectile spell has no projectileDef";
            if (effects == null || effects.Count == 0)
                yield return "spell has no effects";
        }
    }

    /// <summary>Future hook: karma, class level, items, etc. For the test, spells list none.</summary>
    public abstract class SpellRequirement
    {
        public abstract bool IsSatisfied(Pawn caster, out string reason);
    }
}
