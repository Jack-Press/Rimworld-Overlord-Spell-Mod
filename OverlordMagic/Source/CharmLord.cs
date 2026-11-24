using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace OverlordMagic
{
    /// <summary>
    /// AI for charmed pawns that the player cannot directly order (animals, mechanoids):
    /// fight enemies, otherwise stay near the pawn that charmed them.
    /// </summary>
    public class LordJob_CharmedFight : LordJob
    {
        public Pawn leader;

        public LordJob_CharmedFight() { }

        public LordJob_CharmedFight(Pawn leader)
        {
            this.leader = leader;
        }

        public override StateGraph CreateGraph()
        {
            StateGraph graph = new StateGraph();
            LordToil toil = new LordToil_CharmedFight();
            graph.AddToil(toil);
            graph.StartingToil = toil;
            return graph;
        }

        public override void ExposeData()
        {
            Scribe_References.Look(ref leader, "leader");
        }
    }

    public class LordToil_CharmedFight : LordToil
    {
        public override void UpdateAllDuties()
        {
            LordJob_CharmedFight job = lord.LordJob as LordJob_CharmedFight;
            DutyDef def = DefDatabase<DutyDef>.GetNamed("OM_CharmedFight");
            Pawn leader = job != null ? job.leader : null;

            for (int i = 0; i < lord.ownedPawns.Count; i++)
            {
                Pawn p = lord.ownedPawns[i];
                p.mindState.duty = (leader != null && !leader.Dead)
                    ? new PawnDuty(def, leader, 8f)
                    : new PawnDuty(def);
            }
        }
    }
}
