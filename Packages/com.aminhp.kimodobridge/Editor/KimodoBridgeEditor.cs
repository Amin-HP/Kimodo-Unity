// SPDX-License-Identifier: Apache-2.0
// Inspector for the KimodoBridge manager.
//
// Laid out as the three states you actually care about, in the order you meet them — is the server
// running, are we connected, is the model loaded — each one status line with the button that changes
// it. Everything you set once and forget (interpreter, URL, where the server lives) sits behind Setup,
// which opens by itself while something is unconfigured or broken, and the server's own output has its
// own foldout so a model load cannot bury the controls.

using UnityEditor;
using UnityEngine;

namespace AminHP.KimodoBridge.Editor
{
    [CustomEditor(typeof(KimodoBridge))]
    public class KimodoBridgeEditor : UnityEditor.Editor
    {
        private KimodoBridge Bridge => (KimodoBridge)target;

        private bool _setupFoldout, _logFoldout;
        private Vector2 _logScroll;

        private static readonly Color Green = new Color(0.4f, 0.9f, 0.4f);
        private static readonly Color Amber = new Color(0.95f, 0.9f, 0.4f);
        private static readonly Color Red = new Color(0.95f, 0.5f, 0.5f);
        private static readonly Color Grey = Color.gray;

        public override void OnInspectorGUI()
        {
            var b = Bridge;

            DrawServerRow(b);
            DrawConnectionRow(b);
            DrawModelRow(b);

            EditorGUILayout.Space(2f);
            DrawSetup(b);
            DrawServerOutput();
        }

        // ---- 1. the server process ---------------------------------------------------------------
        private void DrawServerRow(KimodoBridge b)
        {
            // A URL naming another machine means the server lives there: there is nothing here to
            // start or stop, and offering the buttons would only mislead.
            if (!KimodoServerLauncher.IsLocalUrl(b.serverUrl))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    Dot(Grey, "Server on another machine");
                    GUILayout.FlexibleSpace();
                }
                EditorGUILayout.LabelField("Start it over there, then Connect. It has to be run with " +
                    "--host 0.0.0.0 to accept connections from this one.", EditorStyles.wordWrappedMiniLabel);
                return;
            }

            bool running = KimodoServerLauncher.IsRunning;
            bool starting = KimodoServerLauncher.Starting;
            // We only track servers THIS project started. One that is online without a PID of ours was
            // started somewhere else — another Unity project, or a terminal — and saying "not running"
            // about a server that is plainly answering is how you end up pressing Start and colliding
            // with its port.
            bool external = !running && !starting && b.Connection == KimodoBridge.ConnectionState.Online;

            using (new EditorGUILayout.HorizontalScope())
            {
                Dot(starting ? Amber : running || external ? Green : Grey,
                    starting ? "Server starting…"
                    : running ? $"Server running (PID {KimodoServerLauncher.RunningPid})"
                    : external ? "Server running (started outside this project)"
                              : "Server not running");
                GUILayout.FlexibleSpace();

                if (external)
                {
                    // Nothing to offer: we cannot stop what we did not start, and it needs no starting.
                }
                else if (running)
                {
                    if (GUILayout.Button("Stop", GUILayout.Width(64)))
                    {
                        // Stopping also drops the connection (see KimodoBridgeAutoConnect.OnServerStopped),
                        // so the rows below go grey with it instead of staying green against nothing.
                        KimodoServerLauncher.Stop();
                        Repaint();
                    }
                }
                else
                    using (new EditorGUI.DisabledScope(starting))
                        if (GUILayout.Button(starting ? "Starting…" : "Start", GUILayout.Width(64)))
                            StartServer(b);
            }

            if (external)
                EditorGUILayout.LabelField("Using a server that was already running — stop it where it was " +
                    "started. Connect and the model may already be loaded, so it answers at once.",
                    EditorStyles.wordWrappedMiniLabel);
            else if (starting)
                EditorGUILayout.LabelField("The first start loads the model, which takes a while.",
                    EditorStyles.wordWrappedMiniLabel);
            else if (KimodoServerLauncher.Detached)
                EditorGUILayout.LabelField("Started before the last recompile — still stoppable, but its " +
                    "output is no longer captured.", EditorStyles.wordWrappedMiniLabel);

