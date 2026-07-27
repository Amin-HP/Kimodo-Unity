// SPDX-License-Identifier: Apache-2.0
// Scene-level manager for the Kimodo bridge connection. Owns the KimodoClient,
// tracks connection + model state, and is referenced by KimodoGenerator components.
//
// Connection/model state is transient (not serialized): it is rebuilt by pressing
// Connect and is lost across domain reloads / play-mode changes. Only the server URL
// and selected model persist. Async web callbacks guard against a destroyed component
// with `if (!this) return;` (Unity's lifetime-aware bool operator).

using System;
using UnityEngine;

namespace AminHP.KimodoBridge
{
    [AddComponentMenu("Kimodo/Kimodo Bridge (Manager)")]
    public class KimodoBridge : MonoBehaviour
    {
        [Tooltip("Bridge server URL (run_bridge.ps1 serves http://127.0.0.1:8765).")]
        public string serverUrl = "http://127.0.0.1:8765";

        [Tooltip("Model short key used for generation, e.g. 'soma'.")]
        public string model = "soma";

        public enum ConnectionState { Unknown, Connecting, Online, Offline }
        public enum ModelLoadState { None, Loading, Loaded, Failed }

        // ---- transient runtime state (not serialized) ----
        [NonSerialized] public KimodoClient Client;
        [NonSerialized] public KimodoHealth Health;
        [NonSerialized] public KimodoModelList Models;
        [NonSerialized] public KimodoMotion Skeleton;   // bones-only, fetched on connect (pre-generation authoring)
        [NonSerialized] public ConnectionState Connection = ConnectionState.Unknown;
        [NonSerialized] public string StatusMessage = "Not connected.";
        [NonSerialized] public ModelLoadState ModelState = ModelLoadState.None;
        [NonSerialized] public string ModelStatus = "";

        public bool IsOnline => Connection == ConnectionState.Online;
        public bool ModelReady => ModelState == ModelLoadState.Loaded;

        /// <summary>Lazily create the HTTP client and keep its base URL in sync with the field.</summary>
        public KimodoClient GetClient()
        {
            if (Client == null) Client = new KimodoClient(serverUrl);
            else Client.BaseUrl = serverUrl.TrimEnd('/');
            return Client;
        }

        /// <summary>Contact the server, then fetch the model list and preload the selected model.</summary>
        public void Connect(Action onChanged = null)
        {
            var client = GetClient();
            Connection = ConnectionState.Connecting;
            StatusMessage = "Contacting server…";
            onChanged?.Invoke();
            client.GetHealth((ok, health, err) =>
            {
                if (!this) return;
                if (!ok)
                {
                    Connection = ConnectionState.Offline;
                    StatusMessage = "Offline: " + err + "\nIs the bridge server running? (run_bridge.ps1)";
                    onChanged?.Invoke();
                    return;
                }
                Health = health;
                Connection = ConnectionState.Online;
                StatusMessage = $"Online · device={health.device} · cuda={health.cudaAvailable} · default={health.defaultModel}";
                if (string.IsNullOrEmpty(model) && !string.IsNullOrEmpty(health.defaultModel))
                    model = health.defaultModel;
                RefreshModels(() => { LoadModel(onChanged); onChanged?.Invoke(); });
                onChanged?.Invoke();
            });
        }

        /// <summary>Fetch the registry of available models.</summary>
        public void RefreshModels(Action onChanged = null)
        {
            GetClient().GetModels((ok, list, err) =>
            {
                if (!this) return;
                if (ok && list != null) Models = list;
                else StatusMessage += "\n(models fetch failed: " + err + ")";
                onChanged?.Invoke();
            });
        }

        /// <summary>Preload the currently selected model so the first Generate is fast.</summary>
        public void LoadModel(Action onChanged = null)
        {
            if (Connection != ConnectionState.Online || string.IsNullOrEmpty(model)) return;
            string key = model;
            ModelState = ModelLoadState.Loading;
            ModelStatus = $"Loading {key} … (first load can take a while)";
            onChanged?.Invoke();
            GetClient().LoadModel(key, (ok, res, err) =>
            {
                if (!this) return;
                if (key != model) return; // selection changed meanwhile; ignore stale result
                if (!ok)
                {
                    ModelState = ModelLoadState.Failed;
                    ModelStatus = "Model load failed: " + err;
                }
                else
                {
                    ModelState = ModelLoadState.Loaded;
                    ModelStatus = "Ready: " + (res != null && !string.IsNullOrEmpty(res.displayName) ? res.displayName : key);
                    if (Models != null)
                        foreach (var m in Models.models)
                            if (m.shortKey == key) m.loaded = true;
                    FetchSkeleton(onChanged);   // pull the rest skeleton so constraints can be authored pre-generation
                }
                onChanged?.Invoke();
            });
        }

        /// <summary>Fetch the current model's rest skeleton (bones only) so waypoints/pose constraints can be
        /// authored and drawn before the first /generate. Cheap once the model is loaded.</summary>
        public void FetchSkeleton(Action onChanged = null)
        {
            if (Connection != ConnectionState.Online || string.IsNullOrEmpty(model)) return;
            string key = model;
            GetClient().GetSkeleton(key, (ok, skel, err) =>
            {
                if (!this) return;
                if (key != model) return; // model changed meanwhile
                if (ok && skel != null && skel.bones != null && skel.bones.Count > 0) Skeleton = skel;
                onChanged?.Invoke();
            });
        }

        /// <summary>Forget the (in-memory) connection so auto-reconnect stops. The server keeps the model loaded.</summary>
        public void Disconnect()
        {
            Connection = ConnectionState.Offline;
            ModelState = ModelLoadState.None;
            StatusMessage = "Disconnected (server still has the model loaded).";
        }
    }
}
