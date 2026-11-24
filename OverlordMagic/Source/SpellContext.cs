using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace OverlordMagic
{
    /// <summary>
    /// Multipliers applied at cast time. Meta-magic (Maximize, Widen, Extend, ...) will
    /// later just change these numbers before the effects run.
    /// </summary>
    public class SpellModifiers
    {
        public float power = 1f;     // damage / healing amount
        public float area = 1f;      // radius
        public float duration = 1f;  // status / summon / creation lifetime
    }

    public class SpellContext
    {
        public SpellDef spell;
        public Pawn caster;
        public Map map;
        public LocalTargetInfo target;
        public SpellModifiers mods = new SpellModifiers();

        // For drag-to-aim line spells (Wall of Stone): where the drag ended.
        public bool hasLineEnd;
        public IntVec3 lineEnd;

        // Where the caster stood when the spell was cast. Caster-centered (Self) spells use this, so a spell
        // queued during Time Stop still goes off where the caster was, not where they have walked to since.
        public bool hasOrigin;
        public IntVec3 origin;

        public Thing TargetThing { get { return target.HasThing ? target.Thing : null; } }

        public IntVec3 Cell
        {
            get
            {
                if (hasOrigin && spell != null && spell.targetKind == TargetKind.Self) return origin;
                return target.HasThing ? target.Thing.PositionHeld : target.Cell;
            }
        }
    }

    public static class SpellUtility
    {
        /// <summary>
        /// True if something sizeable occupies the cell: a creature, building, item, tree, or any other solid thing.
        /// Small plants (grass, bushes, crops) do not count.
        /// </summary>
        public static bool IsOccupied(Map map, IntVec3 c)
        {
            List<Thing> things = c.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t is Pawn) return true;
                ThingCategory cat = t.def.category;
                if (cat == ThingCategory.Building || cat == ThingCategory.Item) return true;
                if (cat == ThingCategory.Plant)
                {
                    if (t.def.plant != null && t.def.plant.IsTree) return true;
                    continue;
                }
                if (t.def.passability == Traversability.Impassable) return true;
            }
            return false;
        }

        /// <summary>True if nothing occupies the cell and a creature could stand there.</summary>
        public static bool IsCellFree(Map map, IntVec3 c)
        {
            if (map == null || !c.InBounds(map) || c.Fogged(map) || !c.Standable(map)) return false;
            return !IsOccupied(map, c);
        }

        /// <summary>Nearest free cell around a point (used for the 2nd, 3rd... summon of a group).</summary>
        public static IntVec3 FindFreeCellNear(Map map, IntVec3 center, float maxRadius)
        {
            foreach (IntVec3 c in GenRadial.RadialCellsAround(center, maxRadius, true))
            {
                if (IsCellFree(map, c)) return c;
            }
            return IntVec3.Invalid;
        }

        public static void Flash(Map map, IntVec3 cell, float scale)
        {
            if (map == null || !cell.InBounds(map)) return;
            FleckMaker.Static(cell, map, FleckDefOf.ExplosionFlash, Mathf.Max(1f, scale));
        }
    }
}
