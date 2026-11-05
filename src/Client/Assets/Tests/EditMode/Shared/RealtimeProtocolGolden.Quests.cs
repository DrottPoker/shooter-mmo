#nullable disable
using System;
using System.Linq;
using Mmorpg.GameProtocol;

namespace Mmorpg.SharedTests
{
    // The quest intents outside an interaction, their results, the owner's quest log changes, and
    // the quest items that a corpse holds for its owner.
    public static partial class RealtimeProtocolGolden
    {
        private static void RegisterQuests(Registry r)
        {
            RegisterQuestVectors(r);
            RegisterQuestIntentCases(r);
            RegisterQuestIntentResultCases(r);
            RegisterQuestLogChangedCases(r);
            RegisterQuestDropInCorpseCases(r);
        }

        private static void RegisterQuestVectors(Registry r)
        {
            const RealtimeMessageType intent = RealtimeMessageType.QuestIntent;
            const RealtimeMessageType result = RealtimeMessageType.QuestIntentResult;
            const RealtimeMessageType change = RealtimeMessageType.QuestLogChanged;
            const RealtimeMessageType drop = RealtimeMessageType.QuestDropInCorpse;

            r.Vector(
                "Quests/QuestIntent/abandon",
                intent,
                () => RealtimeProtocol.EncodeQuestIntent(new RealtimeQuestIntent(
                    G(1),
                    RealtimeQuestIntentKind.Abandon,
                    "quest.wolves_at_the_gate")));
            r.Vector(
                "Quests/QuestIntentResult/accepted",
                result,
                () => RealtimeProtocol.EncodeQuestIntentResult(new RealtimeQuestIntentResult(
                    G(1),
                    RealtimeQuestIntentKind.Abandon,
                    true,
                    42,
                    null)));
            r.Vector(
                "Quests/QuestIntentResult/rejected",
                result,
                () => RealtimeProtocol.EncodeQuestIntentResult(new RealtimeQuestIntentResult(
                    G(1),
                    RealtimeQuestIntentKind.Abandon,
                    false,
                    0,
                    new RealtimeError("quest_not_active", "The quest is not active."))));
            r.Vector(
                "Quests/QuestLogChanged/accepted",
                change,
                () => RealtimeProtocol.EncodeQuestLogChanged(new RealtimeQuestLogChanged(
                    G(1),
                    3,
                    4,
                    false,
                    new[]
                    {
                        new RealtimeQuestChange(
                            "quest.wolves_at_the_gate",
                            "hunt",
                            new[] { new RealtimeQuestObjectiveProgress("wolves", 0) },
                            0)
                    })));
            r.Vector(
                "Quests/QuestLogChanged/kill credit for two quests",
                change,
                () => RealtimeProtocol.EncodeQuestLogChanged(new RealtimeQuestLogChanged(
                    Guid.Empty,
                    9,
                    10,
                    false,
                    new[]
                    {
                        new RealtimeQuestChange(
                            "quest.pelts_and_fangs",
                            "collect",
                            new[]
                            {
                                new RealtimeQuestObjectiveProgress("alpha", 1),
                                new RealtimeQuestObjectiveProgress("wolves", 1000)
                            },
                            2),
                        new RealtimeQuestChange(
                            "quest.wolves_at_the_gate",
                            "hunt",
                            new[] { new RealtimeQuestObjectiveProgress("wolves", 5) },
                            0)
                    })));
            r.Vector(
                "Quests/QuestLogChanged/completed",
                change,
                () => RealtimeProtocol.EncodeQuestLogChanged(new RealtimeQuestLogChanged(
                    G(1),
                    7,
                    9,
                    false,
                    new[] { new RealtimeQuestChange("quest.letter_for_the_outpost", "", null, 1) })));
            r.Vector(
                "Quests/QuestLogChanged/reload",
                change,
                () => RealtimeProtocol.EncodeQuestLogChanged(new RealtimeQuestLogChanged(
                    Guid.Empty,
                    10,
                    12,
                    true,
                    null)));
            r.Vector(
                "Quests/QuestDropInCorpse/two wolf fangs",
                drop,
                () => RealtimeProtocol.EncodeQuestDropInCorpse(new RealtimeQuestDropInCorpse(
                    G(1),
                    "Feral Wolf",
                    "quest.pelts_and_fangs",
                    "quest_item.wolf_fang",
                    2)));
        }

