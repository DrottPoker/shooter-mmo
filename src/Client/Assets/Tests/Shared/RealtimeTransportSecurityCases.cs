#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using Mmorpg.GameProtocol;

namespace Mmorpg.SharedTests
{
    // The cases of the sealed realtime transport, compiled into the Unity EditMode tests, the
    // IL2CPP test player, and the .NET tests, so every runtime seals, opens, and proves exactly
    // alike. Every Verify method returns one line per failure, so a test runner only asserts that
    // the list is empty. The RFC vectors check the algorithms; the pinned vectors check that the
    // key schedule, the proof, and the datagram format stay the same on every runtime.
    public static class RealtimeTransportSecurityCases
    {
        // RFC 8439 section 2.8.2.
        private const string Rfc8439Key = "808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f";
        private const string Rfc8439Nonce = "070000004041424344454647";
        private const string Rfc8439AssociatedData = "50515253c0c1c2c3c4c5c6c7";
        private const string Rfc8439Plaintext =
            "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.";
        private const string Rfc8439Ciphertext =
            "d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d6"
            + "3dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b36"
            + "92ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc"
            + "3ff4def08e4b7a9de576d26586cec64b6116";
        private const string Rfc8439Tag = "1ae10b594f09e26a7e902ecbd0600691";

        // The key schedule, the proof, and one sealed datagram for fixed inputs.
        private const string PinnedProofKey = "72c715c2fa40f2aa90405d5df4de846766c9f0465561483204d24acb75b9d87e";
        private const string PinnedClientToWorkerKey = "bdf27d5603918cffd1b26099fe305936e87993b24b68c28b6a5dc14073322325";
        private const string PinnedWorkerToClientKey = "1473d4b86055ec2128776f379d461c1b16ed5dd39fa27961a728cc254f5450b7";
        private const string PinnedProof = "f96f5113b06e331b3e76d9bb7a43176b181d5d57929d800af697c50309c12517";
        private const string PinnedSealedDatagram =
            "010000000000000000a95da5d28f928a2366f7665b7ae4ebd96b5b9b09026459d821bb5a56ef71d4";

        public static IReadOnlyList<string> VerifyAeadVectors(Func<byte[], IRealtimeAead> create)
        {
            var failures = new List<string>();
            var key = FromHex(Rfc8439Key);
            var nonce = FromHex(Rfc8439Nonce);
            var associatedData = FromHex(Rfc8439AssociatedData);
            var plaintext = Encoding.ASCII.GetBytes(Rfc8439Plaintext);
            using (var aead = create(key))
            {
                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[RealtimeAead.TagSize];
                aead.Seal(nonce, plaintext, ciphertext, tag, associatedData);
                Expect(failures, "RFC 8439 2.8.2 ciphertext", Hex(ciphertext), Rfc8439Ciphertext);
                Expect(failures, "RFC 8439 2.8.2 tag", Hex(tag), Rfc8439Tag);

                var opened = new byte[ciphertext.Length];
                if (!aead.TryOpen(nonce, ciphertext, tag, opened, associatedData)
                    || !opened.SequenceEqual(plaintext))
                {
                    failures.Add("RFC 8439 2.8.2 does not open to its plaintext.");
                }

                // In place, as the datagram layer seals and opens.
                var inPlace = (byte[])plaintext.Clone();
                var inPlaceTag = new byte[RealtimeAead.TagSize];
                aead.Seal(nonce, inPlace, inPlace, inPlaceTag, associatedData);
                Expect(failures, "RFC 8439 2.8.2 sealed in place", Hex(inPlace), Rfc8439Ciphertext);
                if (!aead.TryOpen(nonce, inPlace, inPlaceTag, inPlace, associatedData) || !inPlace.SequenceEqual(plaintext))
                {
                    failures.Add("RFC 8439 2.8.2 does not open in place.");
                }

                ExpectRefused(failures, aead, "a tampered tag", nonce, ciphertext, Flip(tag, 0), associatedData);
                ExpectRefused(failures, aead, "a tampered ciphertext", nonce, Flip(ciphertext, 57), tag, associatedData);
                ExpectRefused(failures, aead, "a tampered nonce", Flip(nonce, 0), ciphertext, tag, associatedData);
                ExpectRefused(failures, aead, "tampered associated data", nonce, ciphertext, tag, Flip(associatedData, 11));

                // Empty plaintext and empty associated data still authenticate.
                var emptyTag = new byte[RealtimeAead.TagSize];
                aead.Seal(nonce, Array.Empty<byte>(), Array.Empty<byte>(), emptyTag, Array.Empty<byte>());
                if (!aead.TryOpen(nonce, Array.Empty<byte>(), emptyTag, Array.Empty<byte>(), Array.Empty<byte>()))
                {
                    failures.Add("An empty message does not open.");
                }
            }

            using (var wrongKey = create(Flip(key, 31)))
            {
                var ciphertext = FromHex(Rfc8439Ciphertext);
                var opened = new byte[ciphertext.Length];
                if (wrongKey.TryOpen(nonce, ciphertext, FromHex(Rfc8439Tag), opened, associatedData))
                {
                    failures.Add("RFC 8439 2.8.2 opens with a wrong key.");
                }
            }

            return failures;
        }

