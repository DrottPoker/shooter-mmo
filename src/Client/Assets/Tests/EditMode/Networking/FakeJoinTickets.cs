using System;
using System.Collections;
using System.Collections.Generic;
using Mmorpg.Client.Api;

namespace Mmorpg.Client.Tests.EditMode
{
    // GameService's join ticket call for the reconnect tests: every request waits until the test
    // grants or refuses it, and its answer arrives on the next step of the routine that asked, as
    // a request's answer arrives on a later frame.
    internal sealed class FakeJoinTickets : IJoinTicketApi
    {
        public List<Request> Requests { get; } = new List<Request>();

        public Request Last
        {
            get { return Requests[Requests.Count - 1]; }
        }

        public IEnumerator CreateJoinTicket(
            string gameServiceBaseUrl,
            string sessionToken,
            string shardId,
            JoinShardRequest request,
            Action<JoinShardResponse> onSuccess,
            Action<ApiError> onError)
        {
            var pending = new Request(sessionToken, shardId, request.characterId);
            Requests.Add(pending);
            while (!pending.IsAnswered)
            {
                yield return null;
            }

            if (pending.Error != null)
            {
                onError(pending.Error);
            }
            else
            {
                onSuccess(pending.Ticket);
            }
        }

        public sealed class Request
        {
            public Request(string sessionToken, string shardId, string characterId)
            {
                SessionToken = sessionToken;
                ShardId = shardId;
                CharacterId = characterId;
            }

            public string SessionToken { get; }

            public string ShardId { get; }

            public string CharacterId { get; }

            public bool IsAnswered { get; private set; }

            public JoinShardResponse Ticket { get; private set; }

            public ApiError Error { get; private set; }

            public void Grant(JoinShardResponse ticket)
            {
                Ticket = ticket;
                IsAnswered = true;
            }

            public void Refuse(ApiError error)
            {
                Error = error;
                IsAnswered = true;
            }
        }
    }
}