        private static void RegisterQuestIntentCases(Registry r)
        {
            r.Accepts("Quests/QuestIntent/abandon baseline", QuestIntentWire());
            r.Accepts("Quests/QuestIntent/128 character quest", QuestIntentWire(quest: Repeat("q", 128)));
            r.Rejects("Quests/QuestIntent/129 character quest", QuestIntentWire(quest: Repeat("q", 129)));
            r.Rejects("Quests/QuestIntent/empty quest", QuestIntentWire(quest: ""));
            r.Rejects("Quests/QuestIntent/whitespace quest", QuestIntentWire(quest: " "));
            r.Rejects("Quests/QuestIntent/empty operation id", QuestIntentWire(operationId: Guid.Empty));
            r.Rejects("Quests/QuestIntent/kind 0", QuestIntentWire(kind: 0));
            r.Rejects("Quests/QuestIntent/kind 2", QuestIntentWire(kind: 2));
            r.RejectsEncode(
                "Quests/QuestIntent/empty quest",
                () => RealtimeProtocol.EncodeQuestIntent(new RealtimeQuestIntent(G(1), RealtimeQuestIntentKind.Abandon, "")));
            r.RejectsEncode(
                "Quests/QuestIntent/empty operation id",
                () => RealtimeProtocol.EncodeQuestIntent(new RealtimeQuestIntent(
                    Guid.Empty,
                    RealtimeQuestIntentKind.Abandon,
                    "quest.wolves_at_the_gate")));
        }

        private static void RegisterQuestIntentResultCases(Registry r)
        {
            r.Accepts("Quests/QuestIntentResult/accepted baseline", QuestIntentResultWire());
            r.Accepts(
                "Quests/QuestIntentResult/rejected baseline",
                QuestIntentResultWire(succeeded: 0, itemStateRevision: 0, error: ErrorFragment("quest_not_found", "No such quest.")));
            r.Rejects("Quests/QuestIntentResult/rejected without error", QuestIntentResultWire(succeeded: 0));
            r.Rejects("Quests/QuestIntentResult/negative item revision", QuestIntentResultWire(itemStateRevision: -1));
            r.Rejects("Quests/QuestIntentResult/empty operation id", QuestIntentResultWire(operationId: Guid.Empty));
            r.Rejects("Quests/QuestIntentResult/kind 0", QuestIntentResultWire(kind: 0));
            r.Rejects(
                "Quests/QuestIntentResult/blank error code",
                QuestIntentResultWire(succeeded: 0, error: ErrorFragment(" ", "message")));
            r.RejectsEncode(
                "Quests/QuestIntentResult/rejected without error",
                () => RealtimeProtocol.EncodeQuestIntentResult(new RealtimeQuestIntentResult(
                    G(1),
                    RealtimeQuestIntentKind.Abandon,
                    false,
                    0,
                    null)));
        }