        // Seals the same random inputs with both implementations and opens each with the other.
        public static IReadOnlyList<string> VerifyImplementationsAgree(
            Func<byte[], IRealtimeAead> first,
            Func<byte[], IRealtimeAead> second,
            int seed,
            int rounds)
        {
            var failures = new List<string>();
            var random = new Random(seed);
            for (var round = 0; round < rounds && failures.Count < 10; round++)
            {
                var key = RandomBytes(random, RealtimeAead.KeySize);
                var nonce = RandomBytes(random, RealtimeAead.NonceSize);
                var plaintext = RandomBytes(random, random.Next(0, 1300));
                var associatedData = RandomBytes(random, random.Next(0, 40));
                using (var a = first(key))
                using (var b = second(key))
                {
                    var ciphertextA = new byte[plaintext.Length];
                    var tagA = new byte[RealtimeAead.TagSize];
                    var ciphertextB = new byte[plaintext.Length];
                    var tagB = new byte[RealtimeAead.TagSize];
                    a.Seal(nonce, plaintext, ciphertextA, tagA, associatedData);
                    b.Seal(nonce, plaintext, ciphertextB, tagB, associatedData);
                    if (!ciphertextA.SequenceEqual(ciphertextB) || !tagA.SequenceEqual(tagB))
                    {
                        failures.Add($"Round {round}: {a.ImplementationName} and {b.ImplementationName} seal {plaintext.Length} bytes differently.");
                        continue;
                    }

                    var openedByB = new byte[plaintext.Length];
                    var openedByA = new byte[plaintext.Length];
                    if (!b.TryOpen(nonce, ciphertextA, tagA, openedByB, associatedData)
                        || !a.TryOpen(nonce, ciphertextB, tagB, openedByA, associatedData)
                        || !openedByA.SequenceEqual(plaintext)
                        || !openedByB.SequenceEqual(plaintext))
                    {
                        failures.Add($"Round {round}: one implementation does not open what the other sealed.");
                    }
                }
            }

            return failures;
        }

        // RFC 5869 test cases 1 and 3.
        public static IReadOnlyList<string> VerifyHkdfVectors()
        {
            var failures = new List<string>();
            var inputKeyMaterial = Enumerable.Repeat((byte)0x0b, 22).ToArray();
            var salt = FromHex("000102030405060708090a0b0c");
            var info = FromHex("f0f1f2f3f4f5f6f7f8f9");
            Expect(
                failures,
                "RFC 5869 case 1 PRK",
                Hex(RealtimeHkdf.Extract(salt, inputKeyMaterial)),
                "077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5");
            Expect(
                failures,
                "RFC 5869 case 1 OKM",
                Hex(RealtimeHkdf.DeriveKey(inputKeyMaterial, 42, salt, info)),
                "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865");
            Expect(
                failures,
                "RFC 5869 case 3 OKM",
                Hex(RealtimeHkdf.DeriveKey(inputKeyMaterial, 42, null, null)),
                "8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8");
            return failures;
        }