            if (!string.IsNullOrEmpty(KimodoServerLauncher.Problem))
                EditorGUILayout.HelpBox(KimodoServerLauncher.Problem, MessageType.Error);
        }

        private void StartServer(KimodoBridge b)
        {
            KimodoServerLauncher.ProbeUrl = b.serverUrl;
            KimodoServerLauncher.Start(b.serverUrl, b.model, (ok, _) =>
            {
                // Connect once it answers: starting it and then having to press Connect would be a
                // pointless second step.
                if (ok) { KimodoBridgeAutoConnect.Wanted = true; b.Connect(Repaint); }
                else _setupFoldout = true;      // something is wrong; show what can be changed
                Repaint();
            });
            Repaint();
        }

        // ---- 2. the connection -------------------------------------------------------------------
        private void DrawConnectionRow(KimodoBridge b)
        {
            bool online = b.Connection == KimodoBridge.ConnectionState.Online;
            using (new EditorGUILayout.HorizontalScope())
            {
                Dot(ConnColor(b.Connection), online ? "Connected" : b.Connection.ToString());
                GUILayout.FlexibleSpace();
                if (online)
                {
                    if (GUILayout.Button("Disconnect", GUILayout.Width(84)))
                    { KimodoBridgeAutoConnect.Wanted = false; b.Disconnect(); Repaint(); }
                }
                else if (GUILayout.Button("Connect", GUILayout.Width(84)))
                { KimodoBridgeAutoConnect.Wanted = true; b.Connect(Repaint); }
            }

            if (!online)
                EditorGUILayout.LabelField(b.StatusMessage, EditorStyles.wordWrappedMiniLabel);
        }

        // ---- 3. the model ------------------------------------------------------------------------
        private void DrawModelRow(KimodoBridge b)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                Dot(ModelColor(b.ModelState),
                    b.ModelState == KimodoBridge.ModelLoadState.None ? "Model" : b.ModelStatus);
                GUILayout.FlexibleSpace();
                DrawModelPicker(b);
            }
        }

        private void DrawModelPicker(KimodoBridge b)
        {
            if (b.Models == null || b.Models.models.Count == 0)
            {
                // No list yet: fall back to a free-text model key so the user isn't blocked.
                EditorGUI.BeginChangeCheck();
                string key = EditorGUILayout.TextField(b.model, GUILayout.Width(150));
                if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(b, "Edit Kimodo model"); b.model = key; }
                return;
            }

            var models = b.Models.models;
            var labels = new string[models.Count];
            int current = 0;
            for (int i = 0; i < models.Count; i++)
            {
                labels[i] = $"{models[i].displayName}{(models[i].loaded ? "  ✓" : "")}";
                if (models[i].shortKey == b.model) current = i;
            }

            using (new EditorGUI.DisabledScope(b.ModelState == KimodoBridge.ModelLoadState.Loading))
            {
                EditorGUI.BeginChangeCheck();
                int picked = EditorGUILayout.Popup(current, labels, GUILayout.Width(150));
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(b, "Select Kimodo model");
                    b.model = models[picked].shortKey;
                    b.LoadModel(Repaint);   // switching preloads it
                }
            }
        }

        // ---- setup (set once) --------------------------------------------------------------------
        private void DrawSetup(KimodoBridge b)
        {
            // Open on its own when there is nothing to run yet or something failed — that is exactly
            // when its contents are the answer.
            bool needed = !KimodoServerLauncher.PythonIsSet || !string.IsNullOrEmpty(KimodoServerLauncher.Problem);
            if (needed && !_setupFoldout) _setupFoldout = true;

            _setupFoldout = EditorGUILayout.Foldout(_setupFoldout, "Setup", true);
            if (!_setupFoldout) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUI.BeginChangeCheck();
                string url = EditorGUILayout.TextField(new GUIContent("Server URL",
                    "Where the server listens. Start takes its host and port from here."), b.serverUrl);
                if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(b, "Edit Kimodo server URL"); b.serverUrl = url; }

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    string py = EditorGUILayout.TextField(new GUIContent("Python",
                        "Interpreter of an environment with Kimodo installed — venv/Scripts/python.exe on " +
                        "Windows, venv/bin/python on macOS and Linux. Remembered per machine."),
                        KimodoServerLauncher.PythonPath);
                    if (EditorGUI.EndChangeCheck()) KimodoServerLauncher.PythonPath = py;

                    if (GUILayout.Button("…", GUILayout.Width(26)))
                    {
                        string picked = EditorUtility.OpenFilePanel("Select the Python interpreter", "", "");
                        if (!string.IsNullOrEmpty(picked)) KimodoServerLauncher.PythonPath = picked;
                    }
                }
                if (!KimodoServerLauncher.PythonIsSet)
                    EditorGUILayout.HelpBox(
                        "Point this at a Python environment that has Kimodo installed, then press Start. " +
                        "The server itself ships with this package — nothing else to locate.",
                        MessageType.Info);

                EditorGUI.BeginChangeCheck();
                bool remote = EditorGUILayout.Toggle(new GUIContent("Reachable from other devices",
                    "Bind the server to 0.0.0.0 so another machine can use it — point that machine's " +
                    "Server URL at this one's IP. The API has no authentication, so only do this on a " +
                    "network you trust."), KimodoServerLauncher.AllowRemoteClients);
                if (EditorGUI.EndChangeCheck()) KimodoServerLauncher.AllowRemoteClients = remote;
                if (KimodoServerLauncher.AllowRemoteClients)
                    EditorGUILayout.HelpBox(
                        "The server will accept connections from anything that can reach this machine. " +
                        "There is no authentication — keep it to a network you trust.", MessageType.Warning);

                EditorGUI.BeginChangeCheck();
                bool cpu = EditorGUILayout.Toggle(new GUIContent("Text encoder on CPU",
                    "Keeps the ~8B text encoder off the GPU so the diffusion model fits in 8 GB of VRAM. " +
                    "Turn it off on a bigger card for faster prompts."), KimodoServerLauncher.TextEncoderOnCpu);
                if (EditorGUI.EndChangeCheck()) KimodoServerLauncher.TextEncoderOnCpu = cpu;

                // Almost nobody needs this: it is here for running a server you are editing.
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    string dir = EditorGUILayout.TextField(new GUIContent("Server folder",
                        "Folder containing the kimodo_bridge package. Empty = the copy bundled with this " +
                        "package; set it to run a checkout you are editing instead."),
                        KimodoServerLauncher.UsingBundledServer ? "" : KimodoServerLauncher.ServerDir);
                    if (EditorGUI.EndChangeCheck()) KimodoServerLauncher.ServerDir = dir;

                    if (GUILayout.Button("…", GUILayout.Width(26)))
                    {
                        string picked = EditorUtility.OpenFolderPanel("Folder containing kimodo_bridge", "", "");
                        if (!string.IsNullOrEmpty(picked)) KimodoServerLauncher.ServerDir = picked;
                    }
                }
                if (KimodoServerLauncher.UsingBundledServer)
                    EditorGUILayout.LabelField("Using the server bundled with the package.", EditorStyles.miniLabel);

                EditorGUILayout.LabelField(
                    "The connection survives Play mode and recompiles (auto-reconnect); Disconnect stops that. " +
                    "To use a server on another machine, put its address in Server URL — everything else works " +
                    "the same, it is all plain HTTP.",
                    EditorStyles.wordWrappedMiniLabel);
            }
        }

        // ---- the server's own output -------------------------------------------------------------
        private void DrawServerOutput()
        {
            var log = KimodoServerLauncher.Log;
            if (log.Count == 0) return;

            _logFoldout = EditorGUILayout.Foldout(_logFoldout, $"Server output ({log.Count})", true);
            if (!_logFoldout) return;

            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(120f));
            for (int i = 0; i < log.Count; i++)   // newest last, like a terminal
                EditorGUILayout.SelectableLabel(log[i], EditorStyles.miniLabel, GUILayout.Height(14f));
            EditorGUILayout.EndScrollView();
        }

        // ---- bits ---------------------------------------------------------------------------------
        private static void Dot(Color c, string text)
        {
            var prev = GUI.color;
            GUI.color = c;
            GUILayout.Label("●", GUILayout.Width(14f));
            GUI.color = prev;
            GUILayout.Label(text, EditorStyles.label);
        }

        private static Color ConnColor(KimodoBridge.ConnectionState s) => s switch
        {
            KimodoBridge.ConnectionState.Online => Green,
            KimodoBridge.ConnectionState.Offline => Red,
            KimodoBridge.ConnectionState.Connecting => Amber,
            _ => Grey,
        };

        private static Color ModelColor(KimodoBridge.ModelLoadState s) => s switch
        {
            KimodoBridge.ModelLoadState.Loaded => Green,
            KimodoBridge.ModelLoadState.Loading => Amber,
            KimodoBridge.ModelLoadState.Failed => Red,
            _ => Grey,
        };
    }
}