        private static void RegisterQuestLogChangedCases(Registry r)
        {
            r.Accepts("Quests/QuestLogChanged/baseline", QuestLogChangedWire());
            r.Accepts("Quests/QuestLogChanged/without operation", QuestLogChangedWire(operationId: Guid.Empty));
            r.Accepts("Quests/QuestLogChanged/from revision 0", QuestLogChangedWire(previous: 0, revision: 1));
            r.Accepts("Quests/QuestLogChanged/no longer active", QuestLogChangedWire(quests: new[] { QuestChangeFragment(active: false, objectives: new Wire[0], completions: 1) }));
            r.Accepts("Quests/QuestLogChanged/step without counted objectives", QuestLogChangedWire(quests: new[] { QuestChangeFragment(objectives: new Wire[0]) }));
            r.Accepts("Quests/QuestLogChanged/reload", QuestLogChangedWire(reload: 1, quests: new Wire[0]));
            r.Accepts(
                "Quests/QuestLogChanged/eight quests",
                QuestLogChangedWire(quests: Enumerable.Range(0, 8).Select(index => QuestChangeFragment(quest: "quest.q" + index)).ToArray()));
            r.Accepts(
                "Quests/QuestLogChanged/eight objectives",
                QuestLogChangedWire(quests: new[]
                {
                    QuestChangeFragment(objectives: Enumerable.Range(0, 8).Select(index => ObjectiveFragment("o" + index, index)).ToArray())
                }));
            r.Accepts("Quests/QuestLogChanged/progress 1000", QuestLogChangedWire(quests: new[] { QuestChangeFragment(objectives: new[] { ObjectiveFragment("wolves", 1000) }) }));
            r.Rejects("Quests/QuestLogChanged/progress 1001", QuestLogChangedWire(quests: new[] { QuestChangeFragment(objectives: new[] { ObjectiveFragment("wolves", 1001) }) }));
            r.Rejects("Quests/QuestLogChanged/negative progress", QuestLogChangedWire(quests: new[] { QuestChangeFragment(objectives: new[] { ObjectiveFragment("wolves", -1) }) }));
            r.Rejects("Quests/QuestLogChanged/revision not advanced", QuestLogChangedWire(previous: 4, revision: 4));
            r.Rejects("Quests/QuestLogChanged/revision behind", QuestLogChangedWire(previous: 5, revision: 4));
            r.Rejects("Quests/QuestLogChanged/revision 0", QuestLogChangedWire(previous: 0, revision: 0));
            r.Rejects("Quests/QuestLogChanged/negative previous revision", QuestLogChangedWire(previous: -1));
            r.Rejects("Quests/QuestLogChanged/reload with a quest", QuestLogChangedWire(reload: 1));
            r.Rejects("Quests/QuestLogChanged/neither quests nor reload", QuestLogChangedWire(quests: new Wire[0]));
            r.Rejects(
                "Quests/QuestLogChanged/inactive quest with progress",
                QuestLogChangedWire(quests: new[] { QuestChangeFragment(active: false, completions: 1) }));
            r.Rejects(
                "Quests/QuestLogChanged/duplicate quest",
                QuestLogChangedWire(quests: new[] { QuestChangeFragment(), QuestChangeFragment() }));
            r.Rejects(
                "Quests/QuestLogChanged/duplicate objective",
                QuestLogChangedWire(quests: new[]
                {
                    QuestChangeFragment(objectives: new[] { ObjectiveFragment("wolves", 1), ObjectiveFragment("wolves", 2) })
                }));
            r.Rejects(
                "Quests/QuestLogChanged/nine quests",
                QuestLogChangedWire(quests: Enumerable.Range(0, 9).Select(index => QuestChangeFragment(quest: "quest.q" + index)).ToArray()));
            r.Rejects(
                "Quests/QuestLogChanged/nine objectives",
                QuestLogChangedWire(quests: new[]
                {
                    QuestChangeFragment(objectives: Enumerable.Range(0, 9).Select(index => ObjectiveFragment("o" + index, index)).ToArray())
                }));
            r.Rejects("Quests/QuestLogChanged/empty quest", QuestLogChangedWire(quests: new[] { QuestChangeFragment(quest: "") }));
            r.Rejects(
                "Quests/QuestLogChanged/present whitespace step with progress",
                QuestLogChangedWire(quests: new[] { QuestChangeFragment(step: " ") }));
            r.AcceptsAs(
                "Quests/QuestLogChanged/present whitespace step reads as absent",
                QuestLogChangedWire(quests: new[] { QuestChangeFragment(step: " ", objectives: new Wire[0], completions: 1) }),
                QuestLogChangedWire(quests: new[] { QuestChangeFragment(active: false, objectives: new Wire[0], completions: 1) }));
            r.Rejects(
                "Quests/QuestLogChanged/empty objective",
                QuestLogChangedWire(quests: new[] { QuestChangeFragment(objectives: new[] { ObjectiveFragment("", 1) }) }));
            r.Rejects(
                "Quests/QuestLogChanged/negative completion count",
                QuestLogChangedWire(quests: new[] { QuestChangeFragment(completions: -1) }));
            r.RejectsEncode(
                "Quests/QuestLogChanged/revision not advanced",
                () => RealtimeProtocol.EncodeQuestLogChanged(new RealtimeQuestLogChanged(G(1), 4, 4, true, null)));
            r.RejectsEncode(
                "Quests/QuestLogChanged/reload with a quest",
                () => RealtimeProtocol.EncodeQuestLogChanged(new RealtimeQuestLogChanged(
                    G(1),
                    4,
                    5,
                    true,
                    new[] { new RealtimeQuestChange("quest.wolves_at_the_gate", "hunt", null, 0) })));
            r.RejectsOversize(
                "Quests/QuestLogChanged/eight quests with eight long objectives",
                () => RealtimeProtocol.EncodeQuestLogChanged(new RealtimeQuestLogChanged(
                    G(1),
                    4,
                    5,
                    false,
                    Enumerable.Range(0, 8)
                        .Select(quest => new RealtimeQuestChange(
                            "quest." + quest + Repeat("q", 100),
                            Repeat("s", 128),
                            Enumerable.Range(0, 8)
                                .Select(objective => new RealtimeQuestObjectiveProgress(objective + Repeat("o", 100), 1))
                                .ToArray(),
                            0))
                        .ToArray())));
        }