        // The three keys of a fixed transport key, the proof of a fixed join request, and one
        // sealed datagram, as every runtime must produce them.
        public static IReadOnlyList<string> VerifyPinnedVectors()
        {
            var failures = new List<string>();
            using (var keys = RealtimeTransportKeys.Derive(PinnedTransportKey()))
            {
                Expect(failures, "proof key", Hex(keys.ProofKey), PinnedProofKey);
                Expect(failures, "client-to-worker key", Hex(keys.ClientToWorkerKey), PinnedClientToWorkerKey);
                Expect(failures, "worker-to-client key", Hex(keys.WorkerToClientKey), PinnedWorkerToClientKey);
                Expect(failures, "proof key alone", Hex(RealtimeTransportKeys.DeriveProofKey(PinnedTransportKey())), PinnedProofKey);
                Expect(failures, "proof", Hex(PinnedProofFor(keys.ProofKey)), PinnedProof);

                using (var session = RealtimeSealedSession.ForClient(keys, RealtimeManagedOnly))
                {
                    var buffer = new byte[64];
                    var payload = Encoding.ASCII.GetBytes("pinned datagram");
                    payload.CopyTo(buffer, 0);
                    var length = payload.Length;
                    session.TrySeal(buffer, 0, ref length);
                    Expect(failures, "sealed datagram", Hex(buffer.Take(length).ToArray()), PinnedSealedDatagram);
                }
            }

            return failures;
        }

        // The join proof: the right proof verifies; another key, nonce, ticket, Shard, version,
        // or proof byte does not, and neither does a proof or nonce of the wrong size.
        public static IReadOnlyList<string> VerifyJoinProof()
        {
            var failures = new List<string>();
            using (var keys = RealtimeTransportKeys.Derive(PinnedTransportKey()))
            using (var otherKeys = RealtimeTransportKeys.Derive(RealtimeTransportKeys.CreateTransportKey()))
            {
                var ticketId = TicketId();
                var nonce = Nonce();
                var proof = RealtimeJoinProof.Compute(keys.ProofKey, RealtimeProtocol.Version, ticketId, "local-shard-1", nonce);
                ExpectProof(failures, "the right proof", true, keys.ProofKey, RealtimeProtocol.Version, ticketId, "local-shard-1", nonce, proof);
                ExpectProof(failures, "another key", false, otherKeys.ProofKey, RealtimeProtocol.Version, ticketId, "local-shard-1", nonce, proof);
                ExpectProof(failures, "another version", false, keys.ProofKey, (ushort)(RealtimeProtocol.Version + 1), ticketId, "local-shard-1", nonce, proof);
                ExpectProof(failures, "another ticket", false, keys.ProofKey, RealtimeProtocol.Version, Guid.NewGuid(), "local-shard-1", nonce, proof);
                ExpectProof(failures, "another Shard", false, keys.ProofKey, RealtimeProtocol.Version, ticketId, "local-shard-2", nonce, proof);
                ExpectProof(failures, "another nonce", false, keys.ProofKey, RealtimeProtocol.Version, ticketId, "local-shard-1", Flip(nonce, 3), proof);
                ExpectProof(failures, "a flipped proof bit", false, keys.ProofKey, RealtimeProtocol.Version, ticketId, "local-shard-1", nonce, Flip(proof, 31));
                ExpectProof(failures, "a short proof", false, keys.ProofKey, RealtimeProtocol.Version, ticketId, "local-shard-1", nonce, proof.Take(31).ToArray());
                ExpectProof(failures, "a short nonce", false, keys.ProofKey, RealtimeProtocol.Version, ticketId, "local-shard-1", nonce.Take(15).ToArray(), proof);
                ExpectProof(failures, "no proof", false, keys.ProofKey, RealtimeProtocol.Version, ticketId, "local-shard-1", nonce, null);

                var request = RealtimeJoinProof.CreateRequest(keys.ProofKey, ticketId, "local-shard-1");
                ExpectProof(failures, "a created request", true, keys.ProofKey, RealtimeProtocol.Version, request.TicketId, "local-shard-1", request.ClientNonce, request.Proof);
                if (RealtimeJoinProof.CreateRequest(keys.ProofKey, ticketId, "local-shard-1").ClientNonce.SequenceEqual(request.ClientNonce))
                {
                    failures.Add("Two join requests share a nonce.");
                }
            }

            return failures;
        }

