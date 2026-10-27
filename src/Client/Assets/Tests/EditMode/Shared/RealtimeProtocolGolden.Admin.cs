#nullable disable
using System;
using Mmorpg.GameProtocol;

namespace Mmorpg.SharedTests
{
    // The development admin intents, their results, and the owner's admin state.
    public static partial class RealtimeProtocolGolden
    {
        private static void RegisterAdmin(Registry r)
        {
            RegisterAdminVectors(r);
            RegisterAdminIntentCases(r);
            RegisterAdminResultCases(r);
            RegisterAdminStateCases(r);
        }

        private static void RegisterAdminVectors(Registry r)
        {
            const RealtimeMessageType intent = RealtimeMessageType.AdminIntent;
            const RealtimeMessageType result = RealtimeMessageType.AdminIntentResult;
            const RealtimeMessageType state = RealtimeMessageType.AdminStateChanged;

            r.Vector(
                "Admin/AdminIntent/set movement speed",
                intent,
                () => RealtimeProtocol.EncodeAdminIntent(RealtimeAdminIntent.CreateSetMovementSpeed(G(1), 300)));
            r.Vector(
                "Admin/AdminIntent/teleport",
                intent,
                () => RealtimeProtocol.EncodeAdminIntent(RealtimeAdminIntent.CreateTeleport(G(1), 12.5f, -0.25f, 300f)));
            r.Vector(
                "Admin/AdminIntent/restore health",
                intent,
                () => RealtimeProtocol.EncodeAdminIntent(RealtimeAdminIntent.CreateRestoreHealth(G(1))));
            r.Vector(
                "Admin/AdminIntent/set invulnerable",
                intent,
                () => RealtimeProtocol.EncodeAdminIntent(RealtimeAdminIntent.CreateSetInvulnerable(G(1), true)));
            r.Vector(
                "Admin/AdminIntentResult/accepted",
                result,
                () => RealtimeProtocol.EncodeAdminIntentResult(new RealtimeAdminIntentResult(
                    G(1),
                    RealtimeAdminIntentKind.Teleport,
                    true,
                    null)));
            r.Vector(
                "Admin/AdminIntentResult/rejected",
                result,
                () => RealtimeProtocol.EncodeAdminIntentResult(new RealtimeAdminIntentResult(
                    G(1),
                    RealtimeAdminIntentKind.SetMovementSpeed,
                    false,
                    new RealtimeError("admin_tools_disabled", "Only a Development worker allows the admin tools."))));
            r.Vector(
                "Admin/AdminStateChanged/default",
                state,
                () => RealtimeProtocol.EncodeAdminStateChanged(new RealtimeAdminStateChanged(100, false)));
            r.Vector(
                "Admin/AdminStateChanged/fast and invulnerable",
                state,
                () => RealtimeProtocol.EncodeAdminStateChanged(new RealtimeAdminStateChanged(1000, true)));
        }

        private static void RegisterAdminIntentCases(Registry r)
        {
            r.Accepts("Admin/AdminIntent/speed baseline", AdminIntentWire(1, Wire.Fragment().U16(200)));
            r.Accepts("Admin/AdminIntent/slowest speed", AdminIntentWire(1, Wire.Fragment().U16(100)));
            r.Accepts("Admin/AdminIntent/fastest speed", AdminIntentWire(1, Wire.Fragment().U16(1000)));
            r.Rejects("Admin/AdminIntent/speed below the slowest", AdminIntentWire(1, Wire.Fragment().U16(99)));
            r.Rejects("Admin/AdminIntent/speed above the fastest", AdminIntentWire(1, Wire.Fragment().U16(1001)));
            r.Rejects("Admin/AdminIntent/speed without a value", AdminIntentWire(1, Wire.Fragment()));
            r.Accepts("Admin/AdminIntent/teleport baseline", AdminIntentWire(2, TeleportFragment(1f, 2f, 3f)));
            r.Rejects(
                "Admin/AdminIntent/NaN teleport destination",
                AdminIntentWire(2, Wire.Fragment().F32(1f).F32Bits(QuietNaNBits).F32(3f)));
            r.Rejects(
                "Admin/AdminIntent/infinite teleport destination",
                AdminIntentWire(2, Wire.Fragment().F32Bits(PositiveInfinityBits).F32(2f).F32(3f)));
            r.Rejects("Admin/AdminIntent/teleport without a destination", AdminIntentWire(2, Wire.Fragment().F32(1f)));
            r.Accepts("Admin/AdminIntent/restore health baseline", AdminIntentWire(3, Wire.Fragment()));
            r.Rejects("Admin/AdminIntent/restore health with a value", AdminIntentWire(3, Wire.Fragment().U16(200)));
            r.Accepts("Admin/AdminIntent/invulnerable baseline", AdminIntentWire(4, Wire.Fragment().Bool(true)));
            r.Accepts("Admin/AdminIntent/vulnerable baseline", AdminIntentWire(4, Wire.Fragment().Bool(false)));
            r.AcceptsAs(
                "Admin/AdminIntent/invulnerable byte 2 reads as true",
                AdminIntentWire(4, Wire.Fragment().U8(2)),
                AdminIntentWire(4, Wire.Fragment().Bool(true)));
            r.Rejects("Admin/AdminIntent/empty operation id", AdminIntentWire(3, Wire.Fragment(), operationId: Guid.Empty));
            r.Rejects("Admin/AdminIntent/kind 0", AdminIntentWire(0, Wire.Fragment()));
            r.Rejects("Admin/AdminIntent/kind 5", AdminIntentWire(5, Wire.Fragment()));
            r.RejectsEncode(
                "Admin/AdminIntent/empty operation id",
                () => RealtimeProtocol.EncodeAdminIntent(RealtimeAdminIntent.CreateRestoreHealth(Guid.Empty)));
            r.RejectsEncode(
                "Admin/AdminIntent/speed above the fastest",
                () => RealtimeProtocol.EncodeAdminIntent(RealtimeAdminIntent.CreateSetMovementSpeed(G(1), 1001)));
            r.RejectsEncode(
                "Admin/AdminIntent/NaN teleport destination",
                () => RealtimeProtocol.EncodeAdminIntent(RealtimeAdminIntent.CreateTeleport(G(1), float.NaN, 0f, 0f)));
        }

