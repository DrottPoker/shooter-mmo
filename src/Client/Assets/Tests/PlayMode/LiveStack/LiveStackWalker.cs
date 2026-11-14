using System;
using System.Collections;
using System.Collections.Generic;
using Mmorpg.Client.Api;
using Mmorpg.Client.Core;
using Mmorpg.Client.Networking;
using Mmorpg.Client.Ui;
using Mmorpg.GameProtocol;
using Mmorpg.GameSimulation;
using UnityEngine;

namespace Mmorpg.Client.Tests.PlayMode
{
    // Another player for the network part of the live stack test: a second connection with its
    // own character that walks back and forth beside the spawn for as long as it runs, so the
    // client under test always watches a remote player that moves. It joins on the worker's own
    // port when the script names one, so only the watched client's line is impaired. It sends
    // movement input as the local player does: one input per simulation tick, the newest batch
    // each time, with the route until an owner snapshot confirms it.
    internal sealed class LiveStackWalker : IDisposable
    {
        // The two ends of the walk, from the spawn: ten meters along X, three meters north.
        private const float HalfLegMeters = 5f;
        private const float NorthMeters = 3f;
        private const float ArrivalMeters = 0.3f;
        private const float LegTimeoutSeconds = 8f;

        private readonly ApiClient api;
        private readonly string baseUrl;
        private readonly GameObject hubObject;
        private readonly RealtimeSimulationClient hub;
        private readonly List<PlayerMovementInput> pending = new List<PlayerMovementInput>();
        private ActiveSimulationSessionResponse joined;
        private RealtimeOwnerSimulationSnapshot latest;
        private uint confirmedPathId;
        private Coroutine walking;
        private string failure;

        public LiveStackWalker(ApiClient api, string baseUrl, ClientSettings settings)
        {
            this.api = api;
            this.baseUrl = baseUrl;
            // The watched client's scene loads must not end the other player.
            hubObject = new GameObject("Live stack walker");
            UnityEngine.Object.DontDestroyOnLoad(hubObject);
            hubObject.SetActive(false);
            hub = hubObject.AddComponent<RealtimeSimulationClient>();
            hub.Initialize(settings);
            hubObject.SetActive(true);
            hub.Movement.OwnerSimulationSnapshotReceived += OnSnapshot;
        }

        public int Legs { get; private set; }

        // Why the walk stopped early, or null while it walks or after it was stopped.
        public string Failure
        {
            get { return hub == null ? "the walker's hub was destroyed" : failure; }
        }

        // Creates an account and a character, as the entry screens do.
        public static IEnumerator CreateAccountAndCharacter(
            ApiClient api,
            string baseUrl,
            string prefix,
            Action<AuthResponse, CharacterResponse> created)
        {
            // A random name, so reruns against the same database never collide.
            var suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            var username = prefix.ToLowerInvariant() + "_" + suffix;
            AuthResponse account = null;
            ApiError error = null;
            yield return api.Register(
                baseUrl,
                new RegisterAccountRequest
                {
                    email = username + "@example.test",
                    username = username,
                    password = prefix.ToLowerInvariant() + "-" + suffix
                },
                value => account = value,
                value => error = value);
            ThrowOnError(error, "registering the account");

            CharacterAppearanceCatalogResponse appearanceCatalog = null;
            yield return api.GetCharacterAppearanceCatalog(baseUrl, account.sessionToken, value => appearanceCatalog = value, value => error = value);
            ThrowOnError(error, "reading what character creation offers");
            var look = new CharacterCreationLook(appearanceCatalog);
            CharacterResponse character = null;
            yield return api.CreateCharacter(
                baseUrl,
                account.sessionToken,
                new CreateCharacterRequest { name = prefix + " " + suffix.Substring(0, 10), appearance = look.ToRequest() },
                value => character = value,
                value => error = value);
            ThrowOnError(error, "creating the character");
            created(account, character);
        }

        // Joins with a new character on the worker the script started, on directPort when it is
        // above zero, and starts walking.
        public IEnumerator JoinAndWalk(string shardId, string expectedRuntime, int directPort)
        {
            AuthResponse account = null;
            CharacterResponse character = null;
            yield return CreateAccountAndCharacter(api, baseUrl, "Walker", (value, created) =>
            {
                account = value;
                character = created;
            });

            JoinShardResponse ticket = null;
            var deadline = Time.realtimeSinceStartup + 90f;
            while (ticket == null)
            {
                JoinShardResponse result = null;
                ApiError error = null;
                yield return api.CreateJoinTicket(
                    baseUrl,
                    account.sessionToken,
                    shardId,
                    new JoinShardRequest { characterId = character.id },
                    value => result = value,
                    value => error = value);
                ThrowOnError(error, "asking for the walker's join ticket");
                if (string.IsNullOrEmpty(expectedRuntime) || result.endpoint.runtimeId == expectedRuntime)
                {
                    ticket = result;
                }
                else if (Time.realtimeSinceStartup > deadline)
                {
                    throw new InvalidOperationException("GameService places the Shard on runtime " + result.endpoint.runtimeId + ".");
                }
                else
                {
                    yield return new WaitForSecondsRealtime(1f);
                }
            }

            if (directPort > 0)
            {
                ticket.endpoint.udpPort = directPort;
            }

            RealtimeClientError joinError = null;
            yield return hub.Session.Join(ticket, value => joined = value, value => joinError = value);
            if (joinError != null || joined == null)
            {
                throw new InvalidOperationException("The walker could not join: "
                    + (joinError != null ? joinError.ToDiagnostic() : "no session"));
            }

            walking = hub.StartCoroutine(Walk());
        }