        // The replay window accepts every counter once, in any order within 1,024 of the highest,
        // and refuses a repeated one or an older one.
        public static IReadOnlyList<string> VerifyReplayWindow()
        {
            var failures = new List<string>();
            var window = new RealtimeReplayWindow();
            ExpectWindow(failures, window, 5, true);
            ExpectWindow(failures, window, 5, false);
            ExpectWindow(failures, window, 3, true);
            ExpectWindow(failures, window, 4, true);
            ExpectWindow(failures, window, 3, false);
            ExpectWindow(failures, window, 2000, true);
            ExpectWindow(failures, window, 5, false);
            ExpectWindow(failures, window, 2000 - 1023, true);
            ExpectWindow(failures, window, 2000 - 1024, false);
            ExpectWindow(failures, window, 1999, true);
            ExpectWindow(failures, window, 1999, false);

            // The largest counters a peer can name: the window takes each once and ends.
            var last = new RealtimeReplayWindow();
            ExpectWindow(failures, last, ulong.MaxValue - 2, true);
            ExpectWindow(failures, last, ulong.MaxValue, true);
            ExpectWindow(failures, last, ulong.MaxValue, false);
            ExpectWindow(failures, last, ulong.MaxValue - 1, true);
            ExpectWindow(failures, last, ulong.MaxValue - 2, false);
            ExpectWindow(failures, last, ulong.MaxValue - 1024, false);

            // A run of 10,000 counters, shuffled within blocks of 500, so none arrives more than
            // 500 places from its own: every one is accepted once, and none twice.
            var random = new Random(7);
            var order = Enumerable.Range(0, 10000).Select(value => (ulong)value).ToArray();
            for (var block = 0; block < order.Length; block += 500)
            {
                for (var index = block + 499; index > block; index--)
                {
                    var other = random.Next(block, index + 1);
                    var swap = order[index];
                    order[index] = order[other];
                    order[other] = swap;
                }
            }

            var shuffled = new RealtimeReplayWindow();
            foreach (var counter in order)
            {
                if (!shuffled.Check(counter))
                {
                    failures.Add($"The reordered counter {counter} was refused.");
                    break;
                }

                shuffled.Accept(counter);
            }

            if (order.Any(shuffled.Check))
            {
                failures.Add("A counter of the reordered run was accepted twice.");
            }

            return failures;
        }

