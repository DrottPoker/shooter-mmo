#nullable disable
using System;
using System.Collections.Generic;

namespace Mmorpg.SharedTests
{
    public static partial class RealtimeProtocolGolden
    {
        // Payload bytes after the seven-byte header, pinned from protocol version 19.
        private static readonly Dictionary<string, string> PayloadVectors = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Combat/CombatEventBatch/full"] = "sha256:965:d57dbf86bcfa60cb62322a23e3cfa6fd1b0e9e60ac337a2ce37233bf924110ec",
            ["Combat/CombatEventBatch/release hit death"] =
                "04 03 02 01 03 01 38 37 36 35 34 33 32 31 48 47 " +
                "46 45 44 43 42 41 10 03 02 01 00 00 00 00 01 00 " +
                "00 00 00 02 38 37 36 35 34 33 32 31 48 47 46 45 " +
                "44 43 42 41 00 00 00 00 11 00 00 00 02 21 00 00 " +
                "00 03 38 37 36 35 34 33 32 31 48 47 46 45 44 43 " +
                "42 41 00 00 00 00 00 00 00 00 01 00 00 00 00",
            ["Combat/CombatIntent/attack target"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "01 48 47 46 45 44 43 42 41",
            ["Combat/CombatIntent/clear target"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "02",
            ["Combat/CombatIntentResult/accepted"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "01 01 48 47 46 45 44 43 42 41",
            ["Combat/CombatIntentResult/rejected"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "01 00 00 00 00 00 00 00 00 00 12 00 63 6f 6d 62 " +
                "61 74 5f 74 61 72 67 65 74 5f 64 65 61 64 1a 00 " +
                "4d c3 a5 6c 65 74 20 c3 a4 72 20 72 65 64 61 6e " +
                "20 64 c3 b6 74 74 20 e2 9c 93",
            ["Combat/CombatantState/alive"] =
                "38 37 36 35 34 33 32 31 07 00 00 00 00 00 00 00 " +
                "25 00 00 00 32 00 00 00 01 01 48 47 46 45 44 43 " +
                "42 41",
            ["Combat/CombatantState/dead"] =
                "38 37 36 35 34 33 32 31 ff ff ff ff ff ff ff 7f " +
                "00 00 00 00 32 00 00 00 02 00 00 00 00 00 00 00 " +
                "00 00",
            ["Combat/OwnerCombatState/attacking"] =
                "0b 00 00 00 00 00 00 00 04 03 02 01 40 00 00 00 " +
                "64 00 00 00 01 02 00 00 00 00 00 00 00 01 48 47 " +
                "46 45 44 43 42 41 02 40 03 02 01 20 03 02 01 50 " +
                "51 52 53 54 55 56 57 58 59 5a 5b 5c 5d 5e 5f e5 " +
                "03 00 00",
            ["Combat/OwnerCombatState/dead"] =
                "0b 00 00 00 00 00 00 00 04 03 02 01 00 00 00 00 " +
                "64 00 00 00 02 ff ff ff ff 0d 0c 0b 0a 01 00 00 " +
                "00 00 00 00 00 00 0a 40 03 02 01 20 03 02 01 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00",
            ["Corpses/CorpseInteractionIntent/close"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "02 a0 a1 a2 a3 a4 a5 a6 a7 a8 a9 aa ab ac ad ae " +
                "af 03 00 00 00 00 00 00 00",
            ["Corpses/CorpseInteractionIntent/deposit item"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "07 a0 a1 a2 a3 a4 a5 a6 a7 a8 a9 aa ab ac ad ae " +
                "af 03 00 00 00 00 00 00 00 20 21 22 23 24 25 26 " +
                "27 28 29 2a 2b 2c 2d 2e 2f 06 00 00 00 00 00 00 " +
                "00 00 00 00 00 c0 c1 c2 c3 c4 c5 c6 c7 c8 c9 ca " +
                "cb cc cd ce cf 07 00 00 00 00 00 00 00 02 00 00 " +
                "00 00",
            ["Corpses/CorpseInteractionIntent/deposit partial stack onto a target"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "08 a0 a1 a2 a3 a4 a5 a6 a7 a8 a9 aa ab ac ad ae " +
                "af 03 00 00 00 00 00 00 00 20 21 22 23 24 25 26 " +
                "27 28 29 2a 2b 2c 2d 2e 2f 06 00 00 00 00 00 00 " +
                "00 02 00 00 00 c0 c1 c2 c3 c4 c5 c6 c7 c8 c9 ca " +
                "cb cc cd ce cf 07 00 00 00 00 00 00 00 01 00 00 " +
                "00 01 40 41 42 43 44 45 46 47 48 49 4a 4b 4c 4d " +
                "4e 4f 00 00 00 00 00 00 00 00",
            ["Corpses/CorpseInteractionIntent/loot item"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "04 a0 a1 a2 a3 a4 a5 a6 a7 a8 a9 aa ab ac ad ae " +
                "af 03 00 00 00 00 00 00 00 20 21 22 23 24 25 26 " +
                "27 28 29 2a 2b 2c 2d 2e 2f 06 00 00 00 00 00 00 " +
                "00 00 00 00 00 30 31 32 33 34 35 36 37 38 39 3a " +
                "3b 3c 3d 3e 3f 09 00 00 00 00 00 00 00 04 00 00 " +
                "00 00",
            ["Corpses/CorpseInteractionIntent/loot item onto a target"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "04 a0 a1 a2 a3 a4 a5 a6 a7 a8 a9 aa ab ac ad ae " +
                "af 03 00 00 00 00 00 00 00 20 21 22 23 24 25 26 " +
                "27 28 29 2a 2b 2c 2d 2e 2f 06 00 00 00 00 00 00 " +
                "00 00 00 00 00 30 31 32 33 34 35 36 37 38 39 3a " +
                "3b 3c 3d 3e 3f 09 00 00 00 00 00 00 00 04 00 00 " +
                "00 01 40 41 42 43 44 45 46 47 48 49 4a 4b 4c 4d " +
                "4e 4f 08 00 00 00 00 00 00 00",
            ["Corpses/CorpseInteractionIntent/loot partial stack"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "05 a0 a1 a2 a3 a4 a5 a6 a7 a8 a9 aa ab ac ad ae " +
                "af 03 00 00 00 00 00 00 00 20 21 22 23 24 25 26 " +
                "27 28 29 2a 2b 2c 2d 2e 2f 06 00 00 00 00 00 00 " +
                "00 05 00 00 00 30 31 32 33 34 35 36 37 38 39 3a " +
                "3b 3c 3d 3e 3f 09 00 00 00 00 00 00 00 00 00 00 " +
                "00 00",
            ["Corpses/CorpseInteractionIntent/loot to equipment"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "0b a0 a1 a2 a3 a4 a5 a6 a7 a8 a9 aa ab ac ad ae " +
                "af 03 00 00 00 00 00 00 00 20 21 22 23 24 25 26 " +
                "27 28 29 2a 2b 2c 2d 2e 2f 06 00 00 00 00 00 00 " +
                "00 09 00 6d 61 69 6e 5f 68 61 6e 64 00",
            ["Corpses/CorpseInteractionIntent/loot to equipment with swap"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "0b a0 a1 a2 a3 a4 a5 a6 a7 a8 a9 aa ab ac ad ae " +
                "af 03 00 00 00 00 00 00 00 20 21 22 23 24 25 26 " +
                "27 28 29 2a 2b 2c 2d 2e 2f 06 00 00 00 00 00 00 " +
                "00 09 00 6d 61 69 6e 5f 68 61 6e 64 01 40 41 42 " +
                "43 44 45 46 47 48 49 4a 4b 4c 4d 4e 4f 08 00 00 " +
                "00 00 00 00 00",
            ["Corpses/CorpseInteractionIntent/move item"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "09 a0 a1 a2 a3 a4 a5 a6 a7 a8 a9 aa ab ac ad ae " +
                "af ff ff ff ff ff ff ff 7f 20 21 22 23 24 25 26 " +
                "27 28 29 2a 2b 2c 2d 2e 2f 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 c0 c1 c2 c3 c4 c5 c6 c7 c8 c9 ca " +
                "cb cc cd ce cf 00 00 00 00 00 00 00 00 ff ff ff " +
                "7f 00",
            ["Corpses/CorpseInteractionIntent/move partial stack"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "0a a0 a1 a2 a3 a4 a5 a6 a7 a8 a9 aa ab ac ad ae " +
                "af 00 00 00 00 00 00 00 00 20 21 22 23 24 25 26 " +
                "27 28 29 2a 2b 2c 2d 2e 2f ff ff ff ff ff ff ff " +
                "7f ff ff ff 7f c0 c1 c2 c3 c4 c5 c6 c7 c8 c9 ca " +
                "cb cc cd ce cf ff ff ff ff ff ff ff 7f 00 00 00 " +
                "00 01 40 41 42 43 44 45 46 47 48 49 4a 4b 4c 4d " +
                "4e 4f ff ff ff ff ff ff ff 7f",
            ["Corpses/CorpseInteractionIntent/open"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "01 a0 a1 a2 a3 a4 a5 a6 a7 a8 a9 aa ab ac ad ae " +
                "af ff ff ff ff ff ff ff ff",
            ["Corpses/CorpseInteractionIntent/refresh"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "03 a0 a1 a2 a3 a4 a5 a6 a7 a8 a9 aa ab ac ad ae " +
                "af 00 00 00 00 00 00 00 00",
            ["Corpses/CorpseInteractionIntent/swap bag"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "06 a0 a1 a2 a3 a4 a5 a6 a7 a8 a9 aa ab ac ad ae " +
                "af 03 00 00 00 00 00 00 00 d0 d1 d2 d3 d4 d5 d6 " +
                "d7 d8 d9 da db dc dd de df 06 00 00 00 00 00 00 " +
                "00 e0 e1 e2 e3 e4 e5 e6 e7 e8 e9 ea eb ec ed ee " +
                "ef 07 00 00 00 00 00 00 00 40 41 42 43 44 45 46 " +
                "47 48 49 4a 4b 4c 4d 4e 4f 08 00 00 00 00 00 00 " +
                "00 f0 f1 f2 f3 f4 f5 f6 f7 f8 f9 fa fb fc fd fe " +
                "ff 09 00 00 00 00 00 00 00",
            ["Corpses/CorpseInteractionResult/failure"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "06 a0 a1 a2 a3 a4 a5 a6 a7 a8 a9 aa ab ac ad ae " +
                "af 00 01 00 0c 00 00 00 00 00 00 00 d2 00 00 00 " +
                "00 00 00 00 c8 00 00 00 00 00 00 00 18 00 63 6f " +
                "72 70 73 65 5f 72 65 76 69 73 69 6f 6e 5f 63 6f " +
                "6e 66 6c 69 63 74 13 00 54 68 65 20 63 6f 72 70 " +
                "73 65 20 63 68 61 6e 67 65 64 2e",
            ["Corpses/CorpseInteractionResult/success"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "04 a0 a1 a2 a3 a4 a5 a6 a7 a8 a9 aa ab ac ad ae " +
                "af 01 00 01 0c 00 00 00 00 00 00 00 d2 00 00 00 " +
                "00 00 00 00 c8 00 00 00 00 00 00 00",
            ["Corpses/CorpsePresenceSnapshotChunk/corpse with a look"] =
                "d4 c3 b2 a1 00 00 01 00 01 a0 a1 a2 a3 a4 a5 a6 " +
                "a7 a8 a9 aa ab ac ad ae af 00 00 b0 40 00 00 00 " +
                "00 00 00 e8 c0 00 00 f7 42 00 c0 2c c8 99 01 00 " +
                "00 00 0b 00 47 72 c3 b6 6e 20 c3 96 64 6c 61 0d " +
                "00 63 6f 72 70 73 65 2e 70 6c 61 79 65 72 01 0c " +
                "00 48 75 6d 61 6e 5f 46 65 6d 61 6c 65 02 04 00 " +
                "66 61 63 65 00 08 00 46 72 65 63 6b 6c 65 73 09 " +
                "00 75 6e 64 65 72 77 65 61 72 01 04 00 6c 65 67 " +
                "73 06 00 42 72 69 65 66 73 02 04 00 68 65 61 64 " +
                "17 00 63 6c 6f 74 68 69 6e 67 2e 62 6c 75 65 5f " +
                "77 6f 6f 6c 5f 68 6f 6f 64 04 00 62 61 63 6b 13 " +
                "00 63 6c 6f 74 68 69 6e 67 2e 77 6f 6f 6c 5f 63 " +
                "6c 6f 61 6b 01 01 14 00 74 6f 6f 6c 2e 73 74 61 " +
                "72 74 65 72 5f 70 69 63 6b 61 78 65",
            ["Corpses/CorpsePresenceSnapshotChunk/empty"] =
                "01 00 00 00 00 00 01 00 00",
            ["Corpses/CorpsePresenceSnapshotChunk/one corpse"] =
                "d4 c3 b2 a1 00 00 01 00 01 a0 a1 a2 a3 a4 a5 a6 " +
                "a7 a8 a9 aa ab ac ad ae af 00 00 b0 40 00 00 00 " +
                "00 00 00 e8 c0 00 00 f7 42 00 c0 2c c8 99 01 00 " +
                "00 00 0b 00 47 72 c3 b6 6e 20 c3 96 64 6c 61 0d " +
                "00 63 6f 72 70 73 65 2e 70 6c 61 79 65 72 00",
            ["Corpses/CorpsePresenceSnapshotChunk/six corpses"] = "sha256:429:8a9637563c36d63e9175b016e4d44a3d64c672e21f92a08b6f76a7d7dbde987e",
            ["Corpses/CorpseViewClosed/typical"] =
                "a0 a1 a2 a3 a4 a5 a6 a7 a8 a9 aa ab ac ad ae af " +
                "0e 00 63 6f 72 70 73 65 5f 65 78 70 69 72 65 64 " +
                "13 00 54 68 65 20 63 6f 72 70 73 65 20 64 65 63 " +
                "61 79 65 64 2e",
            ["Corpses/CorpseViewStateChunk/bag delta"] =
                "b0 b1 b2 b3 b4 b5 b6 b7 b8 b9 ba bb bc bd be bf " +
                "02 a0 a1 a2 a3 a4 a5 a6 a7 a8 a9 aa ab ac ad ae " +
                "af 03 00 00 00 00 00 00 00 ff ff ff ff ff ff ff " +
                "7f 00 c0 2c c8 99 01 00 00 0b 00 47 72 c3 b6 6e " +
                "20 c3 96 64 6c 61 0d 00 63 6f 72 70 73 65 2e 70 " +
                "6c 61 79 65 72 01 00 02 00 03 00 62 61 67 c0 c1 " +
                "c2 c3 c4 c5 c6 c7 c8 c9 ca cb cc cd ce cf 06 00 " +
                "00 00 00 00 00 00 00 00 02 00 00 fc 01 00 00 01 " +
                "ff 01 00 00 03 00 62 61 67 00 00 01 20 21 22 23 " +
                "24 25 26 27 28 29 2a 2b 2c 2d 2e 2f 0c 00 69 74 " +
                "65 6d 2e 62 61 6e 64 61 67 65 ff ff ff 7f 06 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00",
            ["Corpses/CorpseViewStateChunk/equipment snapshot"] = "sha256:269:745e237495329398e618fe83e0ae5cc759755f4de4f958f7a39f1f7b0efe1adb",
            ["Corpses/CorpseViewStateChunk/general snapshot"] = "sha256:369:6d7d6268deac4124f9dad32a4f76b2447c7b537173f0e004e90f60512dea1d30",
            ["Items/CarryStateChanged/empty"] =
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "01 00 00 00 00 00 00 00",
            ["Items/CarryStateChanged/maximum"] =
                "ff ff ff ff ff ff ff 7f ff ff ff ff ff ff ff 7f " +
                "ff ff ff ff ff ff ff 7f",
            ["Items/CarryStateChanged/overflow"] =
                "0c 00 00 00 00 00 00 00 d2 00 00 00 00 00 00 00 " +
                "c8 00 00 00 00 00 00 00",
            ["Items/ItemOperationIntent/claim 24 recovery items"] = "sha256:642:f8c3823a3427d747dc1aaebac059c90eeacb1e8e74aaff1987e2d255bb6673f9",
            ["Items/ItemOperationIntent/claim one recovery item"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "07 05 00 00 00 00 00 00 00 50 51 52 53 54 55 56 " +
                "57 58 59 5a 5b 5c 5d 5e 5f 08 00 00 00 00 00 00 " +
                "00 30 31 32 33 34 35 36 37 38 39 3a 3b 3c 3d 3e " +
                "3f 01 20 21 22 23 24 25 26 27 28 29 2a 2b 2c 2d " +
                "2e 2f 06 00 00 00 00 00 00 00",
            ["Items/ItemOperationIntent/destroy"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "06 00 00 00 00 00 00 00 00 20 21 22 23 24 25 26 " +
                "27 28 29 2a 2b 2c 2d 2e 2f ff ff ff ff ff ff ff " +
                "7f",
            ["Items/ItemOperationIntent/equip"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "02 05 00 00 00 00 00 00 00 20 21 22 23 24 25 26 " +
                "27 28 29 2a 2b 2c 2d 2e 2f 06 00 00 00 00 00 00 " +
                "00 09 00 6d 61 69 6e 5f 68 61 6e 64",
            ["Items/ItemOperationIntent/merge stacks"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "05 05 00 00 00 00 00 00 00 20 21 22 23 24 25 26 " +
                "27 28 29 2a 2b 2c 2d 2e 2f 06 00 00 00 00 00 00 " +
                "00 40 41 42 43 44 45 46 47 48 49 4a 4b 4c 4d 4e " +
                "4f 00 00 00 00 00 00 00 00",
            ["Items/ItemOperationIntent/relocate to any slot"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "01 05 00 00 00 00 00 00 00 20 21 22 23 24 25 26 " +
                "27 28 29 2a 2b 2c 2d 2e 2f 06 00 00 00 00 00 00 " +
                "00 30 31 32 33 34 35 36 37 38 39 3a 3b 3c 3d 3e " +
                "3f ff ff ff ff",
            ["Items/ItemOperationIntent/relocate to slot"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "01 05 00 00 00 00 00 00 00 20 21 22 23 24 25 26 " +
                "27 28 29 2a 2b 2c 2d 2e 2f 06 00 00 00 00 00 00 " +
                "00 30 31 32 33 34 35 36 37 38 39 3a 3b 3c 3d 3e " +
                "3f 07 00 00 00",
            ["Items/ItemOperationIntent/split stack"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "04 05 00 00 00 00 00 00 00 20 21 22 23 24 25 26 " +
                "27 28 29 2a 2b 2c 2d 2e 2f 06 00 00 00 00 00 00 " +
                "00 03 00 00 00 30 31 32 33 34 35 36 37 38 39 3a " +
                "3b 3c 3d 3e 3f 00 00 00 00",
            ["Items/ItemOperationIntent/swap container items"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "08 05 00 00 00 00 00 00 00 20 21 22 23 24 25 26 " +
                "27 28 29 2a 2b 2c 2d 2e 2f 06 00 00 00 00 00 00 " +
                "00 40 41 42 43 44 45 46 47 48 49 4a 4b 4c 4d 4e " +
                "4f 07 00 00 00 00 00 00 00",
            ["Items/ItemOperationIntent/unequip"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "03 05 00 00 00 00 00 00 00 20 21 22 23 24 25 26 " +
                "27 28 29 2a 2b 2c 2d 2e 2f 06 00 00 00 00 00 00 " +
                "00 30 31 32 33 34 35 36 37 38 39 3a 3b 3c 3d 3e " +
                "3f ff ff ff 7f",
            ["Items/ItemOperationResult/failure"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "02 00 01 0c 00 00 00 00 00 00 00 d2 00 00 00 00 " +
                "00 00 00 c8 00 00 00 00 00 00 00 16 00 69 74 65 " +
                "6d 5f 72 65 76 69 73 69 6f 6e 5f 63 6f 6e 66 6c " +
                "69 63 74 26 00 54 68 65 20 69 74 65 6d 20 63 68 " +
                "61 6e 67 65 64 20 62 65 66 6f 72 65 20 74 68 65 " +
                "20 6f 70 65 72 61 74 69 6f 6e 2e 00 00 00",
            ["Items/ItemOperationResult/maximum lists"] = "sha256:1134:0e684ee9e7d25789561997d94ea9f522366afd647133f5ffd8149d9721fec555",
            ["Items/ItemOperationResult/success needing refresh"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "08 01 01 0c 00 00 00 00 00 00 00 d2 00 00 00 00 " +
                "00 00 00 c8 00 00 00 00 00 00 00 00 00 00",
            ["Items/ItemOperationResult/success with revisions"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "01 01 00 0c 00 00 00 00 00 00 00 d2 00 00 00 00 " +
                "00 00 00 c8 00 00 00 00 00 00 00 02 20 21 22 23 " +
                "24 25 26 27 28 29 2a 2b 2c 2d 2e 2f 07 00 00 00 " +
                "00 00 00 00 40 41 42 43 44 45 46 47 48 49 4a 4b " +
                "4c 4d 4e 4f 08 00 00 00 00 00 00 00 01 30 31 32 " +
                "33 34 35 36 37 38 39 3a 3b 3c 3d 3e 3f 09 00 00 " +
                "00 00 00 00 00 01 50 51 52 53 54 55 56 57 58 59 " +
                "5a 5b 5c 5d 5e 5f",
            ["Items/OwnerItemQuantityChanged/arrows spent"] =
                "01 0b 00 00 00 00 00 00 00 0c 00 00 00 00 00 00 " +
                "00 d2 00 00 00 00 00 00 00 c8 00 00 00 00 00 00 " +
                "00 50 51 52 53 54 55 56 57 58 59 5a 5b 5c 5d 5e " +
                "5f e3 03 00 00 29 00 00 00 00 00 00 00",
            ["Items/OwnerItemQuantityChanged/last arrows spent"] =
                "01 0b 00 00 00 00 00 00 00 0c 00 00 00 00 00 00 " +
                "00 d2 00 00 00 00 00 00 00 c8 00 00 00 00 00 00 " +
                "00 50 51 52 53 54 55 56 57 58 59 5a 5b 5c 5d 5e " +
                "5f 00 00 00 00 00 00 00 00 00 00 00 00",
            ["Items/OwnerItemStateChanged/equipped Bag and claimed delivery"] =
                "60 61 62 63 64 65 66 67 68 69 6a 6b 6c 6d 6e 6f " +
                "0b 00 00 00 00 00 00 00 0c 00 00 00 00 00 00 00 " +
                "d2 00 00 00 00 00 00 00 c8 00 00 00 00 00 00 00 " +
                "fa 00 00 00 00 00 00 00 01 30 31 32 33 34 35 36 " +
                "37 38 39 3a 3b 3c 3d 3e 3f 09 00 00 00 00 00 00 " +
                "00 01 40 41 42 43 44 45 46 47 48 49 4a 4b 4c 4d " +
                "4e 4f 03 01 04 00 70 61 63 6b 01 00 00 00 03 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 ff ff ff ff 01 03 00 62 61 67 " +
                "00 70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e " +
                "7f 05 00 00 00 00 00 00 00 01 80 81 82 83 84 85 " +
                "86 87 88 89 8a 8b 8c 8d 8e 8f",
            ["Items/OwnerItemStateChanged/merge removed the source"] =
                "60 61 62 63 64 65 66 67 68 69 6a 6b 6c 6d 6e 6f " +
                "0b 00 00 00 00 00 00 00 0c 00 00 00 00 00 00 00 " +
                "d2 00 00 00 00 00 00 00 c8 00 00 00 00 00 00 00 " +
                "fa 00 00 00 00 00 00 00 01 30 31 32 33 34 35 36 " +
                "37 38 39 3a 3b 3c 3d 3e 3f 09 00 00 00 00 00 00 " +
                "00 02 20 21 22 23 24 25 26 27 28 29 2a 2b 2c 2d " +
                "2e 2f 01 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "ff ff ff ff 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 40 41 " +
                "42 43 44 45 46 47 48 49 4a 4b 4c 4d 4e 4f 02 01 " +
                "06 00 70 6f 74 69 6f 6e 1e 00 00 00 07 00 00 00 " +
                "00 00 00 00 30 31 32 33 34 35 36 37 38 39 3a 3b " +
                "3c 3d 3e 3f 04 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00",
            ["Items/OwnerItemStateChanged/no item changed"] =
                "60 61 62 63 64 65 66 67 68 69 6a 6b 6c 6d 6e 6f " +
                "0b 00 00 00 00 00 00 00 0c 00 00 00 00 00 00 00 " +
                "d2 00 00 00 00 00 00 00 c8 00 00 00 00 00 00 00 " +
                "fa 00 00 00 00 00 00 00 00 00 00",
            ["Items/OwnerItemStateChanged/swap with an equipped item"] = "sha256:293:d99661e59782fa7bc6e27b0b66552bcd5cc726453326549032fb3a6f46f0c61b",
            ["Movement/MovementInputBatch/four"] =
                "04 01 00 00 00 e9 03 00 00 00 00 00 00 ff ff ff " +
                "ff 00 00 00 00 00 00 00 00 03 00 00 00 04 03 02 " +
                "01 ff ff ff ff 04 00 00 00 4d 00 00 00 0d 0c 0b " +
                "0a 00",
            ["Movement/MovementInputBatch/full path"] = "sha256:403:d96e0c593041d850c674860cf81b857cb8d6b0629463642ef967e08f02f24273",
            ["Movement/MovementInputBatch/single"] =
                "01 01 00 00 00 e9 03 00 00 00 00 00 00 00",
            ["Movement/MovementInputBatch/with path"] =
                "02 01 00 00 00 e9 03 00 00 00 00 00 00 02 00 00 " +
                "00 ea 03 00 00 0d 0c 0b 0a 01 0d 0c 0b 0a 02 00 " +
                "00 48 41 00 00 00 80 00 00 50 c0 ff ff 7f 7f 01 " +
                "00 00 00 ff ff 7f ff",
            ["Movement/OwnerSimulationSnapshot/boundary"] =
                "ff ff ff ff 00 00 00 00 01 00 00 00 00 00 00 80 " +
                "01 00 00 00 ff ff 7f 7f ff ff 7f ff 01 00 00 80 " +
                "ee e3 6c 00 df ff b3 c3 00 ff ff ff ff 20 ff",
            ["Movement/OwnerSimulationSnapshot/typical"] =
                "44 33 22 11 88 77 66 55 cc bb aa 99 00 00 48 41 " +
                "00 00 50 c0 00 18 80 44 00 00 00 3f 00 00 1c c1 " +
                "00 00 08 40 00 40 87 43 01 0d 0c 0b 0a 03 02",
            ["Replication/AppearanceIntent/lower headwear"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "01",
            ["Replication/AppearanceIntent/raise headwear"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "02",
            ["Replication/AppearanceIntent/set development look"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "03 0c 00 48 75 6d 61 6e 5f 46 65 6d 61 6c 65 02 " +
                "04 00 66 61 63 65 00 08 00 46 72 65 63 6b 6c 65 " +
                "73 09 00 75 6e 64 65 72 77 65 61 72 01 04 00 6c " +
                "65 67 73 06 00 42 72 69 65 66 73",
            ["Replication/AppearanceIntentResult/accepted"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "01 01",
            ["Replication/AppearanceIntentResult/rejected"] =
                "10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f " +
                "03 00 19 00 64 65 76 65 6c 6f 70 6d 65 6e 74 5f " +
                "6c 6f 6f 6b 5f 64 69 73 61 62 6c 65 64 2e 00 4f " +
                "6e 6c 79 20 61 20 44 65 76 65 6c 6f 70 6d 65 6e " +
                "74 20 47 61 6d 65 53 65 72 76 69 63 65 20 63 68 " +
                "61 6e 67 65 73 20 61 20 6c 6f 6f 6b 2e",
            ["Replication/EntityAppearanceChanged/bare"] =
                "07 00 00 00 00 00 00 00 0a 00 48 75 6d 61 6e 5f " +
                "4d 61 6c 65 00 00 00 00",
            ["Replication/EntityAppearanceChanged/typical"] =
                "07 00 00 00 00 00 00 00 0c 00 48 75 6d 61 6e 5f " +
                "46 65 6d 61 6c 65 02 04 00 66 61 63 65 00 08 00 " +
                "46 72 65 63 6b 6c 65 73 09 00 75 6e 64 65 72 77 " +
                "65 61 72 01 04 00 6c 65 67 73 06 00 42 72 69 65 " +
                "66 73 02 04 00 68 65 61 64 17 00 63 6c 6f 74 68 " +
                "69 6e 67 2e 62 6c 75 65 5f 77 6f 6f 6c 5f 68 6f " +
                "6f 64 04 00 62 61 63 6b 13 00 63 6c 6f 74 68 69 " +
                "6e 67 2e 77 6f 6f 6c 5f 63 6c 6f 61 6b 01 01 14 " +
                "00 74 6f 6f 6c 2e 73 74 61 72 74 65 72 5f 70 69 " +
                "63 6b 61 78 65",
            ["Replication/EntityDespawn/typical"] =
                "07 00 00 00 00 00 00 00 0f 00 6f 75 74 5f 6f 66 " +
                "5f 69 6e 74 65 72 65 73 74",
            ["Replication/EntitySpawn/braced persistent id"] =
                "11 10 0f 0e 0d 0c 0b 0a 01 26 00 7b 38 43 33 44 " +
                "32 45 31 46 2d 30 42 34 41 2d 34 43 35 44 2d 39 " +
                "45 36 46 2d 41 31 42 32 43 33 44 34 45 35 46 36 " +
                "7d 0b 00 47 72 c3 b6 6e 20 c3 96 64 6c 61 0e 00 " +
                "70 6c 61 79 65 72 2d 64 65 66 61 75 6c 74 40 e2 " +
                "01 00 00 00 00 80 01 00 00 00 ff ff 7f 7f ff ff " +
                "7f ff 01 00 00 80 ee e3 6c 00 df ff b3 c3 00 0c " +
                "00 48 75 6d 61 6e 5f 46 65 6d 61 6c 65 02 04 00 " +
                "66 61 63 65 00 08 00 46 72 65 63 6b 6c 65 73 09 " +
                "00 75 6e 64 65 72 77 65 61 72 01 04 00 6c 65 67 " +
                "73 06 00 42 72 69 65 66 73 02 04 00 68 65 61 64 " +
                "17 00 63 6c 6f 74 68 69 6e 67 2e 62 6c 75 65 5f " +
                "77 6f 6f 6c 5f 68 6f 6f 64 04 00 62 61 63 6b 13 " +
                "00 63 6c 6f 74 68 69 6e 67 2e 77 6f 6f 6c 5f 63 " +
                "6c 6f 61 6b 01 01 14 00 74 6f 6f 6c 2e 73 74 61 " +
                "72 74 65 72 5f 70 69 63 6b 61 78 65",
            ["Replication/EntitySpawn/typical"] =
                "11 10 0f 0e 0d 0c 0b 0a 01 24 00 38 63 33 64 32 " +
                "65 31 66 2d 30 62 34 61 2d 34 63 35 64 2d 39 65 " +
                "36 66 2d 61 31 62 32 63 33 64 34 65 35 66 36 0b " +
                "00 47 72 c3 b6 6e 20 c3 96 64 6c 61 0e 00 70 6c " +
                "61 79 65 72 2d 64 65 66 61 75 6c 74 40 e2 01 00 " +
                "00 00 48 41 00 00 50 c0 00 18 80 44 00 00 00 3f " +
                "00 00 1c c1 00 00 08 40 00 40 87 43 01 0c 00 48 " +
                "75 6d 61 6e 5f 46 65 6d 61 6c 65 02 04 00 66 61 " +
                "63 65 00 08 00 46 72 65 63 6b 6c 65 73 09 00 75 " +
                "6e 64 65 72 77 65 61 72 01 04 00 6c 65 67 73 06 " +
                "00 42 72 69 65 66 73 02 04 00 68 65 61 64 17 00 " +
                "63 6c 6f 74 68 69 6e 67 2e 62 6c 75 65 5f 77 6f " +
                "6f 6c 5f 68 6f 6f 64 04 00 62 61 63 6b 13 00 63 " +
                "6c 6f 74 68 69 6e 67 2e 77 6f 6f 6c 5f 63 6c 6f " +
                "61 6b 01 01 14 00 74 6f 6f 6c 2e 73 74 61 72 74 " +
                "65 72 5f 70 69 63 6b 61 78 65",
            ["Replication/SimulationSnapshot/full chunk"] = "sha256:997:463fa3b27bbfcb34175c3d0642e78be59ea7530fc188e8827bf694e91a384b4d",
            ["Replication/SimulationSnapshot/single entity"] =
                "04 03 02 01 08 07 06 05 00 00 01 00 01 2a 00 00 " +
                "00 00 00 00 00 33 00 00 00 00 00 48 41 00 00 50 " +
                "c0 00 18 80 44 00 00 00 3f 00 00 1c c1 00 00 08 " +
                "40 00 40 87 43 01",
            ["Session/JoinAccepted/boundary"] = "sha256:607:773adc23ef8f616e6adf1c171805784310ce29db06b67d39820182429104111f",
            ["Session/JoinAccepted/typical"] = "sha256:333:a5ee8c9fd1d7558bcbd8de505961b74cc476ffd44c1495a01a94304700a92241",
            ["Session/JoinRejected/basic"] =
                "13 00 6a 6f 69 6e 5f 74 69 63 6b 65 74 5f 65 78 " +
                "70 69 72 65 64 18 00 54 68 65 20 6a 6f 69 6e 20 " +
                "74 69 63 6b 65 74 20 65 78 70 69 72 65 64 2e",
            ["Session/JoinRejected/exact-packet-limit"] = "sha256:1193:f154e71e8891959f109220d06bcf5477d321b09c9ab6985201cce2e1eede68bd",
            ["Session/JoinRequest/ascii"] =
                "11 00 74 69 63 6b 65 74 2e 76 31 2e 61 62 63 2d " +
                "31 32 33",
            ["Session/JoinRequest/maximum-length"] = "sha256:1026:04d5494bf4f12da9772b666b28c13e865f91e67779652313f06b542f1563be9f",
            ["Session/JoinRequest/multibyte"] =
                "17 00 62 69 6c 6a 65 74 74 2d c3 a5 c3 a4 c3 b6 " +
                "2d e2 9c 93 2d f0 9f 98 80",
            ["Session/LeaveAccepted/empty"] = "",
            ["Session/LeaveRejected/basic"] =
                "10 00 6c 65 61 76 65 5f 6e 6f 74 5f 6a 6f 69 6e " +
                "65 64 20 00 4e 6f 20 73 69 6d 75 6c 61 74 69 6f " +
                "6e 20 73 65 73 73 69 6f 6e 20 69 73 20 61 63 74 " +
                "69 76 65 2e",
            ["Session/LeaveRequest/basic"] =
                "24 00 36 66 30 64 31 63 39 61 2d 30 30 30 30 2d " +
                "34 30 30 30 2d 38 30 30 30 2d 30 30 30 30 30 30 " +
                "30 30 30 30 30 31",
            ["Session/ServerDisconnect/multibyte"] =
                "0f 00 73 65 72 76 65 72 5f 73 68 75 74 64 6f 77 " +
                "6e 23 00 53 65 72 76 65 72 6e 20 73 74 c3 a4 6e " +
                "67 73 20 66 c3 b6 72 20 75 6e 64 65 72 68 c3 a5 " +
                "6c 6c 20 e2 9c 93",
            ["WorldActors/GatheringState/started"] =
                "90 91 92 93 94 95 96 97 98 99 9a 9b 9c 9d 9e 9f " +
                "28 27 26 25 24 23 22 21 60 61 62 63 64 65 66 67 " +
                "68 69 6a 6b 6c 6d 6e 6f 01 b8 0b 00 00",
            ["WorldActors/GatheringState/stopped"] =
                "90 91 92 93 94 95 96 97 98 99 9a 9b 9c 9d 9e 9f " +
                "28 27 26 25 24 23 22 21 60 61 62 63 64 65 66 67 " +
                "68 69 6a 6b 6c 6d 6e 6f 03 16 00 72 65 73 6f 75 " +
                "72 63 65 5f 6e 6f 64 65 5f 64 65 70 6c 65 74 65 " +
                "64 12 00 c3 85 64 65 72 6e 20 c3 a4 72 20 74 6f " +
                "6d 20 e2 9c 93",
            ["WorldActors/GatheringState/yielded"] =
                "90 91 92 93 94 95 96 97 98 99 9a 9b 9c 9d 9e 9f " +
                "28 27 26 25 24 23 22 21 60 61 62 63 64 65 66 67 " +
                "68 69 6a 6b 6c 6d 6e 6f 02 c0 27 09 00 11 00 6d " +
                "61 74 65 72 69 61 6c 2e 69 72 6f 6e 5f 6f 72 65 " +
                "01 00 00 00 ff ff ff ff ff ff ff 7f",
            ["WorldActors/WorldActorDespawn/typical"] =
                "28 27 26 25 24 23 22 21 60 61 62 63 64 65 66 67 " +
                "68 69 6a 6b 6c 6d 6e 6f 09 00 64 65 73 70 61 77 " +
                "6e 65 64",
            ["WorldActors/WorldActorSpawn/mob"] =
                "28 27 26 25 24 23 22 21 60 61 62 63 64 65 66 67 " +
                "68 69 6a 6b 6c 6d 6e 6f 0e 00 6e 70 63 2e 62 6c " +
                "61 63 6b 73 6d 69 74 68 12 00 73 70 61 77 6e 2e " +
                "62 6c 61 63 6b 73 6d 69 74 68 2e 31 0b 00 53 6d " +
                "65 64 65 6e 20 c3 85 6b 65 02 0c 00 66 61 63 74 " +
                "69 6f 6e 2e 74 6f 77 6e 03 17 00 70 72 65 73 65 " +
                "6e 74 61 74 69 6f 6e 2e 62 6c 61 63 6b 73 6d 69 " +
                "74 68 ff ff ff ff ff ff ff 7f 04 00 00 00 00 00 " +
                "00 00 00 00 00 28 41 00 00 80 3e 00 00 a6 c1 00 " +
                "00 b4 42 03 00 00 28 41 00 00 80 3f 00 00 a6 c1 " +
                "cd cc 4c 3f 00 00 00 40 00 00 40 3f 05 00 59 6c " +
                "61 72 2e 00",
            ["WorldActors/WorldActorSpawn/npc"] =
                "28 27 26 25 24 23 22 21 60 61 62 63 64 65 66 67 " +
                "68 69 6a 6b 6c 6d 6e 6f 0e 00 6e 70 63 2e 62 6c " +
                "61 63 6b 73 6d 69 74 68 12 00 73 70 61 77 6e 2e " +
                "62 6c 61 63 6b 73 6d 69 74 68 2e 31 0b 00 53 6d " +
                "65 64 65 6e 20 c3 85 6b 65 01 0c 00 66 61 63 74 " +
                "69 6f 6e 2e 74 6f 77 6e 01 17 00 70 72 65 73 65 " +
                "6e 74 61 74 69 6f 6e 2e 62 6c 61 63 6b 73 6d 69 " +
                "74 68 03 00 00 00 00 00 00 00 04 00 00 00 00 00 " +
                "00 00 01 00 00 28 41 00 00 80 3e 00 00 a6 c1 00 " +
                "00 b4 42 01 00 00 28 41 00 00 80 3f 00 00 a6 c1 " +
                "cd cc 4c 3f 00 00 00 40 00 00 40 3f 0b 00 53 6f " +
                "74 69 67 20 73 6d 65 64 2e 02 0a 00 73 6d 69 74 " +
                "68 2e 74 61 6c 6b 01 07 00 54 61 6c 6b 2d 74 6f " +
                "0b 00 73 6d 69 74 68 2e 74 72 61 64 65 02 05 00 " +
                "54 72 61 64 65",
            ["WorldActors/WorldActorState/dormant"] =
                "01 00 00 00 00 00 00 00 ff ff ff ff ff ff ff 7f " +
                "01 00 00 00 00 00 00 00 00 00 00 00 80 ff ff 7f " +
                "7f 01 00 00 00 00 00 34 c3 02",
            ["WorldActors/WorldActorState/typical"] =
                "28 27 26 25 24 23 22 21 03 00 00 00 00 00 00 00 " +
                "04 00 00 00 00 00 00 00 01 00 00 28 41 00 00 80 " +
                "3e 00 00 a6 c1 00 00 b4 42 01",
            ["WorldActors/WorldInteractionClosed/typical"] =
                "90 91 92 93 94 95 96 97 98 99 9a 9b 9c 9d 9e 9f " +
                "02 28 27 26 25 24 23 22 21 60 61 62 63 64 65 66 " +
                "67 68 69 6a 6b 6c 6d 6e 6f 10 00 74 61 72 67 65 " +
                "74 5f 64 65 73 70 61 77 6e 65 64 13 00 54 68 65 " +
                "20 74 61 72 67 65 74 20 69 73 20 67 6f 6e 65 2e",
            ["WorldActors/WorldInteractionIntent/AbandonQuest"] =
                "70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e 7f " +
                "80 81 82 83 84 85 86 87 88 89 8a 8b 8c 8d 8e 8f " +
                "90 91 92 93 94 95 96 97 98 99 9a 9b 9c 9d 9e 9f " +
                "03 01 28 27 26 25 24 23 22 21 60 61 62 63 64 65 " +
                "66 67 68 69 6a 6b 6c 6d 6e 6f 04 00 00 00 00 00 " +
                "00 00 0b 00 71 75 65 73 74 2e 66 69 72 73 74 03 " +
                "05 00 00 00 00 00 00 00 04 ff ff ff ff ff ff ff " +
                "7f 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 ff ff ff ff 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00",
            ["WorldActors/WorldInteractionIntent/AcceptQuest"] =
                "70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e 7f " +
                "80 81 82 83 84 85 86 87 88 89 8a 8b 8c 8d 8e 8f " +
                "90 91 92 93 94 95 96 97 98 99 9a 9b 9c 9d 9e 9f " +
                "03 01 28 27 26 25 24 23 22 21 60 61 62 63 64 65 " +
                "66 67 68 69 6a 6b 6c 6d 6e 6f 04 00 00 00 00 00 " +
                "00 00 0b 00 71 75 65 73 74 2e 66 69 72 73 74 03 " +
                "05 00 00 00 00 00 00 00 03 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 30 31 32 33 34 35 36 " +
                "37 38 39 3a 3b 3c 3d 3e 3f 09 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00",
            ["WorldActors/WorldInteractionIntent/ApplyInsurance"] =
                "70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e 7f " +
                "80 81 82 83 84 85 86 87 88 89 8a 8b 8c 8d 8e 8f " +
                "90 91 92 93 94 95 96 97 98 99 9a 9b 9c 9d 9e 9f " +
                "03 01 28 27 26 25 24 23 22 21 60 61 62 63 64 65 " +
                "66 67 68 69 6a 6b 6c 6d 6e 6f 04 00 00 00 00 00 " +
                "00 00 0f 00 69 6e 73 75 72 61 6e 63 65 2e 62 61 " +
                "73 69 63 06 03 00 00 00 00 00 00 00 01 0b 00 00 " +
                "00 00 00 00 00 20 21 22 23 24 25 26 27 28 29 2a " +
                "2b 2c 2d 2e 2f 06 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 ff ff ff " +
                "ff 00 00 00 00 00 00 00 00 00 00 00 00 00 00",
            ["WorldActors/WorldInteractionIntent/RemoveInsurance"] =
                "70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e 7f " +
                "80 81 82 83 84 85 86 87 88 89 8a 8b 8c 8d 8e 8f " +
                "90 91 92 93 94 95 96 97 98 99 9a 9b 9c 9d 9e 9f " +
                "03 01 28 27 26 25 24 23 22 21 60 61 62 63 64 65 " +
                "66 67 68 69 6a 6b 6c 6d 6e 6f 04 00 00 00 00 00 " +
                "00 00 0f 00 69 6e 73 75 72 61 6e 63 65 2e 62 61 " +
                "73 69 63 06 03 00 00 00 00 00 00 00 02 0b 00 00 " +
                "00 00 00 00 00 20 21 22 23 24 25 26 27 28 29 2a " +
                "2b 2c 2d 2e 2f 06 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 ff ff ff " +
                "ff 00 00 00 00 00 00 00 00 00 00 00 00 00 00",
            ["WorldActors/WorldInteractionIntent/StartGathering"] =
                "70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e 7f " +
                "80 81 82 83 84 85 86 87 88 89 8a 8b 8c 8d 8e 8f " +
                "90 91 92 93 94 95 96 97 98 99 9a 9b 9c 9d 9e 9f " +
                "03 01 28 27 26 25 24 23 22 21 60 61 62 63 64 65 " +
                "66 67 68 69 6a 6b 6c 6d 6e 6f 04 00 00 00 00 00 " +
                "00 00 0d 00 72 65 73 6f 75 72 63 65 2e 6d 69 6e " +
                "65 0a 02 00 00 00 00 00 00 00 07 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 ff ff ff ff 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00",
            ["WorldActors/WorldInteractionIntent/VendorPurchase"] =
                "70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e 7f " +
                "80 81 82 83 84 85 86 87 88 89 8a 8b 8c 8d 8e 8f " +
                "90 91 92 93 94 95 96 97 98 99 9a 9b 9c 9d 9e 9f " +
                "03 01 28 27 26 25 24 23 22 21 60 61 62 63 64 65 " +
                "66 67 68 69 6a 6b 6c 6d 6e 6f 04 00 00 00 00 00 " +
                "00 00 0c 00 76 65 6e 64 6f 72 2e 74 72 61 64 65 " +
                "02 06 00 00 00 00 00 00 00 05 0c 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 ff ff ff ff 14 00 " +
                "70 6f 74 69 6f 6e 2e 6d 69 6e 6f 72 5f 68 65 61 " +
                "6c 69 6e 67 03 00 00 00 4b 00 00 00 00 00 00 00",
            ["WorldActors/WorldInteractionIntent/VendorSale"] =
                "70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e 7f " +
                "80 81 82 83 84 85 86 87 88 89 8a 8b 8c 8d 8e 8f " +
                "90 91 92 93 94 95 96 97 98 99 9a 9b 9c 9d 9e 9f " +
                "03 01 28 27 26 25 24 23 22 21 60 61 62 63 64 65 " +
                "66 67 68 69 6a 6b 6c 6d 6e 6f 04 00 00 00 00 00 " +
                "00 00 0c 00 76 65 6e 64 6f 72 2e 74 72 61 64 65 " +
                "02 06 00 00 00 00 00 00 00 06 0c 00 00 00 00 00 " +
                "00 00 20 21 22 23 24 25 26 27 28 29 2a 2b 2c 2d " +
                "2e 2f 07 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 ff ff ff ff 00 00 " +
                "64 00 00 00 ff ff ff ff ff ff ff 7f",
            ["WorldActors/WorldInteractionIntent/close"] =
                "70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e 7f " +
                "80 81 82 83 84 85 86 87 88 89 8a 8b 8c 8d 8e 8f " +
                "90 91 92 93 94 95 96 97 98 99 9a 9b 9c 9d 9e 9f " +
                "02 01 28 27 26 25 24 23 22 21 60 61 62 63 64 65 " +
                "66 67 68 69 6a 6b 6c 6d 6e 6f 04 00 00 00 00 00 " +
                "00 00",
            ["WorldActors/WorldInteractionIntent/dialogue"] =
                "70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e 7f " +
                "80 81 82 83 84 85 86 87 88 89 8a 8b 8c 8d 8e 8f " +
                "90 91 92 93 94 95 96 97 98 99 9a 9b 9c 9d 9e 9f " +
                "03 01 28 27 26 25 24 23 22 21 60 61 62 63 64 65 " +
                "66 67 68 69 6a 6b 6c 6d 6e 6f 04 00 00 00 00 00 " +
                "00 00 11 00 64 69 61 6c 6f 67 75 65 2e 67 72 65 " +
                "65 74 69 6e 67 01 02 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 ff " +
                "ff ff ff 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00",
            ["WorldActors/WorldInteractionIntent/open actor"] =
                "70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e 7f " +
                "80 81 82 83 84 85 86 87 88 89 8a 8b 8c 8d 8e 8f " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "01 01 28 27 26 25 24 23 22 21 60 61 62 63 64 65 " +
                "66 67 68 69 6a 6b 6c 6d 6e 6f 04 00 00 00 00 00 " +
                "00 00",
            ["WorldActors/WorldInteractionIntent/open corpse"] =
                "70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e 7f " +
                "80 81 82 83 84 85 86 87 88 89 8a 8b 8c 8d 8e 8f " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "01 02 ff ff ff ff ff ff ff ff 60 61 62 63 64 65 " +
                "66 67 68 69 6a 6b 6c 6d 6e 6f ff ff ff ff ff ff " +
                "ff 7f",
            ["WorldActors/WorldInteractionOpened/no capabilities"] =
                "70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e 7f " +
                "90 91 92 93 94 95 96 97 98 99 9a 9b 9c 9d 9e 9f " +
                "01 28 27 26 25 24 23 22 21 60 61 62 63 64 65 66 " +
                "67 68 69 6a 6b 6c 6d 6e 6f 04 00 00 00 00 00 00 " +
                "00 05 00 00 00 00 00 00 00 0b 00 53 6d 65 64 65 " +
                "6e 20 c3 85 6b 65 00",
            ["WorldActors/WorldInteractionOpened/sixteen capabilities"] = "sha256:815:df7de13136a68524cce2dcac37b2d410c03e671393858cae8f429f0941e1a896",
            ["WorldActors/WorldInteractionOpened/three capabilities"] =
                "70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e 7f " +
                "90 91 92 93 94 95 96 97 98 99 9a 9b 9c 9d 9e 9f " +
                "01 28 27 26 25 24 23 22 21 60 61 62 63 64 65 66 " +
                "67 68 69 6a 6b 6c 6d 6e 6f 04 00 00 00 00 00 00 " +
                "00 05 00 00 00 00 00 00 00 0b 00 53 6d 65 64 65 " +
                "6e 20 c3 85 6b 65 03 11 00 64 69 61 6c 6f 67 75 " +
                "65 2e 67 72 65 65 74 69 6e 67 01 05 00 50 72 61 " +
                "74 61 00 01 02 00 00 00 00 00 00 00 0f 00 69 6e " +
                "73 75 72 61 6e 63 65 2e 62 61 73 69 63 06 0e 00 " +
                "46 c3 b6 72 73 c3 a4 6b 72 61 20 e2 9c 93 00 00 " +
                "03 00 00 00 00 00 00 00 0c 00 76 65 6e 64 6f 72 " +
                "2e 74 72 61 64 65 02 06 00 48 61 6e 64 6c 61 01 " +
                "14 00 76 65 6e 64 6f 72 2e 67 65 6e 65 72 61 6c " +
                "5f 73 74 6f 72 65 01 ff ff ff ff ff ff ff 7f",
            ["WorldActors/WorldInteractionResult/capability failed with message"] =
                "70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e 7f " +
                "90 91 92 93 94 95 96 97 98 99 9a 9b 9c 9d 9e 9f " +
                "03 00 04 00 00 00 00 00 00 00 0c 00 00 00 00 00 " +
                "00 00 01 00 20 15 00 69 6e 73 75 72 61 6e 63 65 " +
                "5f 75 6e 61 76 61 69 6c 61 62 6c 65 19 00 49 6e " +
                "73 75 72 61 6e 63 65 20 69 73 20 6e 6f 74 20 6f " +
                "66 66 65 72 65 64 2e",
            ["WorldActors/WorldInteractionResult/capability succeeded"] =
                "70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e 7f " +
                "90 91 92 93 94 95 96 97 98 99 9a 9b 9c 9d 9e 9f " +
                "03 01 ff ff ff ff ff ff ff 7f 4d 00 00 00 00 00 " +
                "00 00 00 00",
            ["WorldActors/WorldInteractionResult/close succeeded"] =
                "70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e 7f " +
                "90 91 92 93 94 95 96 97 98 99 9a 9b 9c 9d 9e 9f " +
                "02 01 04 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 0b 00 46 61 72 76 c3 a4 6c 20 e2 9c 93",
            ["WorldActors/WorldInteractionResult/open failed"] =
                "70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e 7f " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
                "00 00 00 00 18 00 69 6e 74 65 72 61 63 74 69 6f " +
                "6e 5f 6f 75 74 5f 6f 66 5f 72 61 6e 67 65 14 00 " +
                "4d 6f 76 65 20 63 6c 6f 73 65 72 20 74 6f 20 74 " +
                "61 6c 6b 2e",
        };
    }
}
