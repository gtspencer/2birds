Currently, players can pickup other players and throw them.  They can also pickup boulders using a two handed IK method (items marked as heavy in their ItemDefinition).

When a player picks up another player, their currently held item is retained (i.e. it doesn't do anything to the held item).
$gri
I want to author pickup spots on other avatars and implement 2 hand IK spots.  This should act exactly like the heavy item holding (like the current Boulder item).  The only difference is we don't need to move the held player during throw charging.

I also want to return any held items into a players inventory (i.e. we essentially deselect any hot bar item so the hands can now hold the player).