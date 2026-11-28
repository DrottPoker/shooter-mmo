#nullable disable
using Mmorpg.GameProtocol;

namespace Mmorpg.SharedTests
{
    // The owner's level and experience, and the level-ups that the viewers of a character see.
    public static partial class RealtimeProtocolGolden
    {
        private static void RegisterProgression(Registry r)
        {
            RegisterProgressionVectors(r);
            RegisterOwnerProgressionCases(r);
            RegisterEntityLeveledUpCases(r);
        }

        private static void RegisterProgressionVectors(Registry r)
        {
            const RealtimeMessageType owner = RealtimeMessageType.OwnerProgressionChanged;
            const RealtimeMessageType levelUp = RealtimeMessageType.EntityLeveledUp;

            r.Vector(
                "Progression/OwnerProgressionChanged/snapshot",
                owner,
                () => RealtimeProtocol.EncodeOwnerProgressionChanged(new RealtimeOwnerProgressionChanged(
                    12,
                    3400,
                    9800,
                    5,
                    0,
                    0,
                    RealtimeProgressionChangeReason.Snapshot)));
            r.Vector(
                "Progression/OwnerProgressionChanged/mob kill level up",
                owner,
                () => RealtimeProtocol.EncodeOwnerProgressionChanged(new RealtimeOwnerProgressionChanged(
                    3,
                    20,
                    1500,
                    -4,
                    450,
                    1,
                    RealtimeProgressionChangeReason.MobKill)));
            r.Vector(
                "Progression/OwnerProgressionChanged/admin lowered the level",
                owner,
                () => RealtimeProtocol.EncodeOwnerProgressionChanged(new RealtimeOwnerProgressionChanged(
                    2,
                    0,
                    400,
                    -10,
                    0,
                    -38,
                    RealtimeProgressionChangeReason.Admin)));
            r.Vector(
                "Progression/OwnerProgressionChanged/quest reward to the highest level",
                owner,
                () => RealtimeProtocol.EncodeOwnerProgressionChanged(new RealtimeOwnerProgressionChanged(
                    200,
                    0,
                    0,
                    int.MaxValue,
                    1200,
                    1,
                    RealtimeProgressionChangeReason.QuestReward)));
            r.Vector(
                "Progression/EntityLeveledUp/typical",
                levelUp,
                () => RealtimeProtocol.EncodeEntityLeveledUp(new RealtimeEntityLeveledUp(0x4142434445464748UL, 13)));
        }

        private static void RegisterOwnerProgressionCases(Registry r)
        {
            r.Accepts("Progression/OwnerProgressionChanged/baseline", OwnerProgressionWire());
            r.Accepts(
                "Progression/OwnerProgressionChanged/highest level",
                OwnerProgressionWire(level: ushort.MaxValue, toNext: 0, highest: int.MaxValue));
            r.Accepts(
                "Progression/OwnerProgressionChanged/lowered level",
                OwnerProgressionWire(highest: int.MinValue, levels: int.MinValue, reason: 4));
            r.Rejects("Progression/OwnerProgressionChanged/level 0", OwnerProgressionWire(level: 0));
            r.Rejects("Progression/OwnerProgressionChanged/negative experience", OwnerProgressionWire(experience: -1));
            r.Rejects("Progression/OwnerProgressionChanged/negative experience to next level", OwnerProgressionWire(toNext: -1));
            r.Rejects("Progression/OwnerProgressionChanged/negative experience gained", OwnerProgressionWire(gained: -1));
            r.Rejects("Progression/OwnerProgressionChanged/reason 0", OwnerProgressionWire(reason: 0));
            r.Rejects("Progression/OwnerProgressionChanged/reason 5", OwnerProgressionWire(reason: 5));
            r.RejectsEncode(
                "Progression/OwnerProgressionChanged/level 0",
                () => RealtimeProtocol.EncodeOwnerProgressionChanged(new RealtimeOwnerProgressionChanged(
                    0,
                    0,
                    100,
                    0,
                    0,
                    0,
                    RealtimeProgressionChangeReason.Snapshot)));
            r.RejectsEncode(
                "Progression/OwnerProgressionChanged/negative experience",
                () => RealtimeProtocol.EncodeOwnerProgressionChanged(new RealtimeOwnerProgressionChanged(
                    1,
                    -1,
                    100,
                    0,
                    0,
                    0,
                    RealtimeProgressionChangeReason.Snapshot)));
            r.RejectsEncode(
                "Progression/OwnerProgressionChanged/reason 0",
                () => RealtimeProtocol.EncodeOwnerProgressionChanged(new RealtimeOwnerProgressionChanged(
                    1,
                    0,
                    100,
                    0,
                    0,
                    0,
                    0)));
        }

        private static void RegisterEntityLeveledUpCases(Registry r)
        {
            r.Accepts("Progression/EntityLeveledUp/baseline", EntityLeveledUpWire(7, 2));
            r.Accepts("Progression/EntityLeveledUp/highest level", EntityLeveledUpWire(ulong.MaxValue, ushort.MaxValue));
            r.Rejects("Progression/EntityLeveledUp/entity 0", EntityLeveledUpWire(0, 2));
            r.Rejects("Progression/EntityLeveledUp/level 0", EntityLeveledUpWire(7, 0));
            r.Rejects("Progression/EntityLeveledUp/without a level", Wire.Message(RealtimeMessageType.EntityLeveledUp).U64(7));
            r.RejectsEncode(
                "Progression/EntityLeveledUp/entity 0",
                () => RealtimeProtocol.EncodeEntityLeveledUp(new RealtimeEntityLeveledUp(0, 2)));
            r.RejectsEncode(
                "Progression/EntityLeveledUp/level 0",
                () => RealtimeProtocol.EncodeEntityLeveledUp(new RealtimeEntityLeveledUp(7, 0)));
        }

        private static Wire OwnerProgressionWire(
            ushort level = 4,
            long experience = 120,
            long toNext = 900,
            int highest = -2,
            long gained = 60,
            int levels = 0,
            byte reason = 2)
        {
            return Wire.Message(RealtimeMessageType.OwnerProgressionChanged)
                .U16(level)
                .I64(experience)
                .I64(toNext)
                .I32(highest)
                .I64(gained)
                .I32(levels)
                .U8(reason);
        }

        private static Wire EntityLeveledUpWire(ulong entityId, ushort level)
        {
            return Wire.Message(RealtimeMessageType.EntityLeveledUp)
                .U64(entityId)
                .U16(level);
        }
    }
}