        private static void RegisterQuestDropInCorpseCases(Registry r)
        {
            r.Accepts("Quests/QuestDropInCorpse/baseline", QuestDropInCorpseWire());
            r.Accepts(
                "Quests/QuestDropInCorpse/128 character texts",
                QuestDropInCorpseWire(source: Repeat("s", 128), quest: Repeat("q", 128), definition: Repeat("d", 128)));
            r.Rejects("Quests/QuestDropInCorpse/129 character source", QuestDropInCorpseWire(source: Repeat("s", 129)));
            r.Rejects("Quests/QuestDropInCorpse/129 character quest", QuestDropInCorpseWire(quest: Repeat("q", 129)));
            r.Rejects("Quests/QuestDropInCorpse/129 character item", QuestDropInCorpseWire(definition: Repeat("d", 129)));
            r.Rejects("Quests/QuestDropInCorpse/empty corpse id", QuestDropInCorpseWire(corpseId: Guid.Empty));
            r.Rejects("Quests/QuestDropInCorpse/whitespace source", QuestDropInCorpseWire(source: " "));
            r.Rejects("Quests/QuestDropInCorpse/empty quest", QuestDropInCorpseWire(quest: ""));
            r.Rejects("Quests/QuestDropInCorpse/empty item", QuestDropInCorpseWire(definition: ""));
            r.Rejects("Quests/QuestDropInCorpse/quantity 0", QuestDropInCorpseWire(quantity: 0));
            r.Rejects("Quests/QuestDropInCorpse/negative quantity", QuestDropInCorpseWire(quantity: -1));
            r.RejectsEncode(
                "Quests/QuestDropInCorpse/quantity 0",
                () => RealtimeProtocol.EncodeQuestDropInCorpse(new RealtimeQuestDropInCorpse(
                    G(1),
                    "Feral Wolf",
                    "quest.pelts_and_fangs",
                    "quest_item.wolf_fang",
                    0)));
            r.RejectsEncode(
                "Quests/QuestDropInCorpse/empty corpse id",
                () => RealtimeProtocol.EncodeQuestDropInCorpse(new RealtimeQuestDropInCorpse(
                    Guid.Empty,
                    "Feral Wolf",
                    "quest.pelts_and_fangs",
                    "quest_item.wolf_fang",
                    1)));
        }

        private static Wire QuestIntentWire(Guid? operationId = null, byte kind = 1, WireText quest = null)
        {
            return Wire.Message(RealtimeMessageType.QuestIntent)
                .Guid(operationId ?? G(1))
                .U8(kind)
                .Text(quest ?? "quest.wolves_at_the_gate");
        }

        private static Wire QuestIntentResultWire(
            Guid? operationId = null,
            byte kind = 1,
            byte succeeded = 1,
            long itemStateRevision = 42,
            Wire error = null)
        {
            var wire = Wire.Message(RealtimeMessageType.QuestIntentResult)
                .Guid(operationId ?? G(1))
                .U8(kind)
                .U8(succeeded)
                .I64(itemStateRevision);
            return error == null ? wire : wire.Append(error);
        }

        private static Wire QuestLogChangedWire(
            Guid? operationId = null,
            long previous = 3,
            long revision = 4,
            byte reload = 0,
            Wire[] quests = null)
        {
            var list = quests ?? new[] { QuestChangeFragment() };
            var wire = Wire.Message(RealtimeMessageType.QuestLogChanged)
                .Guid(operationId ?? G(1))
                .I64(previous)
                .I64(revision)
                .U8(reload)
                .U8((byte)list.Length);
            foreach (var quest in list)
            {
                wire.Append(quest);
            }

            return wire;
        }

        // An active quest has a step; one that is no longer active writes none.
        private static Wire QuestChangeFragment(
            WireText quest = null,
            bool active = true,
            WireText step = null,
            Wire[] objectives = null,
            int completions = 0)
        {
            var wire = Wire.Fragment().Text(quest ?? "quest.wolves_at_the_gate");
            wire = active ? wire.Bool(true).Text(step ?? "hunt") : wire.Bool(false);
            var list = objectives ?? new[] { ObjectiveFragment("wolves", 2) };
            wire.U8((byte)list.Length);
            foreach (var objective in list)
            {
                wire.Append(objective);
            }

            return wire.I32(completions);
        }

        private static Wire ObjectiveFragment(WireText objective, int progress)
        {
            return Wire.Fragment().Text(objective).I32(progress);
        }

        private static Wire QuestDropInCorpseWire(
            Guid? corpseId = null,
            WireText source = null,
            WireText quest = null,
            WireText definition = null,
            int quantity = 2)
        {
            return Wire.Message(RealtimeMessageType.QuestDropInCorpse)
                .Guid(corpseId ?? G(1))
                .Text(source ?? "Feral Wolf")
                .Text(quest ?? "quest.pelts_and_fangs")
                .Text(definition ?? "quest_item.wolf_fang")
                .I32(quantity);
        }
    }
}
