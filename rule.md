Project Rules & Guidelines
1. Mandatory Git Commit Policy
Critical: Every time code modifications, feature improvements, or bug fixes are completed, you must perform a git commit immediately.

Never leave successfully working code in an uncommitted state.

Keep commit messages clear and organized to maintain a history of work at every step.

2. Deployment Policy
When compiling GameOverlay.sln -c Release successfully with 0 Errors/0 Warnings, run deploy.py to sync files to C:\Games\Hy-v Tool\DXPEOE\GameHelper2\.

3. Patch-Day Heuristic Offset Discovery (Fresh Offset Discovery on League Launch Day)
When the game updates to a new league and offsets or signatures break, use the Heuristic Memory Probing technique to find fresh positions directly from memory:

Step 1: Base Pattern Recovery (AOB Signature)
Scan for unchanging reference strings in the .exe file or module memory:

"Unable to get InGameState" ➔ Calculate the RIP-relative displacement to find the Game States base.

"Mods.dat" ➔ File Root base.

"Got Instance Details from login server" ➔ AreaChangeCounter.

Turn the assembly opcodes around the calling point into an AOB pattern and place it in GameOffsets/StaticOffsetsPatterns.cs.

Step 2: Heuristic AreaInstance & Player Chasing (Without Prior Offset Knowledge)
From InGameState, scan for all pointers within the range 0x000 - 0x500 (stepping by 8 bytes).

Within each valid pointer, inspect sub-pointers in the range 0x000 - 0x800 to see which one points to an entity whose Details->name starts with "Metadata/Characters/":

The matched sub-pointer ➔ LocalPlayerPtr (e.g., 0x5D0).

The location within InGameState storing this pointer ➔ AreaInstanceData (e.g., 0x290).

Step 3: Heuristic Vital Pools (HP / MP / ES Discovery)
Go to the player's "Life" component.

Scan for 4-byte integer pairs (Total, Current) meeting the condition 0 < Current <= Total <= 50000:

First pair found ➔ Health (HP) (e.g., 0x1DC / 0x1E0 in VitalStruct Health).

Next pair found ➔ Mana (MP) (e.g., 0x234).

Next pair found ➔ Energy Shield (ES) (e.g., 0x274).

Step 4: Area Level Discovery
Within AreaInstance, scan for a 1-byte variable (Byte) matching the level of the zone where the character is standing (1 - 100) ➔ Retrieves the offset for CurrentAreaLevel (e.g., 0x0BC).

4. Language Policy
Communication with User: Communicate with the user in Thai at all times.

Code: Write all code, variable names, method names, class names, and code comments in English.