        // The client sends clear until the worker's first sealed datagram, then seals; the worker
        // seals once it installed the keys and drops clear datagrams from then on. Replays,
        // datagrams of another session, garbage, and clear packets that carry a channel are
        // dropped and counted.
        public static IReadOnlyList<string> VerifyLayers()
        {
            var failures = new List<string>();
            var transportKey = RealtimeTransportKeys.CreateTransportKey();
            var client = new RealtimeClientTransportSecurity();
            var worker = new RealtimeWorkerTransportSecurity(RealtimeManagedOnly);
            var endPoint = new IPEndPoint(IPAddress.Loopback, 40000);
            var sameEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 40000);
            using (var keys = RealtimeTransportKeys.Derive(transportKey))
            {
                client.Install(keys, RealtimeManagedOnly);

                // Before the keys: the connect request and the join travel in clear.
                var connect = ClientToWorker(client, worker, endPoint, ConnectRequestPacket());
                ExpectDelivered(failures, "a clear connect request", connect, ConnectRequestPacket());
                ExpectDelivered(failures, "a clear join on the session channel", ClientToWorker(client, worker, endPoint, Channeled(2, 50)), Channeled(2, 50));
                ExpectDelivered(failures, "a clear acknowledgement", WorkerToClient(worker, client, endPoint, Ack()), Ack());
                ExpectDropped(failures, "a clear owner channel packet before the keys", ClientToWorker(client, worker, endPoint, Channeled(14, 20)));
                ExpectDropped(failures, "a clear unreliable packet before the keys", ClientToWorker(client, worker, endPoint, Unreliable(30)));
                ExpectDropped(failures, "a clear session channel packet to the client", WorkerToClient(worker, client, endPoint, Channeled(2, 30)));
                ExpectDropped(failures, "a clear merged packet that hides a channel", WorkerToClient(worker, client, endPoint, Merged(Ack(), Channeled(10, 8))));
                ExpectDelivered(failures, "a clear merged packet of control", WorkerToClient(worker, client, endPoint, Merged(Ack(), Ping())), Merged(Ack(), Ping()));
                Expect(failures, "client sealing before the worker's first sealed datagram", client.IsSealing.ToString(), "False");

                // The consume succeeded: the worker seals from now on.
                worker.Install(endPoint, keys);
                Expect(failures, "worker sealed by the same endpoint", worker.IsSealed(sameEndPoint).ToString(), "True");
                ExpectDropped(failures, "a clear datagram after the worker's switch", ClientToWorker(client, worker, endPoint, Channeled(2, 40)));
                var accepted = Channeled(2, 300);
                var sealedAccept = WorkerSend(worker, endPoint, accepted);
                if (sealedAccept.Length != accepted.Length + RealtimeDatagram.SealedOverhead || sealedAccept[0] != RealtimeDatagram.SealedMarker)
                {
                    failures.Add("The worker's datagram after its switch is not sealed.");
                }

                ExpectDelivered(failures, "the worker's first sealed datagram", ClientReceive(client, sealedAccept), accepted);
                Expect(failures, "client sealing after the worker's first sealed datagram", client.IsSealing.ToString(), "True");
                ExpectDropped(failures, "a replayed worker datagram", ClientReceive(client, sealedAccept));
                ExpectDropped(failures, "a clear datagram after the client's switch", WorkerToClient(new RealtimeWorkerTransportSecurity(), client, endPoint, Ack()));

                var input = Channeled(5, 60);
                var sealedInput = ClientSend(client, input);
                ExpectDelivered(failures, "a sealed client datagram", WorkerReceive(worker, sameEndPoint, (byte[])sealedInput.Clone()), input);
                ExpectDropped(failures, "a replayed client datagram", WorkerReceive(worker, endPoint, (byte[])sealedInput.Clone()));

                // Altered datagrams that were never accepted, so only the tag can refuse them.
                ExpectDropped(failures, "a client datagram with a flipped counter", WorkerReceive(worker, endPoint, Flip(ClientSend(client, input), 2)));
                var fresh = ClientSend(client, input);
                ExpectDropped(failures, "a client datagram with a flipped tag", WorkerReceive(worker, endPoint, Flip(fresh, fresh.Length - 1)));
                ExpectDropped(failures, "a client datagram with a flipped byte", WorkerReceive(worker, endPoint, Flip(ClientSend(client, input), 12)));
                ExpectDropped(failures, "a truncated sealed datagram", WorkerReceive(worker, endPoint, sealedInput.Take(20).ToArray()));
                ExpectDropped(failures, "an unknown marker", WorkerReceive(worker, endPoint, new byte[] { 7, 1, 2, 3 }));
                ExpectDropped(failures, "an empty datagram", WorkerReceive(worker, endPoint, Array.Empty<byte>()));
                ExpectDropped(failures, "a sealed datagram from an endpoint without keys", WorkerReceive(worker, new IPEndPoint(IPAddress.Loopback, 40001), ClientSend(client, input)));

                // Another session's keys open nothing of this one.
                using (var otherKeys = RealtimeTransportKeys.Derive(RealtimeTransportKeys.CreateTransportKey()))
                {
                    var other = new RealtimeClientTransportSecurity();
                    other.Install(otherKeys, RealtimeManagedOnly);
                    ExpectDelivered(failures, "the other session's switch", ClientReceive(other, SealWith(otherKeys, accepted)), accepted);

                    // Past every counter that this session used, so only the key can refuse it.
                    for (var index = 0; index < 100; index++)
                    {
                        ClientSend(other, input);
                    }

                    ExpectDropped(failures, "a datagram of another session's key", WorkerReceive(worker, endPoint, ClientSend(other, input)));
                    other.Dispose();
                }

                // Reordered datagrams within the window all arrive once.
                var batch = Enumerable.Range(0, 64).Select(index => ClientSend(client, Channeled(5, 10 + index))).ToArray();
                foreach (var datagram in batch.Reverse())
                {
                    ExpectDelivered(failures, "a reordered client datagram", WorkerReceive(worker, endPoint, (byte[])datagram.Clone()), null);
                }

                // The keys go with the peer: its sealed datagrams no longer open, and clear ones
                // are a new connection's again.
                worker.Remove(sameEndPoint);
                ExpectDropped(failures, "a sealed datagram after the keys went", WorkerReceive(worker, endPoint, ClientSend(client, input)));
                ExpectDelivered(failures, "a clear connect request after the keys went", WorkerReceive(worker, endPoint, Clear(ConnectRequestPacket())), ConnectRequestPacket());
            }

