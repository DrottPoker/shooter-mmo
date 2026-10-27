using System.Collections.Generic;
using Mmorpg.Client.Core;
using Mmorpg.Client.Gameplay;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using UnityEngine;

namespace Mmorpg.Client.Combat
{
    // When a released attack shows a projectile, how long it still flies, and when it leaves.
    public static class CombatProjectileRules
    {
        // Shorter flights are not worth drawing, such as an arrow that already landed.
        public const float MinimumFlightSeconds = 0.05f;

        // A magic bolt leaves the staff when its attack clip thrusts the staff forward, but never
        // later than halfway through its flight.
        public const float MagicLaunchDelaySeconds = 0.15f;

        // A projectile lands after the tick of its release; a Mob bite resolves at once.
        public static bool IsProjectile(RealtimeCombatEvent combatEvent, uint batchServerTick)
        {
            return combatEvent != null
                && combatEvent.Kind == RealtimeCombatEventKind.AttackReleased
                && unchecked((int)(combatEvent.ImpactTick - batchServerTick)) > 0;
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

        // How long after the release the projectile appears; it still lands at its impact tick.
        public static float LaunchDelaySeconds(RealtimeCombatAttackStyle style, float flightSeconds)
        {
            return style == RealtimeCombatAttackStyle.Magic
                ? Mathf.Clamp(flightSeconds * 0.5f, 0f, MagicLaunchDelaySeconds)
                : 0f;
        }
    }

    // Replaceable presentation of released attacks. The attacker's body plays its weapon's attack
    // clip, such as Cast for a staff, and a projectile flies from the attacker to the target and
    // lands at the event's impact tick: a simple arrow for a ranged attack, and a glowing bolt
    // from the weapon's launch point, such as a staff's crystal, for a magic one. It decides
    // nothing; damage comes from Hit events and health from the combat states.
    [DisallowMultipleComponent]
    public sealed class CombatProjectilePresentation : MonoBehaviour
    {
        private const int MaximumFlights = 32;
        private const float LaunchHeightFraction = 0.7f;
        private static readonly Vector3 ArrowScale = new Vector3(0.05f, 0.05f, 0.7f);
        private static readonly Color ArrowColor = new Color(0.93f, 0.89f, 0.78f, 1f);

        // A bolt is a bright core with two fading sparks trailing it, in an arcane blue-white.
        private static readonly float[] BoltSparkScales = { 0.16f, 0.1f, 0.06f };
        private const float BoltSparkSpacing = 0.12f;
        private static readonly Color BoltColor = new Color(0.78f, 0.94f, 1f, 1f);
        private static readonly Color BoltEmission = new Color(0.45f, 0.8f, 1f, 1f) * 2.5f;

        private readonly List<Flight> flights = new List<Flight>();
        private readonly Stack<GameObject> idleArrows = new Stack<GameObject>();
        private readonly Stack<GameObject> idleBolts = new Stack<GameObject>();
        private CombatClientController combatController;
        private RealtimeSimulationClient realtimeClient;
        private CombatantViewLocator views;
        private Material primitiveMaterial;
        private Material arrowMaterial;
        private Material boltMaterial;
        private bool subscribed;

        public int ActiveFlightCount
        {
            get { return flights.Count; }
        }

        // Called by the composition root before the component is activated.
        public void Initialize(
            CombatClientController combat,
            RealtimeSimulationClient realtime,
            CombatantViewLocator combatantViews,
            Material runtimePrimitiveMaterial)
        {
            combatController = combat;
            realtimeClient = realtime;
            views = combatantViews;
            primitiveMaterial = runtimePrimitiveMaterial;
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
                if (combatEvent == null
                    || combatEvent.Kind != RealtimeCombatEventKind.AttackReleased
                    || !views.TryGetView(combatEvent.SourceEntityId, out var source))
                {
                    continue;
                }

                var body = source.Root.GetComponentInChildren<PlayerBodyAppearance>();
                if (body != null)
                {
                    body.PlayAttack();
                }

                if (!CombatProjectileRules.IsProjectile(combatEvent, batch.ServerTick)
                    || flights.Count >= MaximumFlights
                    || !views.TryGetView(combatEvent.TargetEntityId, out var target))
                {
                    continue;
                }

                var seconds = CombatProjectileRules.RemainingFlightSeconds(
                    clock,
                    combatEvent.ImpactTick,
                    batch.ServerTick,
                    tickRateHz);
                if (seconds < CombatProjectileRules.MinimumFlightSeconds)
                {
                    continue;
                }

                var magic = combatEvent.AttackStyle == RealtimeCombatAttackStyle.Magic;
                var delay = CombatProjectileRules.LaunchDelaySeconds(combatEvent.AttackStyle, seconds);
                var projectile = magic ? AcquireBolt() : AcquireArrow();
                projectile.SetActive(false);
                flights.Add(new Flight(
                    projectile,
                    magic,
                    body,
                    combatEvent.SourceEntityId,
                    combatEvent.TargetEntityId,
                    target.Center,
                    Time.unscaledTime + delay,
                    seconds - delay));
                UpdateFlight(flights.Count - 1);
            }
        }

        private void LateUpdate()
        {
            for (var index = flights.Count - 1; index >= 0; index--)
            {
                UpdateFlight(index);
            }
        }

        // A projectile leaves from where its attacker holds its weapon at the moment it appears and
        // follows its target, so it lands where the target is presented.
        private void UpdateFlight(int index)
        {
            var flight = flights[index];
            if (flight.Projectile == null)
            {
                Release(index);
                return;
            }

            var now = Time.unscaledTime;
            if (now < flight.StartedAt)
            {
                return;
            }

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
                flight.Projectile.transform.rotation = Quaternion.LookRotation(direction);
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

        private GameObject CreateArrow()
        {
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

            public float StartedAt { get; }

            public float Duration { get; }

            // Whether it appeared and took its start from the attacker.
            public bool Launched { get; set; }
        }
    }
}
