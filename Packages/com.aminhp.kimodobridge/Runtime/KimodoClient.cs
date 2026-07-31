// SPDX-License-Identifier: Apache-2.0
// HTTP client for the Kimodo bridge server. Callback-based on top of
// UnityWebRequest so it works from both the Editor (async op completed event)
// and at runtime.

using System;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace AminHP.KimodoBridge
{
    public class KimodoClient
    {
        public string BaseUrl { get; set; }

        /// <summary>Timeout for generation requests (seconds). 0 = no timeout.
        /// Kimodo generation can take ~1 minute on modest hardware.</summary>
        public int GenerateTimeoutSeconds = 0;

        public KimodoClient(string baseUrl = "http://127.0.0.1:8765")
        {
            BaseUrl = baseUrl.TrimEnd('/');
        }

        // --- GET /health ---
        public UnityWebRequest GetHealth(Action<bool, KimodoHealth, string> onDone)
        {
            var req = UnityWebRequest.Get(BaseUrl + "/health");
            req.timeout = 10;
            SendJson(req, onDone);
            return req;
        }

        // --- GET /progress --- (answers while /generate is still working)
        public UnityWebRequest GetProgress(Action<bool, KimodoProgress, string> onDone)
        {
            var req = UnityWebRequest.Get(BaseUrl + "/progress");
            req.timeout = 5;
            SendJson(req, onDone);
            return req;
        }

        // --- GET /models ---
        public UnityWebRequest GetModels(Action<bool, KimodoModelList, string> onDone)
        {
            var req = UnityWebRequest.Get(BaseUrl + "/models");
            req.timeout = 15;
            SendJson(req, onDone);
            return req;
        }

        // --- GET /skeleton --- (bones only, so the client can author constraints before the first generate)
        public UnityWebRequest GetSkeleton(string model, Action<bool, KimodoMotion, string> onDone)
        {
            var req = UnityWebRequest.Get(BaseUrl + "/skeleton?model=" + UnityWebRequest.EscapeURL(model));
            req.timeout = 0; // may trigger a model load the first time
            SendJson(req, onDone);
            return req;
        }

        // --- POST /load_model ---
        [Serializable]
        private class LoadReq { public string model; }

        public UnityWebRequest LoadModel(string model, Action<bool, KimodoLoadResult, string> onDone)
        {
            string json = JsonUtility.ToJson(new LoadReq { model = model });
            var req = new UnityWebRequest(BaseUrl + "/load_model", "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 0; // model load (first time) can take a while

            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                if (!IsSuccess(req))
                {
                    onDone?.Invoke(false, null, DescribeError(req));
                    req.Dispose();
                    return;
                }
                KimodoLoadResult res = null;
                try { res = JsonUtility.FromJson<KimodoLoadResult>(req.downloadHandler.text); }
                catch (Exception e) { onDone?.Invoke(false, null, "JSON parse error: " + e.Message); req.Dispose(); return; }
                if (res != null && !string.IsNullOrEmpty(res.detail))
                    onDone?.Invoke(false, null, "Server error: " + res.detail);
                else
                    onDone?.Invoke(true, res, null);
                req.Dispose();
            };
            return req;
        }

        // --- POST /generate ---
        public UnityWebRequest Generate(KimodoGenerateRequest request, Action<bool, KimodoMotion, string> onDone)
        {
            string json = JsonUtility.ToJson(request);
            var req = new UnityWebRequest(BaseUrl + "/generate", "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = GenerateTimeoutSeconds;

            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                if (!IsSuccess(req))
                {
                    onDone?.Invoke(false, null, DescribeError(req));
                    req.Dispose();
                    return;
                }
                KimodoMotion motion = null;
                try { motion = JsonUtility.FromJson<KimodoMotion>(req.downloadHandler.text); }
                catch (Exception e) { onDone?.Invoke(false, null, "JSON parse error: " + e.Message); req.Dispose(); return; }

                if (motion != null && !string.IsNullOrEmpty(motion.detail))
                    onDone?.Invoke(false, null, "Server error: " + motion.detail);
                else if (motion == null || motion.clips == null || motion.clips.Count == 0)
                    onDone?.Invoke(false, null, "Server returned no clips.");
                else
                    onDone?.Invoke(true, motion, null);
                req.Dispose();
            };
            return req;
        }

        // --- shared helper for simple GET/JSON endpoints ---
        private static void SendJson<T>(UnityWebRequest req, Action<bool, T, string> onDone) where T : class
        {
            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                if (!IsSuccess(req))
                {
                    onDone?.Invoke(false, null, DescribeError(req));
                    req.Dispose();
                    return;
                }
                T result = null;
                try { result = JsonUtility.FromJson<T>(req.downloadHandler.text); }
                catch (Exception e) { onDone?.Invoke(false, null, "JSON parse error: " + e.Message); req.Dispose(); return; }
                onDone?.Invoke(true, result, null);
                req.Dispose();
            };
        }

        private static bool IsSuccess(UnityWebRequest req)
        {
            return req.result == UnityWebRequest.Result.Success;
        }

        // Assigned by JsonUtility, which the compiler cannot see - hence the pragma.
#pragma warning disable CS0649
        [Serializable] private class ErrorBody { public string detail; }
#pragma warning restore CS0649

        private static string DescribeError(UnityWebRequest req)
        {
            string body = req.downloadHandler != null ? req.downloadHandler.text : null;
            string msg = $"{req.error} (HTTP {req.responseCode})";
            if (string.IsNullOrEmpty(body)) return msg;

            // The server answers errors as {"detail": "..."}; show that sentence rather than the raw
            // JSON, since it is the part that says what to do about it.
            try
            {
                var parsed = JsonUtility.FromJson<ErrorBody>(body);
                if (parsed != null && !string.IsNullOrEmpty(parsed.detail)) return msg + " — " + parsed.detail;
            }
            catch { /* not JSON: fall through and show it as-is */ }
            return msg + " — " + body;
        }
    }
}