            Expect(failures, "worker datagrams dropped as unauthenticated", worker.Counters.DroppedUnauthenticated.ToString(CultureInfo.InvariantCulture), "6");
            Expect(failures, "worker datagrams dropped as replayed", worker.Counters.DroppedReplayed.ToString(CultureInfo.InvariantCulture), "1");
            Expect(failures, "worker datagrams dropped as clear after sealing", worker.Counters.DroppedClearAfterSealing.ToString(CultureInfo.InvariantCulture), "1");
            Expect(failures, "worker datagrams dropped as clear and not allowed", worker.Counters.DroppedClearNotAllowed.ToString(CultureInfo.InvariantCulture), "2");
            Expect(failures, "worker datagrams dropped as malformed", worker.Counters.DroppedMalformed.ToString(CultureInfo.InvariantCulture), "3");
            Expect(failures, "client datagrams dropped as replayed", client.Counters.DroppedReplayed.ToString(CultureInfo.InvariantCulture), "1");
            Expect(failures, "client datagrams dropped as clear after sealing", client.Counters.DroppedClearAfterSealing.ToString(CultureInfo.InvariantCulture), "1");
            Expect(failures, "client datagrams dropped as clear and not allowed", client.Counters.DroppedClearNotAllowed.ToString(CultureInfo.InvariantCulture), "2");
            client.Dispose();
            worker.Dispose();
            return failures;
        }

        // A long run never uses a counter twice and every datagram opens once.
        public static IReadOnlyList<string> VerifyCountersNeverRepeat(int datagrams)
        {
            var failures = new List<string>();
            using (var keys = RealtimeTransportKeys.Derive(RealtimeTransportKeys.CreateTransportKey()))
            using (var sender = RealtimeSealedSession.ForClient(keys))
            using (var receiver = RealtimeSealedSession.ForWorker(keys))
            {
                var buffer = new byte[64];
                ulong expected = 0;
                for (var index = 0; index < datagrams; index++)
                {
                    var length = 8;
                    Array.Clear(buffer, 0, buffer.Length);
                    sender.TrySeal(buffer, 0, ref length);
                    if (!RealtimeDatagram.TryReadCounter(buffer, length, out var counter) || counter != expected)
                    {
                        failures.Add($"Datagram {index} carries counter {counter}, expected {expected}.");
                        break;
                    }

                    expected++;
                    if (receiver.TryOpen(buffer, ref length) != RealtimeOpenResult.Opened)
                    {
                        failures.Add($"Datagram {index} did not open.");
                        break;
                    }
                }
            }

            return failures;
        }

