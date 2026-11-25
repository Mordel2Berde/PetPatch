# PetPatch

Tired of your Lox's head poking through the palisade of its pen? Of your wolf's head vanishing
into a wall? Does your Viking pride suffer when a chick manages to push you around? Fed up with a
herd of boars blocking access to your doors? Then this mod is for you!

PetPatch only affects **tamed creatures**. Wild creatures and combat are left untouched.

## Features

### Bodies stay out of walls
A creature only collides with the world through a short capsule, much smaller than its actual
body: a Lox's muzzle sits 4.3 m from its center, its capsule radius is only 1.5 m. PetPatch
measures the real body of each tamed creature from its skeleton and keeps it clear of buildings
and obstacles. Doorways stay passable: the width never exceeds the vanilla collision capsule.

### Destinations that make sense
Vanilla picks a random roaming point without checking whether it can be reached, so pets end up
pressed against a wall or a closed door, often right in front of your entrance. Tamed creatures
now only roam to places:
- reached by a complete path,
- with enough room to turn around without their head or rump entering an obstacle,
- not blocked by a player or another pet standing right in front of them.

Walking along a wall is still allowed, only the destination matters.

### No more pushing
Walking creatures impose their velocity on whatever is in their way, whatever its mass, which is
how a chick can push a Viking. A tamed creature now stops when a player or another pet stands in
its way, and lets its next movement settle the conflict.

### A calmer herd
Instead of picking a new destination on a fixed schedule, a roaming pet now stays in place for a
random time (5 to 45 s by default) after arriving or being interrupted.

### Who is it following?
The name of the followed player is shown after the name of a tamed creature, for example
`Wolf -> Ragnar`. Every player sees it, whoever owns the creature.

## Configuration

File: `BepInEx/config/Mordel2Berde.PetPatch.cfg`, or any in-game config manager.

| Section | Setting | Default | Description |
|---|---|---|---|
| General | Enabled | true | Turns every movement feature on or off, live. |
| General | BodyMargin | 0.15 | Clearance added around the skeleton, in meters. |
| General | ClearanceFactor | 1.1 | Multiplies the distance kept from obstacles. |
| General | PushSpeed | 1.5 | Speed at which a clipping creature is moved out of an obstacle, in m/s. |
| General | KeepTurningRoom | true | Only roam to reachable places with room to turn around. |
| General | IdleMinSeconds | 5 | Minimum idle time after arriving or being interrupted. |
| General | IdleMaxSeconds | 45 | Maximum idle time after arriving or being interrupted. |
| Traffic | StopForPlayers | true | Stop instead of pushing a player. |
| Traffic | StopForPets | true | Stop instead of pushing another tamed creature. |
| Display | ShowFollowedPlayer | true | Show the followed player's name after the creature's name. |
| Debug | LogBodySizes | false | Log the body size measured for each tamed creature. |

## Multiplayer

No server installation required. A creature is simulated by the player who owns it (usually the
closest one), so for consistent behavior every player should install the mod. Players without it
simply see vanilla behavior for the creatures they own.

## Known limitations

- A pet following you waits as long as another pet stands between you and it: move and it follows.
- Pathfinding ignores creatures, so a pet completely surrounded by others stays in place until
  one of them moves.
