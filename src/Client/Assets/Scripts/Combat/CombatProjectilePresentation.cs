using System.Collections.Generic;
using Mmorpg.Client.Networking;
using Mmorpg.GameProtocol;
using UnityEngine;

namespace Mmorpg.Client.Combat
{
    // When a released attack shows a projectile and how long it still flies.
    public static class CombatProjectileRules
    {
        // Shorter flights are not worth drawing, such as an arrow that already landed.
        public const float MinimumFlightSeconds = 0.05f;

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
    }

    // Replaceable presentation: a simple arrow flies from the attacker to the target of
    // every released ranged attack and lands at the event's impact tick. It decides
    // nothing; damage comes from Hit events and health from the combat states.
    [DisallowMultipleComponent]
    public sealed class CombatProjectilePresentation : MonoBehaviour
    {
        private const int MaximumFlights = 32;
        private const float LaunchHeightFraction = 0.7f;
        private static readonly Vector3 ArrowScale = new Vector3(0.05f, 0.05f, 0.7f);
        private static readonly Color ArrowColor = new Color(0.93f, 0.89f, 0.78f, 1f);

        private readonly List<Flight> flights = new List<Flight>();
        private readonly Stack<GameObject> idleArrows = new Stack<GameObject>();
        private CombatClientController combatController;
        private RealtimeSimulationClient realtimeClient;
        private CombatantViewLocator views;
        private Material arrowMaterial;
        private bool subscribed;

        public int ActiveFlightCount
        {
            get { return flights.Count; }
        }

        // Called by the composition root before the component is activated.
        public void Initialize(
            CombatClientController combat,
            RealtimeSimulationClient realtime,
            CombatantViewLocator combatantViews)
        {
            combatController = combat;
            realtimeClient = realtime;
            views = combatantViews;
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
            while (idleArrows.Count > 0)
            {
                var arrow = idleArrows.Pop();
                if (arrow != null)
                {
                    Destroy(arrow);
                }
            }

            if (arrowMaterial != null)
            {
                Destroy(arrowMaterial);
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
                if (!CombatProjectileRules.IsProjectile(combatEvent, batch.ServerTick)
                    || flights.Count >= MaximumFlights
                    || !views.TryGetView(combatEvent.SourceEntityId, out var source)
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

                var start = source.Position + (Vector3.up * (source.Height * LaunchHeightFraction));
                flights.Add(new Flight(
                    AcquireArrow(),
                    combatEvent.TargetEntityId,
                    start,
                    target.Center,
                    Time.unscaledTime,
                    seconds));
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

        // The arrow follows its target, so it lands where the target is presented.
        private void UpdateFlight(int index)
        {
            var flight = flights[index];
            if (views != null && views.TryGetView(flight.TargetEntityId, out var target))
            {
                flight.End = target.Center;
            }

            var progress = Mathf.Clamp01((Time.unscaledTime - flight.StartedAt) / flight.Duration);
            if (progress >= 1f || flight.Arrow == null)
            {
                Release(index);
                return;
            }

            var direction = flight.End - flight.Start;
            flight.Arrow.transform.position = Vector3.Lerp(flight.Start, flight.End, progress);
            if (direction.sqrMagnitude > 0.0001f)
            {
                flight.Arrow.transform.rotation = Quaternion.LookRotation(direction);
            }

            flights[index] = flight;
        }

        private GameObject AcquireArrow()
        {
            GameObject arrow = null;
            while (arrow == null && idleArrows.Count > 0)
            {
                arrow = idleArrows.Pop();
            }

            if (arrow == null)
            {
                arrow = CreateArrow();
            }

            arrow.SetActive(true);
            return arrow;
        }

        private GameObject CreateArrow()
        {
            var arrow = GameObject.CreatePrimitive(PrimitiveType.Cube);
            arrow.name = "CombatArrow";
            arrow.transform.SetParent(transform, false);
            arrow.transform.localScale = ArrowScale;
            // The arrow must never block clicks or the camera, so its collider goes at once.
            var arrowCollider = arrow.GetComponent<Collider>();
            if (arrowCollider != null)
            {
                DestroyImmediate(arrowCollider);
            }

            var arrowRenderer = arrow.GetComponent<Renderer>();
            if (arrowMaterial == null)
            {
                arrowMaterial = new Material(arrowRenderer.sharedMaterial);
                arrowMaterial.SetColor("_BaseColor", ArrowColor);
                arrowMaterial.SetColor("_Color", ArrowColor);
            }

            arrowRenderer.sharedMaterial = arrowMaterial;
            arrowRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            arrowRenderer.receiveShadows = false;
            return arrow;
        }

        private void Release(int index)
        {
            var arrow = flights[index].Arrow;
            flights.RemoveAt(index);
            if (arrow != null)
            {
                arrow.SetActive(false);
                idleArrows.Push(arrow);
            }
        }

        private struct Flight
        {
            public Flight(
                GameObject arrow,
                ulong targetEntityId,
                Vector3 start,
                Vector3 end,
                float startedAt,
                float duration)
            {
                Arrow = arrow;
                TargetEntityId = targetEntityId;
                Start = start;
                End = end;
                StartedAt = startedAt;
                Duration = Mathf.Max(CombatProjectileRules.MinimumFlightSeconds, duration);
            }

            public GameObject Arrow { get; }

            public ulong TargetEntityId { get; }

            public Vector3 Start { get; }

            public Vector3 End { get; set; }

            public float StartedAt { get; }

            public float Duration { get; }
        }
    }
}