        public static IRealtimeAead RealtimeManagedOnly(byte[] key)
        {
            return new RealtimeManagedChaCha20Poly1305(key);
        }

        private static byte[] PinnedTransportKey()
        {
            return Enumerable.Range(0, RealtimeTransportKeys.TransportKeySize).Select(value => (byte)value).ToArray();
        }

        private static Guid TicketId()
        {
            return new Guid(Enumerable.Range(0x10, 16).Select(value => (byte)value).ToArray());
        }

        private static byte[] Nonce()
        {
            return Enumerable.Range(0x40, RealtimeJoinProof.NonceSize).Select(value => (byte)value).ToArray();
        }

        // Pinned at protocol version 46, so a version bump changes no pinned bytes.
        // The pinned proof names protocol version 46 as a fixed input, so a version bump keeps it.
        private static byte[] PinnedProofFor(byte[] proofKey)
        {
            return RealtimeJoinProof.Compute(proofKey, 46, TicketId(), "local-shard-1", Nonce());
        }

        private static byte[] SealWith(RealtimeTransportKeys keys, byte[] packet)
        {
            var worker = new RealtimeWorkerTransportSecurity(RealtimeManagedOnly);
            var endPoint = new IPEndPoint(IPAddress.Loopback, 1);
            worker.Install(endPoint, keys);
            var datagram = WorkerSend(worker, endPoint, packet);
            worker.Dispose();
            return datagram;
        }

        private static byte[] ClientToWorker(
            RealtimeClientTransportSecurity client,
            RealtimeWorkerTransportSecurity worker,
            IPEndPoint endPoint,
            byte[] packet)
        {
            return WorkerReceive(worker, endPoint, ClientSend(client, packet));
        }

        private static byte[] WorkerToClient(
            RealtimeWorkerTransportSecurity worker,
            RealtimeClientTransportSecurity client,
            IPEndPoint endPoint,
            byte[] packet)
        {
            return ClientReceive(client, WorkerSend(worker, endPoint, packet));
        }

        private static byte[] ClientSend(RealtimeClientTransportSecurity client, byte[] packet)
        {
            var buffer = new byte[packet.Length + RealtimeDatagram.MaximumOverhead];
            packet.CopyTo(buffer, 0);
            var offset = 0;
            var length = packet.Length;
            client.ProcessOutbound(buffer, ref offset, ref length);
            return buffer.Skip(offset).Take(length).ToArray();
        }

        private static byte[] WorkerSend(RealtimeWorkerTransportSecurity worker, IPEndPoint endPoint, byte[] packet)
        {
            var buffer = new byte[packet.Length + RealtimeDatagram.MaximumOverhead];
            packet.CopyTo(buffer, 0);
            var offset = 0;
            var length = packet.Length;
            worker.ProcessOutbound(endPoint, buffer, ref offset, ref length);
            return buffer.Skip(offset).Take(length).ToArray();
        }

        private static byte[] ClientReceive(RealtimeClientTransportSecurity client, byte[] datagram)
        {
            var buffer = (byte[])datagram.Clone();
            var length = buffer.Length;
            client.ProcessInbound(buffer, ref length);
            return buffer.Take(length).ToArray();
        }

        private static byte[] WorkerReceive(RealtimeWorkerTransportSecurity worker, IPEndPoint endPoint, byte[] datagram)
        {
            var buffer = (byte[])datagram.Clone();
            var length = buffer.Length;
            worker.ProcessInbound(endPoint, buffer, ref length);
            return buffer.Take(length).ToArray();
        }

