using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace ShooterMmo.Api
{
    public sealed class ShooterMmoApiClient
    {
        public IEnumerator Register(
            string authServiceBaseUrl,
            RegisterAccountRequest request,
            Action<AuthResponse> onSuccess,
            Action<string> onError)
        {
            return SendJson(
                "POST",
                CombineUrl(authServiceBaseUrl, "/api/accounts/register"),
                JsonUtility.ToJson(request),
                null,
                onSuccess,
                onError);
        }

        public IEnumerator Login(
            string authServiceBaseUrl,
            LoginAccountRequest request,
            Action<AuthResponse> onSuccess,
            Action<string> onError)
        {
            return SendJson(
                "POST",
                CombineUrl(authServiceBaseUrl, "/api/accounts/login"),
                JsonUtility.ToJson(request),
                null,
                onSuccess,
                onError);
        }

        public IEnumerator GetCharacters(
            string authServiceBaseUrl,
            string sessionToken,
            Action<CharacterResponse[]> onSuccess,
            Action<string> onError)
        {
            return SendArray(
                "GET",
                CombineUrl(authServiceBaseUrl, "/api/characters"),
                null,
                sessionToken,
                onSuccess,
                onError);
        }

        public IEnumerator CreateCharacter(
            string authServiceBaseUrl,
            string sessionToken,
            CreateCharacterRequest request,
            Action<CharacterResponse> onSuccess,
            Action<string> onError)
        {
            return SendJson(
                "POST",
                CombineUrl(authServiceBaseUrl, "/api/characters"),
                JsonUtility.ToJson(request),
                sessionToken,
                onSuccess,
                onError);
        }

        public IEnumerator GetWorlds(
            string authServiceBaseUrl,
            Action<WorldResponse[]> onSuccess,
            Action<string> onError)
        {
            return SendArray(
                "GET",
                CombineUrl(authServiceBaseUrl, "/api/worlds"),
                null,
                null,
                onSuccess,
                onError);
        }

        public IEnumerator CreateJoinTicket(
            string authServiceBaseUrl,
            string sessionToken,
            string worldId,
            JoinWorldRequest request,
            Action<JoinWorldResponse> onSuccess,
            Action<string> onError)
        {
            return SendJson(
                "POST",
                CombineUrl(authServiceBaseUrl, "/api/worlds/" + Uri.EscapeDataString(worldId) + "/join"),
                JsonUtility.ToJson(request),
                sessionToken,
                onSuccess,
                onError);
        }

        public IEnumerator DebugJoinWorldServer(
            string worldServerBaseUrl,
            DebugJoinRequest request,
            Action<ActivePlayerSessionResponse> onSuccess,
            Action<string> onError)
        {
            return SendJson(
                "POST",
                CombineUrl(worldServerBaseUrl, "/debug/join"),
                JsonUtility.ToJson(request),
                null,
                onSuccess,
                onError);
        }

        public IEnumerator GetDebugSessions(
            string worldServerBaseUrl,
            Action<ActivePlayerSessionResponse[]> onSuccess,
            Action<string> onError)
        {
            return SendArray(
                "GET",
                CombineUrl(worldServerBaseUrl, "/debug/sessions"),
                null,
                null,
                onSuccess,
                onError);
        }

        public IEnumerator RemoveDebugSession(
            string worldServerBaseUrl,
            string characterId,
            Action onSuccess,
            Action<string> onError)
        {
            return SendEmpty(
                "DELETE",
                CombineUrl(worldServerBaseUrl, "/debug/sessions/" + Uri.EscapeDataString(characterId)),
                null,
                null,
                onSuccess,
                onError);
        }

        private static IEnumerator SendJson<T>(
            string method,
            string url,
            string json,
            string sessionToken,
            Action<T> onSuccess,
            Action<string> onError)
        {
            using (var request = CreateRequest(method, url, json, sessionToken))
            {
                yield return request.SendWebRequest();

                if (!IsSuccessful(request))
                {
                    onError(CreateErrorMessage(request));
                    yield break;
                }

                var response = JsonUtility.FromJson<T>(request.downloadHandler.text);
                if (response == null)
                {
                    onError("The server returned an empty response.");
                    yield break;
                }

                onSuccess(response);
            }
        }

        private static IEnumerator SendArray<T>(
            string method,
            string url,
            string json,
            string sessionToken,
            Action<T[]> onSuccess,
            Action<string> onError)
        {
            using (var request = CreateRequest(method, url, json, sessionToken))
            {
                yield return request.SendWebRequest();

                if (!IsSuccessful(request))
                {
                    onError(CreateErrorMessage(request));
                    yield break;
                }

                onSuccess(JsonArrayUtility.FromJson<T>(request.downloadHandler.text));
            }
        }

        private static IEnumerator SendEmpty(
            string method,
            string url,
            string json,
            string sessionToken,
            Action onSuccess,
            Action<string> onError)
        {
            using (var request = CreateRequest(method, url, json, sessionToken))
            {
                yield return request.SendWebRequest();

                if (!IsSuccessful(request))
                {
                    onError(CreateErrorMessage(request));
                    yield break;
                }

                onSuccess();
            }
        }

        private static UnityWebRequest CreateRequest(
            string method,
            string url,
            string json,
            string sessionToken)
        {
            var request = new UnityWebRequest(url, method)
            {
                downloadHandler = new DownloadHandlerBuffer()
            };

            if (!string.IsNullOrEmpty(json))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                request.uploadHandler = new UploadHandlerRaw(bytes);
                request.SetRequestHeader("Content-Type", "application/json");
            }

            request.SetRequestHeader("Accept", "application/json");

            if (!string.IsNullOrWhiteSpace(sessionToken))
            {
                request.SetRequestHeader("Authorization", "Bearer " + sessionToken);
            }

            return request;
        }

        private static bool IsSuccessful(UnityWebRequest request)
        {
            return request.result == UnityWebRequest.Result.Success
                   && request.responseCode >= 200
                   && request.responseCode < 300;
        }

        private static string CreateErrorMessage(UnityWebRequest request)
        {
            var body = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
            return string.IsNullOrWhiteSpace(body)
                ? request.responseCode + " " + request.error
                : request.responseCode + " " + body;
        }

        private static string CombineUrl(string baseUrl, string path)
        {
            return baseUrl.TrimEnd('/') + path;
        }
    }
}