        private static void RegisterAdminResultCases(Registry r)
        {
            r.Accepts("Admin/AdminIntentResult/accepted baseline", AdminIntentResultWire());
            r.Accepts(
                "Admin/AdminIntentResult/rejected baseline",
                AdminIntentResultWire(succeeded: 0, error: ErrorFragment("admin_target_dead", "Dead characters cannot teleport.")));
            r.Rejects("Admin/AdminIntentResult/empty operation id", AdminIntentResultWire(operationId: Guid.Empty));
            r.Rejects("Admin/AdminIntentResult/kind 0", AdminIntentResultWire(kind: 0));
            r.Rejects("Admin/AdminIntentResult/rejected without error", AdminIntentResultWire(succeeded: 0));
            r.Rejects(
                "Admin/AdminIntentResult/blank error code",
                AdminIntentResultWire(succeeded: 0, error: ErrorFragment(" ", "message")));
            r.RejectsEncode(
                "Admin/AdminIntentResult/rejected without error",
                () => RealtimeProtocol.EncodeAdminIntentResult(new RealtimeAdminIntentResult(
                    G(1),
                    RealtimeAdminIntentKind.RestoreHealth,
                    false,
                    null)));
        }

        private static void RegisterAdminStateCases(Registry r)
        {
            r.Accepts("Admin/AdminStateChanged/baseline", AdminStateWire(100, 0));
            r.Accepts("Admin/AdminStateChanged/fastest and invulnerable", AdminStateWire(1000, 1));
            r.Rejects("Admin/AdminStateChanged/speed below the slowest", AdminStateWire(99, 0));
            r.Rejects("Admin/AdminStateChanged/speed above the fastest", AdminStateWire(1001, 0));
            r.AcceptsAs(
                "Admin/AdminStateChanged/invulnerable byte 2 reads as true",
                AdminStateWire(100, 2),
                AdminStateWire(100, 1));
            r.RejectsEncode(
                "Admin/AdminStateChanged/speed 0",
                () => RealtimeProtocol.EncodeAdminStateChanged(new RealtimeAdminStateChanged(0, false)));
        }

        private static Wire AdminIntentWire(byte kind, Wire payload, Guid? operationId = null)
        {
            return Wire.Message(RealtimeMessageType.AdminIntent)
                .Guid(operationId ?? G(1))
                .U8(kind)
                .Append(payload);
        }

        private static Wire TeleportFragment(float x, float y, float z)
        {
            return Wire.Fragment().F32(x).F32(y).F32(z);
        }

        private static Wire AdminIntentResultWire(
            Guid? operationId = null,
            byte kind = 2,
            byte succeeded = 1,
            Wire error = null)
        {
            var wire = Wire.Message(RealtimeMessageType.AdminIntentResult)
                .Guid(operationId ?? G(1))
                .U8(kind)
                .U8(succeeded);
            return error == null ? wire : wire.Append(error);
        }

        private static Wire AdminStateWire(ushort speedPercent, byte invulnerable)
        {
            return Wire.Message(RealtimeMessageType.AdminStateChanged)
                .U16(speedPercent)
                .U8(invulnerable);
        }
    }
}
