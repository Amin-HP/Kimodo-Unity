// SPDX-License-Identifier: Apache-2.0
// Starts and stops the Python bridge server from inside Unity, so you do not have to keep a
// PowerShell window around: press Start Server on the KimodoBridge and it runs run_bridge.ps1,
// watches its output, and says in the Console whether it actually came up.
//
// Three things this has to get right:
//
//  * OUTPUT. The server's stdout/stderr arrive on background threads, where touching the Unity API is
//    not allowed, so lines are queued and drained on EditorApplication.update. Only failures and a few
//    milestones are logged (a model load prints a lot); the rest goes to a rolling buffer the Bridge
//    inspector can show, so nothing is hidden but the Console stays readable.
//  * FAILURES. "It did not start" is useless on its own. The common causes — the venv missing Kimodo,
//    PowerShell's execution policy, a wrong path, the port already taken — are recognised in the output
//    and reported as what to do about them.
//  * SURVIVING RELOADS. A script compile wipes every static field, including the Process handle, which
//    would leave an orphan server nobody can stop. The PID is kept in EditorPrefs (not SessionState — an
//    orphan has to be re-findable after a full restart too) and re-attached on load. Re-attached means
//    "we can see it and stop it": the output streams cannot be reconnected, which the UI says.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AminHP.KimodoBridge.Editor
{
    [InitializeOnLoad]
    public static class KimodoServerLauncher
    {
        private const string ScriptPref = "Kimodo.ServerScript";
        private const string PidPref = "Kimodo.ServerPid";
        private const int MaxLogLines = 400;

        private static Process _proc;                       // null after a domain reload, even while running
        private static readonly Queue<string> _pending = new Queue<string>();
        private static readonly List<string> _log = new List<string>();
        private static bool _pumping;
        private static double _waitUntil;
        private static Action<bool, string> _onReady;

        /// <summary>Rolling tail of the server's own output (newest last).</summary>
        public static IReadOnlyList<string> Log => _log;

        /// <summary>What went wrong last, in words the user can act on. Empty when fine.</summary>
        public static string Problem { get; private set; } = "";

        /// <summary>True while we are waiting for /health to answer after a start.</summary>
        public static bool Starting { get; private set; }

        static KimodoServerLauncher()
        {
            // Re-attach to a server this project started before the reload/restart.
            int pid = EditorPrefs.GetInt(PidKey, 0);
            if (pid != 0 && !IsAlive(pid)) EditorPrefs.DeleteKey(PidKey);
        }

        // Per-project, so two checkouts do not fight over one PID.
        private static string PidKey => PidPref + "." + Application.dataPath.GetHashCode();

        /// <summary>Full path to run_bridge.ps1. Remembered per machine (it is not project data).</summary>
        public static string ScriptPath
        {
            get => EditorPrefs.GetString(ScriptPref, GuessScriptPath());
            set { EditorPrefs.SetString(ScriptPref, value ?? ""); Problem = ""; }
        }

        /// <summary>A best guess at run_bridge.ps1: the copy bundled with this repo, then the usual
        /// place the live Kimodo checkout sits.</summary>
        public static string GuessScriptPath()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath) ?? ".";
            foreach (var candidate in new[]
                     {
                         Path.Combine(projectRoot, "Server", "run_bridge.ps1"),
                         Path.Combine(Path.GetDirectoryName(projectRoot) ?? ".", "kimodo", "run_bridge.ps1"),
                     })
                if (File.Exists(candidate)) return candidate;
            return "";
        }

        public static int RunningPid => EditorPrefs.GetInt(PidKey, 0);

        public static bool IsRunning
        {
            get
            {
                int pid = RunningPid;
                if (pid == 0) return false;
                if (IsAlive(pid)) return true;
                Forget();
                return false;
            }
        }

        /// <summary>True when the server is ours but the process handle was lost to a domain reload —
        /// we can still stop it, we just cannot read its output any more.</summary>
        public static bool Detached => IsRunning && _proc == null;

        // -----------------------------------------------------------------------------------------
        /// <summary>Launch the bridge. <paramref name="onReady"/> fires once /health answers, or with
        /// false when it gives up / the process dies first.</summary>
        public static void Start(string serverUrl, string preloadModel, Action<bool, string> onReady = null)
        {
            if (IsRunning) { onReady?.Invoke(true, "The server is already running."); return; }

            string script = ScriptPath;
            if (string.IsNullOrEmpty(script) || !File.Exists(script))
            {
                Fail($"Could not find run_bridge.ps1. Set the path to it on the KimodoBridge " +
                     $"(looked for '{script}').", onReady);
                return;
            }

            int port = PortOf(serverUrl);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                // -ExecutionPolicy Bypass: the script is local and the default policy blocks it on most
                // machines, which otherwise fails with a message that looks nothing like the real cause.
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Port {port}" +
                            (string.IsNullOrWhiteSpace(preloadModel) ? "" : $" -Preload {preloadModel}"),
                WorkingDirectory = Path.GetDirectoryName(script) ?? ".",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            _log.Clear();
            Problem = "";
            try
            {
                _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _proc.OutputDataReceived += (_, e) => Enqueue(e.Data);
                _proc.ErrorDataReceived += (_, e) => Enqueue(e.Data);
                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
                EditorPrefs.SetInt(PidKey, _proc.Id);
            }
            catch (Exception e)
            {
                _proc = null;
                Fail("Could not start PowerShell: " + e.Message, onReady);
                return;
            }

            Debug.Log($"[Kimodo] Starting the bridge server (PID {_proc.Id}) — {script}");
            Starting = true;
            _onReady = onReady;
            // Loading the ~8B text encoder is slow, and -Preload makes the first start slower still.
            _waitUntil = EditorApplication.timeSinceStartup + 300.0;
            StartPump();
            EditorApplication.update -= PollHealth;
            EditorApplication.update += PollHealth;
        }

        /// <summary>Stop the server we started (the whole tree: PowerShell plus the python it spawned).</summary>
        public static void Stop()
        {
            int pid = RunningPid;
            if (pid == 0) return;
            try
            {
                // taskkill /T, not Process.Kill: killing PowerShell alone would leave python holding the
                // port, and Process.Kill(entireProcessTree) is not available on Unity's runtime.
                using (var kill = Process.Start(new ProcessStartInfo("taskkill", $"/PID {pid} /T /F")
                { UseShellExecute = false, CreateNoWindow = true }))
                    kill?.WaitForExit(5000);
            }
            catch (Exception e) { Debug.LogWarning("[Kimodo] Could not stop the server: " + e.Message); }

            Forget();
            Starting = false;
            EditorApplication.update -= PollHealth;
            Debug.Log("[Kimodo] Bridge server stopped.");
        }

        // -----------------------------------------------------------------------------------------
        private static void Enqueue(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (_pending) _pending.Enqueue(line);
        }

        private static void StartPump()
        {
            if (_pumping) return;
            _pumping = true;
            EditorApplication.update += Pump;
        }

        private static void Pump()
        {
            string[] lines = null;
            lock (_pending)
            {
                if (_pending.Count > 0) { lines = _pending.ToArray(); _pending.Clear(); }
            }
            if (lines != null)
                foreach (var line in lines)
                {
                    _log.Add(line);
                    if (_log.Count > MaxLogLines) _log.RemoveAt(0);
                    Classify(line);
                }

            // The process object is gone after a domain reload; the PID check keeps working.
            if (_proc != null && _proc.HasExited)
            {
                int code = SafeExitCode(_proc);
                _proc = null;
                Forget();
                if (Starting)
                {
                    Starting = false;
                    EditorApplication.update -= PollHealth;
                    string why = string.IsNullOrEmpty(Problem)
                        ? $"The server exited before it came up (exit code {code}). See the server output below."
                        : Problem;
                    Debug.LogError("[Kimodo] " + why);
                    _onReady?.Invoke(false, why);
                    _onReady = null;
                }
                else Debug.Log($"[Kimodo] The bridge server exited (code {code}).");
            }
        }

        // Turn the output that matters into something actionable, and let the rest sit in the buffer.
        private static void Classify(string line)
        {
            if (line.IndexOf("No module named", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("ModuleNotFoundError", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string missing = line.IndexOf("kimodo", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "Kimodo itself is not installed in that virtual environment — install it there " +
                      "(pip install -e .) and try again."
                    : "A Python package the server needs is missing from that virtual environment.";
                Problem = missing + "  (" + line.Trim() + ")";
                Debug.LogError("[Kimodo] " + Problem);
            }
            else if (line.IndexOf("cannot be loaded because running scripts is disabled", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Problem = "PowerShell blocked the script (execution policy). It is launched with " +
                          "-ExecutionPolicy Bypass, so a machine-wide policy is overriding it.";
                Debug.LogError("[Kimodo] " + Problem);
            }
            else if (line.IndexOf("is not recognized", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     line.IndexOf("Activate.ps1", StringComparison.OrdinalIgnoreCase) >= 0 &&
                     line.IndexOf("not exist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Problem = "The script could not find its Python virtual environment — check that the venv " +
                          "sits next to the Kimodo folder, as run_bridge.ps1 expects.";
                Debug.LogError("[Kimodo] " + Problem);
            }
            else if (line.IndexOf("address already in use", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     line.IndexOf("only one usage of each socket address", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Problem = "That port is already taken — a bridge server is probably running already. " +
                          "Press Connect instead, or stop the other one.";
                Debug.LogWarning("[Kimodo] " + Problem);
            }
            else if (line.IndexOf("Traceback (most recent call last)", StringComparison.Ordinal) >= 0)
            {
                Debug.LogError("[Kimodo] The server hit an error — see the server output on the KimodoBridge.");
            }
        }

        // Ask the server itself whether it is up: the process being alive is not the same as serving.
        private static void PollHealth()
        {
            if (!Starting) { EditorApplication.update -= PollHealth; return; }
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 1.0;

            if (EditorApplication.timeSinceStartup > _waitUntil)
            {
                Starting = false;
                EditorApplication.update -= PollHealth;
                string why = "The server did not answer in time. It may still be loading the model — " +
                             "press Connect in a moment, or check the server output.";
                Debug.LogWarning("[Kimodo] " + why);
                _onReady?.Invoke(false, why);
                _onReady = null;
                return;
            }

            if (_probe) return;
            _probe = true;
            new KimodoClient(_probeUrl).GetHealth((ok, _, __) =>
            {
                _probe = false;
                if (!ok || !Starting) return;
                Starting = false;
                EditorApplication.update -= PollHealth;
                Debug.Log("[Kimodo] The bridge server is up.");
                _onReady?.Invoke(true, "The server is up.");
                _onReady = null;
            });
        }

        private static double _nextPoll;
        private static bool _probe;
        private static string _probeUrl = "http://127.0.0.1:8765";

        /// <summary>Where PollHealth looks; set from the Bridge's URL when starting.</summary>
        public static string ProbeUrl { get => _probeUrl; set => _probeUrl = value; }

        private static void Fail(string why, Action<bool, string> onReady)
        {
            Problem = why;
            Debug.LogError("[Kimodo] " + why);
            onReady?.Invoke(false, why);
        }

        private static void Forget() => EditorPrefs.DeleteKey(PidKey);

        private static bool IsAlive(int pid)
        {
            try { return !Process.GetProcessById(pid).HasExited; }
            catch { return false; }   // no such process
        }

        private static int SafeExitCode(Process p)
        {
            try { return p.ExitCode; } catch { return -1; }
        }

        private static int PortOf(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Port > 0) return uri.Port;
            return 8765;
        }
    }
}
