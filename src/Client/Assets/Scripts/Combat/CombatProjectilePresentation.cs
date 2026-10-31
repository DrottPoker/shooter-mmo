using System.Collections.Generic;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using UnityEngine;

namespace Mmorpg.Client.Combat
{
    // When a released attack shows a projectile, how long it still flies, and when a started attack
    // releases.
    public static class CombatProjectileRules
    {
        // Shorter flights are not worth drawing, such as an arrow that already landed.
        public const float MinimumFlightSeconds = 0.05f;

        // A projectile lands after the tick of its release; a Mob bite resolves at once.
        public static bool IsProjectile(RealtimeCombatEvent combatEvent, uint batchServerTick)
        {
            return combatEvent != null
                && combatEvent.Kind == RealtimeCombatEventKind.AttackReleased
                && unchecked((int)(combatEvent.Tick - batchServerTick)) > 0;
        }

        // The seconds until the impact tick by the client's server clock, or by the batch
        // tick before the first owner snapshot.
        public static float RemainingFlightSeconds(
            CombatServerClock clock,
            uint impactTick,
            uint batchServerTick,
            int tickRateHz)
        {
            if (clock.IsKnown)
            {
                return clock.SecondsUntil(impactTick);
            }

            return tickRateHz > 0
                ? unchecked((int)(impactTick - batchServerTick)) / (float)tickRateHz
                : 0f;
        }

        // The seconds from the batch of a started attack until it releases at its release tick, by
        // the batch's own tick, so an attack clip timed by it strikes or looses as the batch of the
        // release arrives; 0 before the tick rate is known.
        public static float SecondsUntilRelease(uint releaseTick, uint batchServerTick, int tickRateHz)
        {
            return tickRateHz > 0
                ? unchecked((int)(releaseTick - batchServerTick)) / (float)tickRateHz
                : 0f;
        }
    }

