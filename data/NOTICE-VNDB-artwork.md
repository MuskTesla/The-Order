# VNDB artwork selection hints

`vndb-banner-hints.json` contains public VNDB work and screenshot identifiers (no images).
It is derived from PotatoVN's `GalgameManager/Assets/Data/ex-vndb.db`, pinned at commit
`39709ab04ccb73259377f45e654dcabdd66be765`:
https://github.com/GoldenPotato137/PotatoVN/blob/39709ab04ccb73259377f45e654dcabdd66be765/GalgameManager/Assets/Data/ex-vndb.db

PotatoVN is Copyright GoldenPotato137 and contributors, licensed under Apache-2.0:
https://github.com/GoldenPotato137/PotatoVN/blob/39709ab04ccb73259377f45e654dcabdd66be765/LICENSE

This is an independently implemented artwork selector. It does not copy PotatoVN code.
Hints are not identity evidence or an NSFW verdict: each identifier must still appear
in the exact work's current public VNDB API response with permitted flags, and then
pass the local dialogue/UI text detector. Artwork rights remain with their owners.
VNDB API terms and data license: https://api.vndb.org/kana and https://vndb.org/d17.

Steam library hero and portrait assets are obtained only for a unique Steam game app ID
already associated with the same catalog work. When the work also lists a demo,
the official Steam response must confirm that demo's exact parent game ID;
unrelated games, DLC and ambiguous associations are rejected. The strategy was researched from
PotatoVN's SteamParser.cs at the same pinned commit and Steamworks library asset
documentation: https://partner.steamgames.com/doc/store/assets/libraryassets.
These images are not distributed as application source or covered by its license.
