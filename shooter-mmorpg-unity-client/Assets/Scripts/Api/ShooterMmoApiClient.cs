using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace ShooterMmo.Api
{
    public sealed class ShooterMmoApiClient
    {
        private readonly int timeoutSeconds;

        public ShooterMmoApiClient(int timeoutSeconds)
        {
            this.timeoutSeconds = Mathf.Clamp(timeoutSeconds, 1, 120);
        }

        public IEnumerator Register(
            string authServiceBaseUrl,
            RegisterAccountRequest request,
            Action<AuthResponse> onSuccess,
            Action<ShooterMmoApiError> onError)
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
            Action<ShooterMmoApiError> onError)
        {
            return SendJson(
                "POST",
                CombineUrl(authServiceBaseUrl, "/api/accounts/login"),
                JsonUtility.ToJson(request),
                null,
                onSuccess,
                onError);
        }

        public IEnumerator Logout(
            string authServiceBaseUrl,
            string sessionToken,
            Action onSuccess,
            Action<ShooterMmoApiError> onError)
        {
            return SendEmpty(
                "POST",
                CombineUrl(authServiceBaseUrl, "/api/accounts/logout"),
                null,
                sessionToken,
                onSuccess,
                onError);
        }

        public IEnumerator RevokeSession(
            string authServiceBaseUrl,
            string sessionToken,
            string sessionId,
            Action onSuccess,
            Action<ShooterMmoApiError> onError)
        {
            return SendEmpty(
                "DELETE",
                CombineUrl(authServiceBaseUrl, "/api/accounts/sessions/" + Uri.EscapeDataString(sessionId)),
                null,
                sessionToken,
                onSuccess,
                onError);
        }

        public IEnumerator ValidateSession(
            string authServiceBaseUrl,
            string sessionToken,
            Action onSuccess,
            Action<ShooterMmoApiError> onError)
        {
            return SendEmpty(
                "GET",
                CombineUrl(authServiceBaseUrl, "/api/accounts/session"),
                null,
                sessionToken,
                onSuccess,
                onError);
        }

        public IEnumerator GetCharacters(
            string authServiceBaseUrl,
            string sessionToken,
            Action<CharacterResponse[]> onSuccess,
            Action<ShooterMmoApiError> onError)
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
            Action<ShooterMmoApiError> onError)
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
            Action<ShooterMmoApiError> onError)
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
            Action<ShooterMmoApiError> onError)
        {
            return SendJson(
                "POST",
                CombineUrl(authServiceBaseUrl, "/api/worlds/" + Uri.EscapeDataString(worldId) + "/join"),
                JsonUtility.ToJson(request),
                sessionToken,
                onSuccess,
                onError);
        }

        private IEnumerator SendJson<T>(
            string method,
            string url,
            string json,
            string sessionToken,
            Action<T> onSuccess,
            Action<ShooterMmoApiError> onError)
        {
            using (var request = CreateRequest(method, url, json, sessionToken))
            {
                yield return request.SendWebRequest();

                if (!IsSuccessful(request))
                {
                    onError(CreateError(request));
                    yield break;
                }

                var body = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                if (string.IsNullOrWhiteSpace(body))
                {
                    onError(InvalidResponse("The server returned an empty response."));
                    yield break;
                }

                T response;
                try
                {
                    response = JsonUtility.FromJson<T>(body);
                }
                catch (Exception exception)
                {
                    onError(InvalidResponse("The server returned invalid JSON: " + exception.Message));
                    yield break;
                }

                if (response == null)
                {
                    onError(InvalidResponse("The server returned an invalid response."));
                    yield break;
                }

                onSuccess(response);
            }
        }

        private IEnumerator SendArray<T>(
            string method,
            string url,
            string json,
            string sessionToken,
            Action<T[]> onSuccess,
            Action<ShooterMmoApiError> onError)
        {
            using (var request = CreateRequest(method, url, json, sessionToken))
            {
                yield return request.SendWebRequest();

                if (!IsSuccessful(request))
                {
                    onError(CreateError(request));
                    yield break;
                }

                T[] result;
                if (!JsonArrayUtility.TryFromJson(request.downloadHandler.text, out result))
                {
                    onError(InvalidResponse("The server returned an invalid JSON array."));
                    yield break;
                }

                onSuccess(result);
            }
        }

        private IEnumerator SendEmpty(
            string method,
            string url,
            string json,
            string sessionToken,
            Action onSuccess,
            Action<ShooterMmoApiError> onError)
        {
            using (var request = CreateRequest(method, url, json, sessionToken))
            {
                yield return request.SendWebRequest();

                if (!IsSuccessful(request))
                {
                    onError(CreateError(request));
                    yield break;
                }

                onSuccess();
            }
        }

        private UnityWebRequest CreateRequest(
            string method,
            string url,
            string json,
            string sessionToken)
        {
            var request = new UnityWebRequest(url, method)
            {
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = timeoutSeconds
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

        internal static ShooterMmoApiError ParseHttpError(long statusCode, string body, string fallbackMessage)
        {
            ProblemDetailsResponse problem = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    problem = JsonUtility.FromJson<ProblemDetailsResponse>(body);
                }
                catch (ArgumentException)
                {
                    problem = null;
                }
            }

            var code = problem != null && !string.IsNullOrWhiteSpace(problem.code)
                ? problem.code
                : "http_error";
            var message = problem != null && !string.IsNullOrWhiteSpace(problem.detail)
                ? problem.detail
                : fallbackMessage;
            var correlationId = problem != null ? problem.correlationId : string.Empty;

            return new ShooterMmoApiError(
                ShooterMmoApiErrorKind.Http,
                statusCode,
                code,
                message,
                correlationId);
        }

        private static ShooterMmoApiError CreateError(UnityWebRequest request)
        {
            var body = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
            if (request.result == UnityWebRequest.Result.ConnectionError)
            {
                var timedOut = !string.IsNullOrWhiteSpace(request.error)
                    && (request.error.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
                        || request.error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0);
                return new ShooterMmoApiError(
                    timedOut ? ShooterMmoApiErrorKind.Timeout : ShooterMmoApiErrorKind.Network,
                    request.responseCode,
                    timedOut ? "request_timeout" : "network_error",
                    timedOut ? "The request timed out." : request.error,
                    string.Empty);
            }

            if (request.responseCode > 0)
            {
                return ParseHttpError(request.responseCode, body, request.error);
            }

            return InvalidResponse(request.error);
        }

        private static ShooterMmoApiError InvalidResponse(string message)
        {
            return new ShooterMmoApiError(
                ShooterMmoApiErrorKind.InvalidResponse,
                0,
                "invalid_response",
                message,
                string.Empty);
        }

        private static string CombineUrl(string baseUrl, string path)
        {
            return baseUrl.TrimEnd('/') + path;
        }
    }
}