        // Stops walking and leaves cleanly.
        public IEnumerator Leave()
        {
            StopWalking();
            if (joined == null || hub == null || !hub.Session.IsJoined)
            {
                yield break;
            }

            yield return hub.Session.Leave(joined.simulationSessionId, _ => { }, _ => { });
        }

        public void Dispose()
        {
            StopWalking();
            if (hub == null)
            {
                return;
            }

            hub.Movement.OwnerSimulationSnapshotReceived -= OnSnapshot;
            if (hub.Session.State != RealtimeConnectionState.Disconnected)
            {
                hub.Session.Abort();
            }

            UnityEngine.Object.Destroy(hubObject);
        }

        private void StopWalking()
        {
            if (walking != null && hub != null)
            {
                hub.StopCoroutine(walking);
            }

            walking = null;
        }

        private void OnSnapshot(RealtimeOwnerSimulationSnapshot snapshot)
        {
            latest = snapshot;
            pending.RemoveAll(input => !MovementSequence.IsNewer(input.InputSequence, snapshot.LastProcessedInputSequence));
            confirmedPathId = snapshot.PathProgress.PathId;
        }

        // Walks between the two ends until stopped. Failures end the walk with a reason instead
        // of an exception, because the walk runs beside the test.
        private IEnumerator Walk()
        {
            var deadline = Time.realtimeSinceStartup + 10f;
            while (latest == null)
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    failure = "no owner snapshot";
                    yield break;
                }

                yield return null;
            }

            var session = hub.Movement.MovementSession;
            var planner = new MovementPathPlanner(session.CollisionWorld, session.Settings, MovementPathPlannerSettings.Default);
            var origin = PositionOf(latest);
            var ends = new[]
            {
                new SimulationVector3(origin.X + HalfLegMeters, origin.Y, origin.Z + NorthMeters),
                new SimulationVector3(origin.X - HalfLegMeters, origin.Y, origin.Z + NorthMeters)
            };
            var tickSeconds = session.Settings.FixedDeltaTime;
            var accumulator = tickSeconds;
            var sequence = 0u;
            MovementPath path = null;
            var destination = default(SimulationVector3);
            var legStarted = 0f;
            while (hub.Session.IsJoined)
            {
                var position = PositionOf(latest);
                if (path == null
                    || HorizontalDistance(position, destination) <= ArrivalMeters
                    || Time.realtimeSinceStartup - legStarted > LegTimeoutSeconds)
                {
                    var end = ends[Legs % ends.Length];
                    if (!session.TryEnsureCollisionChunks(new[] { position, end }, out var chunkError))
                    {
                        failure = "no collision for the walk: " + chunkError;
                        yield break;
                    }

                    var plan = planner.Plan(position, end);
                    if (plan.Status == MovementPathPlanStatus.NoPath || plan.Waypoints.Count == 0)
                    {
                        failure = "no route between the ends of the walk";
                        yield break;
                    }

                    path = new MovementPath((uint)(Legs + 1), plan.Waypoints);
                    destination = plan.Waypoints[plan.Waypoints.Count - 1];
                    legStarted = Time.realtimeSinceStartup;
                    Legs++;
                }

                while (accumulator >= tickSeconds)
                {
                    accumulator -= tickSeconds;
                    sequence++;
                    pending.Add(new PlayerMovementInput(sequence, sequence, path));
                    var count = Math.Min(pending.Count, RealtimeProtocol.MaximumInputBatchSize);
                    var batch = pending.GetRange(pending.Count - count, count).ToArray();
                    if (!hub.Movement.TrySendMovementInputs(batch, confirmedPathId == path.Id ? null : path))
                    {
                        failure = "the movement input could not be sent";
                        yield break;
                    }
                }

                yield return null;
                accumulator += Mathf.Min(Time.unscaledDeltaTime, 0.25f);
            }

            failure = "the walker's session ended";
        }

        private static SimulationVector3 PositionOf(RealtimeOwnerSimulationSnapshot snapshot)
        {
            return new SimulationVector3(snapshot.State.PositionX, snapshot.State.PositionY, snapshot.State.PositionZ);
        }

        private static float HorizontalDistance(SimulationVector3 first, SimulationVector3 second)
        {
            var x = first.X - second.X;
            var z = first.Z - second.Z;
            return Mathf.Sqrt((x * x) + (z * z));
        }

        private static void ThrowOnError(ApiError error, string what)
        {
            if (error != null)
            {
                throw new InvalidOperationException(what + " failed: " + error.ToDiagnostic());
            }
        }
    }
}