    // Replaceable presentation of attacks. When an attack starts, the attacker's body plays its
    // weapon's attack clip, such as Cast for a staff, timed to strike or loose when the attack
    // releases, and an archer's arrow lies on the string while the drawing hand holds it; a
    // cancelled attack ends its clip. When an attack releases, a creature with clips of its own, such
    // as a wolf, bites, and a projectile flies from the attacker to the target and lands at the
    // event's impact tick: an arrow for a ranged attack, from the string or else the weapon, the same
    // arrow for every weapon that shoots arrows, and a glowing bolt from the weapon's launch point,
    // such as a staff's crystal, for a magic one. It decides nothing; damage comes from Hit events
    // and health from the combat states.
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(PresentationExecutionOrder.Projectiles)]
    public sealed class CombatProjectilePresentation : MonoBehaviour
    {
        private const int MaximumFlights = 32;
        private const float LaunchHeightFraction = 0.7f;

        // An arrow that the string let go of waits there this long for its release to arrive, and
        // no shot is drawn longer than the other limit.
        private const float LooseWaitSeconds = 0.25f;
        private const float MaximumShotSeconds = 5f;

        // Without an arrow model, an arrow is a stretched cube with its point at its middle.
        private static readonly Vector3 ArrowScale = new Vector3(0.05f, 0.05f, 0.7f);
        private static readonly Color ArrowColor = new Color(0.93f, 0.89f, 0.78f, 1f);

        // A bolt is a bright core with two fading sparks trailing it, in an arcane blue-white.
        private static readonly float[] BoltSparkScales = { 0.16f, 0.1f, 0.06f };
        private const float BoltSparkSpacing = 0.12f;
        private static readonly Color BoltColor = new Color(0.78f, 0.94f, 1f, 1f);
        private static readonly Color BoltEmission = new Color(0.45f, 0.8f, 1f, 1f) * 2.5f;

        private readonly List<Flight> flights = new List<Flight>();
        private readonly List<Shot> shots = new List<Shot>();
        private readonly Stack<GameObject> idleArrows = new Stack<GameObject>();
        private readonly Stack<GameObject> idleBolts = new Stack<GameObject>();
        private CombatClientController combatController;
        private RealtimeSimulationClient realtimeClient;
        private CombatantViewLocator views;
        private Material primitiveMaterial;
        private GameObject arrowModel;
        // How far the nock lies behind the point, the root of an arrow.
        private float arrowLength = ArrowScale.z * 0.5f;
        private Material arrowMaterial;
        private Material boltMaterial;
        private bool subscribed;

        public int ActiveFlightCount
        {
            get { return flights.Count; }
        }

        // The arrows that lie on their archers' strings or wait there for their release.
        public int NockedArrowCount
        {
            get
            {
                var count = 0;
                foreach (var shot in shots)
                {
                    count += shot.Arrow != null ? 1 : 0;
                }

                return count;
            }
        }

        // Called by the composition root before the component is activated. The arrow's model has
        // its point at its root and flies along its forward axis; without one, arrows are cubes.
        public void Initialize(
            CombatClientController combat,
            RealtimeSimulationClient realtime,
            CombatantViewLocator combatantViews,
            Material runtimePrimitiveMaterial,
            GameObject arrow = null)
        {
            combatController = combat;
            realtimeClient = realtime;
            views = combatantViews;
            primitiveMaterial = runtimePrimitiveMaterial;
            arrowModel = arrow;
            if (arrowModel != null)
            {
                arrowLength = LengthOf(arrowModel);
            }
        }

        private void OnEnable()
        {
            if (combatController != null && !subscribed)
            {
                combatController.State.EventsReceived += OnEventsReceived;
                subscribed = true;
            }
        }

        private void OnDisable()
        {
            if (subscribed && combatController != null)
            {
                combatController.State.EventsReceived -= OnEventsReceived;
            }

            subscribed = false;
            for (var index = flights.Count - 1; index >= 0; index--)
            {
                Release(index);
            }

            for (var index = shots.Count - 1; index >= 0; index--)
            {
                RemoveShot(index);
            }
        }

        private void OnDestroy()
        {
            DestroyIdle(idleArrows);
            DestroyIdle(idleBolts);
            if (arrowMaterial != null)
            {
                Destroy(arrowMaterial);
            }

            if (boltMaterial != null)
            {
                Destroy(boltMaterial);
            }
        }

        private static void DestroyIdle(Stack<GameObject> idle)
        {
            while (idle.Count > 0)
            {
                var projectile = idle.Pop();
                if (projectile != null)
                {
                    Destroy(projectile);
                }
            }
        }

        private void OnEventsReceived(RealtimeCombatEventBatch batch)
        {
            if (views == null || batch.Events == null)
            {
                return;
            }

            var session = realtimeClient != null ? realtimeClient.MovementSession : null;
            var tickRateHz = session != null ? session.Settings.TickRateHz : 0;
            var clock = CombatServerClock.FromClient(realtimeClient);
            for (var index = 0; index < batch.Events.Length; index++)
            {
                var combatEvent = batch.Events[index];
                if (combatEvent == null)
                {
                    continue;
                }

                switch (combatEvent.Kind)
                {
                    case RealtimeCombatEventKind.AttackStarted:
                        StartAttack(combatEvent, batch.ServerTick, tickRateHz);
                        break;
                    case RealtimeCombatEventKind.AttackCancelled:
                        CancelAttack(combatEvent);
                        break;
                    case RealtimeCombatEventKind.AttackReleased:
                        ReleaseAttack(combatEvent, batch.ServerTick, clock, tickRateHz);
                        break;
                }
            }
        }

        // The attacker's body plays its weapon's attack clip, timed to the release, and an archer
        // gets a shot whose arrow lies on the string once the drawing hand takes it.
        private void StartAttack(RealtimeCombatEvent combatEvent, uint batchServerTick, int tickRateHz)
        {
            DropShot(combatEvent.SourceEntityId);
            var body = BodyOf(combatEvent.SourceEntityId);
            if (body == null)
            {
                return;
            }

            body.PlayAttack(
                combatEvent.AttackStyle == RealtimeCombatAttackStyle.Melee,
                CombatProjectileRules.SecondsUntilRelease(combatEvent.Tick, batchServerTick, tickRateHz),
                views.TryGetView(combatEvent.TargetEntityId, out var target) ? target.Root : null);
            if (combatEvent.AttackStyle == RealtimeCombatAttackStyle.Ranged && body.Bow != null)
            {
                shots.Add(new Shot(combatEvent.SourceEntityId, body, Time.unscaledTime));
            }
        }

        private void CancelAttack(RealtimeCombatEvent combatEvent)
        {
            DropShot(combatEvent.SourceEntityId);
            var body = BodyOf(combatEvent.SourceEntityId);
            if (body != null)
            {
                body.StopAttack();
            }
        }

        // A creature bites, a bow looses, and a projectile flies: the arrow on the string from where
        // it lies, or one from the weapon's launch point.
        private void ReleaseAttack(RealtimeCombatEvent combatEvent, uint batchServerTick, CombatServerClock clock, int tickRateHz)
        {
            var shot = TakeShot(combatEvent.SourceEntityId);
            if (!views.TryGetView(combatEvent.SourceEntityId, out var source))
            {
                Recycle(shot);
                return;
            }

            var body = source.Root.GetComponentInChildren<PlayerBodyAppearance>();
            if (body != null)
            {
                body.Loose();
            }

            // A creature with clips of its own, such as a wolf, bites.
            var creature = source.Root.GetComponentInChildren<CreatureAnimator>();
            if (creature != null)
            {
                creature.PlayAttack();
            }

            if (!CombatProjectileRules.IsProjectile(combatEvent, batchServerTick)
                || flights.Count >= MaximumFlights
                || !views.TryGetView(combatEvent.TargetEntityId, out var target))
            {
                Recycle(shot);
                return;
            }

            var seconds = CombatProjectileRules.RemainingFlightSeconds(clock, combatEvent.Tick, batchServerTick, tickRateHz);
            if (seconds < CombatProjectileRules.MinimumFlightSeconds)
            {
                Recycle(shot);
                return;
            }

            var magic = combatEvent.AttackStyle == RealtimeCombatAttackStyle.Magic;
            var fromString = !magic && shot != null && shot.Arrow != null;
            if (!fromString)
            {
                Recycle(shot);
            }

            var projectile = magic ? AcquireBolt() : fromString ? shot.Arrow : AcquireArrow();
            var flight = new Flight(
                projectile,
                magic,
                body,
                combatEvent.SourceEntityId,
                combatEvent.TargetEntityId,
                target.Center,
                Time.unscaledTime,
                seconds);
            if (fromString)
            {
                flight.Start = shot.Point;
                flight.Up = shot.Up;
                flight.Launched = true;
            }
            else
            {
                projectile.SetActive(false);
            }

            flights.Add(flight);
            UpdateFlight(flights.Count - 1);
        }

        private PlayerBodyAppearance BodyOf(ulong entityId)
        {
            return views.TryGetView(entityId, out var view) ? view.Root.GetComponentInChildren<PlayerBodyAppearance>() : null;
        }

        // After the bodies posed and drew their strings this frame.
        private void LateUpdate()
        {
            for (var index = shots.Count - 1; index >= 0; index--)
            {
                UpdateShot(index);
            }

            for (var index = flights.Count - 1; index >= 0; index--)
            {
                UpdateFlight(index);
            }
        }

        // An arrow lies on the string while the drawing hand holds it, and once the bow looses it
        // waits where the string let go of it for its release, for a short time. An archer who stops
        // drawing takes it away.
        private void UpdateShot(int index)
        {
            var shot = shots[index];
            var now = Time.unscaledTime;
            if (shot.Body == null || now - shot.StartedAt > MaximumShotSeconds)
            {
                RemoveShot(index);
                return;
            }

            if (shot.Body.TryGetNockedArrow(out var nock, out var forward, out var up))
            {
                if (shot.Arrow == null)
                {
                    shot.Arrow = AcquireArrow();
                }

                shot.Point = nock + (forward * arrowLength);
                shot.Up = up;
                shot.LetGoAt = -1f;
                shot.Arrow.transform.SetPositionAndRotation(shot.Point, Quaternion.LookRotation(forward, up));
                shot.Arrow.SetActive(true);
                return;
            }

            if (shot.Arrow == null)
            {
                return;
            }

            var bow = shot.Body.Bow;
            if (bow == null || !bow.HasLoosed)
            {
                RemoveShot(index);
            }
            else if (shot.LetGoAt < 0f)
            {
                shot.LetGoAt = now;
            }
            else if (now - shot.LetGoAt > LooseWaitSeconds)
            {
                RemoveShot(index);
            }
        }

        // A projectile leaves from where its attacker holds its weapon at the moment it appears, or
        // from the string, and follows its target, so it lands where the target is presented.
        private void UpdateFlight(int index)
        {
            var flight = flights[index];
            if (flight.Projectile == null)
            {
                Release(index);
                return;
            }

            var now = Time.unscaledTime;
            if (views != null && views.TryGetView(flight.TargetEntityId, out var target))
            {
                flight.End = target.Center;
            }

            if (!flight.Launched)
            {
                flight.Start = LaunchPoint(flight);
                flight.Launched = true;
                flight.Projectile.SetActive(true);
            }

            var progress = Mathf.Clamp01((now - flight.StartedAt) / flight.Duration);
            if (progress >= 1f)
            {
                Release(index);
                return;
            }

            var direction = flight.End - flight.Start;
            flight.Projectile.transform.position = Vector3.Lerp(flight.Start, flight.End, progress);
            if (direction.sqrMagnitude > 0.0001f)
            {
                var up = Vector3.ProjectOnPlane(flight.Up, direction);
                flight.Projectile.transform.rotation = Quaternion.LookRotation(
                    direction,
                    up.sqrMagnitude > 0.0001f ? up : Vector3.up);
            }

            flights[index] = flight;
        }

        // The weapon's launch point, such as a staff's crystal, or a point at chest height.
        private Vector3 LaunchPoint(Flight flight)
        {
            if (flight.Body != null && flight.Body.TryGetLaunchPoint(out var point))
            {
                return point;
            }

            return views != null && views.TryGetView(flight.SourceEntityId, out var source)
                ? source.Position + (Vector3.up * (source.Height * LaunchHeightFraction))
                : flight.End;
        }

        // The shot of an attacker, out of the shots, with the arrow it shows.
        private Shot TakeShot(ulong sourceEntityId)
        {
            for (var index = 0; index < shots.Count; index++)
            {
                if (shots[index].SourceEntityId == sourceEntityId)
                {
                    var shot = shots[index];
                    shots.RemoveAt(index);
                    return shot;
                }
            }

            return null;
        }

        private void DropShot(ulong sourceEntityId)
        {
            Recycle(TakeShot(sourceEntityId));
        }

        private void RemoveShot(int index)
        {
            var shot = shots[index];
            shots.RemoveAt(index);
            Recycle(shot);
        }

        // A shot's arrow goes back to the idle arrows.
        private void Recycle(Shot shot)
        {
            if (shot == null || shot.Arrow == null)
            {
                return;
            }

            shot.Arrow.SetActive(false);
            idleArrows.Push(shot.Arrow);
            shot.Arrow = null;
        }

        private GameObject AcquireArrow()
        {
            GameObject arrow = null;
            while (arrow == null && idleArrows.Count > 0)
            {
                arrow = idleArrows.Pop();
            }

            return arrow != null ? arrow : CreateArrow();
        }

        private GameObject AcquireBolt()
        {
            GameObject bolt = null;
            while (bolt == null && idleBolts.Count > 0)
            {
                bolt = idleBolts.Pop();
            }

            return bolt != null ? bolt : CreateBolt();
        }

        // The arrow model without shadows, or a stretched cube without one.
        private GameObject CreateArrow()
        {
            if (arrowModel != null)
            {
                var model = Instantiate(arrowModel, transform);
                model.name = "CombatArrow";
                foreach (var modelRenderer in model.GetComponentsInChildren<Renderer>(true))
                {
                    modelRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    modelRenderer.receiveShadows = false;
                }

                return model;
            }

            var arrow = CreatePiece("CombatArrow", PrimitiveType.Cube, transform);
            arrow.transform.localScale = ArrowScale;
            var arrowRenderer = arrow.GetComponent<Renderer>();
            if (arrowMaterial == null)
            {
                arrowMaterial = new Material(arrowRenderer.sharedMaterial);
                arrowMaterial.SetColor("_BaseColor", ArrowColor);
                arrowMaterial.SetColor("_Color", ArrowColor);
            }

            arrowRenderer.sharedMaterial = arrowMaterial;
            return arrow;
        }

        // How far behind its root, its point, a model reaches along its forward axis.
        private static float LengthOf(GameObject model)
        {
            var length = 0f;
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null)
                {
                    continue;
                }

                var toModel = model.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                var bounds = filter.sharedMesh.bounds;
                for (var corner = 0; corner < 8; corner++)
                {
                    var offset = new Vector3(
                        (corner & 1) == 0 ? -bounds.extents.x : bounds.extents.x,
                        (corner & 2) == 0 ? -bounds.extents.y : bounds.extents.y,
                        (corner & 4) == 0 ? -bounds.extents.z : bounds.extents.z);
                    length = Mathf.Max(length, -toModel.MultiplyPoint3x4(bounds.center + offset).z);
                }
            }

            return length;
        }

        // A root with the core in front and the sparks behind it along its forward axis.
        private GameObject CreateBolt()
        {
            var bolt = new GameObject("CombatBolt");
            bolt.transform.SetParent(transform, false);
            for (var spark = 0; spark < BoltSparkScales.Length; spark++)
            {
                var piece = CreatePiece("Spark", PrimitiveType.Sphere, bolt.transform);
                piece.transform.localPosition = Vector3.back * (BoltSparkSpacing * spark);
                piece.transform.localScale = Vector3.one * BoltSparkScales[spark];
                var pieceRenderer = piece.GetComponent<Renderer>();
                if (boltMaterial == null)
                {
                    boltMaterial = new Material(pieceRenderer.sharedMaterial);
                    boltMaterial.SetColor("_BaseColor", BoltColor);
                    boltMaterial.SetColor("_Color", BoltColor);
                    boltMaterial.EnableKeyword("_EMISSION");
                    boltMaterial.SetColor("_EmissionColor", BoltEmission);
                    boltMaterial.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                }

                pieceRenderer.sharedMaterial = boltMaterial;
            }

            return bolt;
        }

        // A primitive without a collider and without shadows, so it never blocks clicks, the
        // camera, or the light.
        private GameObject CreatePiece(string pieceName, PrimitiveType type, Transform parent)
        {
            var piece = RuntimePrimitives.Create(type, primitiveMaterial);
            piece.name = pieceName;
            piece.transform.SetParent(parent, false);
            var pieceCollider = piece.GetComponent<Collider>();
            if (pieceCollider != null)
            {
                DestroyImmediate(pieceCollider);
            }

            var pieceRenderer = piece.GetComponent<Renderer>();
            pieceRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pieceRenderer.receiveShadows = false;
            return piece;
        }

        private void Release(int index)
        {
            var flight = flights[index];
            flights.RemoveAt(index);
            if (flight.Projectile == null)
            {
                return;
            }

            flight.Projectile.SetActive(false);
            (flight.Magic ? idleBolts : idleArrows).Push(flight.Projectile);
        }

        // An archer's attack whose arrow lies on the string, by the point of the arrow, while the
        // drawing hand holds it, and waits there once the string let go of it.
        private sealed class Shot
        {
            public Shot(ulong sourceEntityId, PlayerBodyAppearance body, float startedAt)
            {
                SourceEntityId = sourceEntityId;
                Body = body;
                StartedAt = startedAt;
            }

            public ulong SourceEntityId { get; }

            public PlayerBodyAppearance Body { get; }

            public float StartedAt { get; }

            public GameObject Arrow { get; set; }

            public Vector3 Point { get; set; }

            // The way the arrow's cock vane points.
            public Vector3 Up { get; set; } = Vector3.up;

            // When the string let go of it, or -1 while it lies on the string.
            public float LetGoAt { get; set; } = -1f;
        }

        private struct Flight
        {
            public Flight(
                GameObject projectile,
                bool magic,
                PlayerBodyAppearance body,
                ulong sourceEntityId,
                ulong targetEntityId,
                Vector3 end,
                float startedAt,
                float duration)
            {
                Projectile = projectile;
                Magic = magic;
                Body = body;
                SourceEntityId = sourceEntityId;
                TargetEntityId = targetEntityId;
                Start = end;
                End = end;
                Up = Vector3.up;
                StartedAt = startedAt;
                Duration = Mathf.Max(CombatProjectileRules.MinimumFlightSeconds, duration);
                Launched = false;
            }

            public GameObject Projectile { get; }

            public bool Magic { get; }

            // The attacker's body, whose weapon names where the projectile leaves, or null.
            public PlayerBodyAppearance Body { get; }

            public ulong SourceEntityId { get; }

            public ulong TargetEntityId { get; }

            public Vector3 Start { get; set; }

            public Vector3 End { get; set; }

            // The way the projectile's up axis turns toward, such as an arrow's cock vane.
            public Vector3 Up { get; set; }

            public float StartedAt { get; }

            public float Duration { get; }

            // Whether it appeared and took its start from the attacker.
            public bool Launched { get; set; }
        }
    }
}