        private static byte[] Clear(byte[] packet)
        {
            return new[] { RealtimeDatagram.ClearMarker }.Concat(packet).ToArray();
        }

        // LiteNetLib 2.1 packets: the property in the low five bits of the first byte, and a
        // channeled packet's sequence and channel id after it.
        private static byte[] ConnectRequestPacket()
        {
            return new byte[] { 6, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        }

        private static byte[] Ack()
        {
            return new byte[] { 3, 0, 0, 2, 1, 0, 0, 0 };
        }

        private static byte[] Ping()
        {
            return new byte[] { 4, 1, 0 };
        }

        private static byte[] Unreliable(int payloadBytes)
        {
            return new byte[] { 0 }.Concat(new byte[payloadBytes]).ToArray();
        }

        private static byte[] Channeled(byte channelId, int payloadBytes)
        {
            return new byte[] { 1, 0, 0, channelId }.Concat(Enumerable.Repeat((byte)0x5a, payloadBytes)).ToArray();
        }

        private static byte[] Merged(params byte[][] packets)
        {
            var bytes = new List<byte> { 13 };
            foreach (var packet in packets)
            {
                bytes.Add((byte)packet.Length);
                bytes.Add((byte)(packet.Length >> 8));
                bytes.AddRange(packet);
            }

            return bytes.ToArray();
        }

        private static void ExpectDelivered(List<string> failures, string name, byte[] delivered, byte[] expected)
        {
            if (delivered.Length == 0)
            {
                failures.Add($"{name} was dropped.");
            }
            else if (expected != null && !delivered.SequenceEqual(expected))
            {
                failures.Add($"{name} arrived as {Hex(delivered)}.");
            }
        }

        private static void ExpectDropped(List<string> failures, string name, byte[] delivered)
        {
            if (delivered.Length != 0)
            {
                failures.Add($"{name} was delivered.");
            }
        }

        private static void ExpectRefused(
            List<string> failures,
            IRealtimeAead aead,
            string name,
            byte[] nonce,
            byte[] ciphertext,
            byte[] tag,
            byte[] associatedData)
        {
            var plaintext = new byte[ciphertext.Length];
            if (aead.TryOpen(nonce, ciphertext, tag, plaintext, associatedData))
            {
                failures.Add($"{aead.ImplementationName} opens with {name}.");
            }
        }

        private static void ExpectProof(
            List<string> failures,
            string name,
            bool expected,
            byte[] proofKey,
            ushort version,
            Guid ticketId,
            string shardId,
            byte[] nonce,
            byte[] proof)
        {
            if (RealtimeJoinProof.Verify(proofKey, version, ticketId, shardId, nonce, proof) != expected)
            {
                failures.Add($"The proof with {name} verifies {!expected}.");
            }
        }

        private static void ExpectWindow(List<string> failures, RealtimeReplayWindow window, ulong counter, bool expected)
        {
            var accepted = window.Check(counter);
            if (accepted != expected)
            {
                failures.Add($"The replay window {(accepted ? "accepts" : "refuses")} counter {counter}.");
            }

            if (accepted)
            {
                window.Accept(counter);
            }
        }

        private static void Expect(List<string> failures, string name, string actual, string expected)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                failures.Add($"{name} is {actual}, pinned {expected}.");
            }
        }

        private static byte[] Flip(byte[] bytes, int index)
        {
            var copy = (byte[])bytes.Clone();
            copy[index] ^= 0x01;
            return copy;
        }

        private static byte[] RandomBytes(Random random, int count)
        {
            var bytes = new byte[count];
            random.NextBytes(bytes);
            return bytes;
        }

        private static byte[] FromHex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (var index = 0; index < bytes.Length; index++)
            {
                bytes[index] = byte.Parse(hex.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            return bytes;
        }

        private static string Hex(byte[] bytes)
        {
            return string.Concat(bytes.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }
    }
}
