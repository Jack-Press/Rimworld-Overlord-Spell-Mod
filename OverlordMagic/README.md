# Overlord Magic (Test Build) - RimWorld 1.6

Player-controlled colonists get a **Cast spell** button (select a colonist, look in the gizmo bar). It opens a
menu of 13 sample spells. No mana cost, no requirements, no DLC. **Requires the Harmony mod** (brrainz.harmony).

## Build
1. Install the .NET SDK (or Visual Studio) and subscribe to / install the Harmony mod.
2. `cd Source` then
   `dotnet build -c Release /p:RimWorldDir="C:\Program Files (x86)\Steam\steamapps\common\RimWorld"`
   (first build downloads the Harmony reference package, so it needs internet).
3. The DLL lands in `1.6/Assemblies/OverlordMagic.dll`.
4. Put the whole `OverlordMagic` folder in `RimWorld/Mods/`, enable Harmony then this mod (Harmony first), restart.

The code has not been compiled or run by me. If the build or the game complains, send me the exact message.

## How casting works
1. Pick a spell, then a target. While aiming you see the caster's range ring and, for area spells, the blast
   radius under the cursor. Invalid targets show the blocked cursor.
2. The caster performs an **incantation** (30 + 15 x tier ticks; Magic Arrow ~0.75 s, Time Stop 3 s). It is a job,
   so it only advances while time runs, and giving the pawn any other order cancels it: the spell does not go off.
   You can choose spells while the game is paused; the incantation starts when you unpause.
3. When the incantation finishes the spell is validated again and goes off.

## Spell notes
- **Shock Wave**: area burst (radius 2.5) at the clicked point; spares the caster and allies.
- **Heal**: closes ordinary injuries up to a limit (30 injury-severity), no scars, no limb regrowth. (Regrowth is
  still available to future spells through `regrowParts`.)
- **Raise Dead**: only fresh corpses (not yet rotting) are targetable. A resurrection may only fix what decides whether
  the pawn lives or for how long; everything else stays exactly as it was (a missing foot, scars, bruises, chronic
  conditions, implants). It is limited by the spell's level (`reviveRestoreLevel`: 0 restores only life-critical parts,
  1 adds large limbs, 2 everything). Raise Dead is level 0: a pawn missing head and foot gets the head back but not the foot.
  Every revived pawn gets severe blood loss (`reviveBloodLoss`) and is unconscious for a while
  (`reviveUnconsciousTicks`). Bleeding wounds are applied only to parts that were brought back (neck, head) and parts that
  were already bleeding, tuned from the game's bleed rate so they bleed out in about 6 hours (`reviveHoursToDeath`; 0 = no
  fragility). If there are no such parts the pawn has the blood loss but no bleeding wound. In dev mode the log prints the
  bleed rate so you can check the tuning.
- **Summons / Teleportation**: the target tile must be empty (no pawn, building or item, not fogged); an occupied
  tile is an invalid target and nothing is spawned nearby instead.
- **Wall of Stone**: click where it starts, keep the button held and drag to choose direction and length (up to 7),
  release to confirm (right click / Esc cancels). A plain click makes a 5-tile wall across your line of sight. The
  wall stops at the first obstacle in either case.
- **Charm Person**: the target leaves its old group (so its entourage no longer commands it) and joins your faction.
  Animals and mechanoids get an AI that follows the caster and fights enemies; humanlike targets become ordinary
  controllable colonists for the duration, and start out drafted. Charm, Haste, Paralysis and Time Stop are removed when the pawn dies (a charmed pawn's corpse returns to its old
  faction). Damage from your side breaks the charm and returns the target to its old
  faction and group.
- **Time Stop**: for 600 ticks every pawn on the map except the caster does not tick at all (no moving, acting,
  bleeding or aging), projectiles and fire stand still, and frozen colonists have no buttons and refuse orders (with a message).
  The caster can walk and cast, but other orders are refused, and spells cast during the stop are queued and go off
  when time resumes. Frozen pawns show a "frozen in time" hediff.

## Layout
- `Common/` Defs, Patches, Textures (version independent). `1.6/Assemblies/` the DLL. `LoadFolders.xml` picks them.
- `Source/`: `SpellDef.cs` (definition), `Effects.cs` (damage / heal / summon / creation / status),
  `SpellCaster.cs` (pipeline, incantation job, projectile, targeting overlays), `TimeStop.cs`,
  `CompSpellCaster.cs` (button + MP hooks), `Hediffs.cs` + `CharmLord.cs` (charm, paralysis, time-stop marker),
  `Patches.cs` (Harmony).

## Future hooks
MP: `CompSpellCaster.InfiniteMana`, `HasMana`, `ConsumeMana`. Requirements: subclasses of `SpellRequirement` listed
under `<requirements>`. Meta-magic: `SpellModifiers` (power / area / duration). Cooldowns: `cooldownTicks` + `CanCast`.
Incantation length per spell: `<incantationTicks>`.

## Known limits
- Only the `Human` race gets the casting component.
- During Time Stop the caster's autonomous work jobs are not blocked (only ordered ones), and damage the caster deals
  by hand lands immediately.
- Charm on a humanlike target that is a prisoner or slave is untested.
- Targeting range rings are capped at 56 tiles.

## Most likely to need a tweak if the build or game complains
- Harmony patch targets (`Patches.cs`): each patch class is applied separately and failures are logged as
  "[OverlordMagic] Could not apply ...". `Pawn.Tick`/`TickInterval`, `Pawn_JobTracker.TryTakeOrderedJob`,
  `Pawn.PostApplyDamage`.
- `TargetingParameters.validator` type (`CompSpellCaster.cs`), `Projectile.Impact/Launch` (`SpellCaster.cs`).
- `LordJob_AssaultColony` constructor and the `JobGiver_AIFightEnemies` / `JobGiver_AIFollowEscortee` names
  (`Hediffs.cs`, `Jobs_And_Duties.xml`).
