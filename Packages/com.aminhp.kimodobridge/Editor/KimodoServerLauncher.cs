// SPDX-License-Identifier: Apache-2.0
// Starts and stops the Python bridge server from inside Unity, so you do not have to keep a terminal
// around: press Start server on the KimodoBridge and it runs, watches its output, and says in the
// Console whether it actually came up.
//
// It launches the interpreter DIRECTLY — `python -m kimodo_bridge --host … --port …` — rather than a
// shell script. That is what makes this portable: the shell script only ever existed to pick the venv's
// interpreter and set one environment variable, both of which are one line of C# each. No PowerShell
// means no execution-policy failures, no process tree to chase when stopping, and the same code path on
// Windows, macOS and Linux — only the interpreter's own location inside a venv differs.
//
// The server module itself ships in the package under `Server~` (Unity ignores folders ending in `~`),
// so the only thing to configure is which Python to use.
//
// Three things this has to get right:
//
//  * OUTPUT. The server's stdout/stderr arrive on background threads, where touching the Unity API is
//    not allowed, so lines are queued and drained on EditorApplication.update. Only failures and a few
//    milestones are logged (a model load prints a lot); the rest goes to a rolling buffer the Bridge
//    inspector can show, so nothing is hidden but the Console stays readable.
//  * FAILURES. "It did not start" is useless on its own. The common causes — Kimodo missing from the
//    environment, a wrong interpreter, the port already taken — are recognised in the output and
//    reported as what to do about them.
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
        /// <summary>Raised when the server we started goes away — stopped here, or exited on its own.
        /// The connection state is not observable (there is no socket: "connected" only means a past
        /// /health answered), so without this the UI keeps showing green against a server that is gone.</summary>
        public static event System.Action Stopped;

        private const string PythonPref = "Kimodo.PythonPath";
        private const string ServerDirPref = "Kimodo.ServerDir";
        private const string CpuEncoderPref = "Kimodo.TextEncoderCpu";
        private const string RemotePref = "Kimodo.AllowRemote";
        private const string PidPref = "Kimodo.ServerPid";
        private const int MaxLogLines = 400;

        private static Process _proc;                       // null after a domain reload, even while running
        private static readonly Queue<string> _pending = new Queue<string>();
        private static readonly List<string> _log = new List<string>();
        private static bool _pumping;
        private static double _waitUntil, _nextPoll;
        private static bool _probe;
        private static Action<bool, string> _onReady;

        /// <summary>Rolling tail of the server's own output (newest last).</summary>
        public static IReadOnlyList<string> Log => _log;

        /// <summary>What went wrong last, in words the user can act on. Empty when fine.</summary>
        public static string Problem { get; private set; } = "";

        /// <summary>True while we are waiting for /health to answer after a start.</summary>
        public static bool Starting { get; private set; }

        /// <summary>Where PollHealth looks; set from the Bridge's URL when starting.</summary>
        public static string ProbeUrl { get; set; } = "http://127.0.0.1:8765";

        static KimodoServerLauncher()
        {
            int pid = EditorPrefs.GetInt(PidKey, 0);
            if (pid != 0 && !IsAlive(pid)) Forget();   // it died while we were reloading
        }

        // Per-project, so two checkouts do not fight over one PID.
        private static string PidKey => PidPref + "." + Application.dataPath.GetHashCode();

        // ---- settings ---------------------------------------------------------------------------

        /// <summary>The Python interpreter to run the server with — a virtual environment that has
        /// Kimodo installed. Remembered per machine: it is not project data.</summary>
        public static string PythonPath
        {
            get
            {
                string p = EditorPrefs.GetString(PythonPref, "");
                return string.IsNullOrEmpty(p) ? GuessPython() : p;
            }
            set { EditorPrefs.SetString(PythonPref, value ?? ""); Problem = ""; }
        }

        /// <summary>Folder containing the `kimodo_bridge` package. Defaults to the copy bundled with
        /// this package, so there is normally nothing to set.</summary>
        public static string ServerDir
        {
            get
            {
                string d = EditorPrefs.GetString(ServerDirPref, "");
                return string.IsNullOrEmpty(d) ? BundledServerDir() : d;
            }
            set { EditorPrefs.SetString(ServerDirPref, value ?? ""); Problem = ""; }
        }

        /// <summary>Keep the ~8B text encoder on the CPU (what makes it fit in 8 GB of VRAM).</summary>
        public static bool TextEncoderOnCpu
        {
            get => EditorPrefs.GetBool(CpuEncoderPref, true);
            set => EditorPrefs.SetBool(CpuEncoderPref, value);
        }

        /// <summary>Bind to 0.0.0.0 instead of just this machine, so another device (a second PC, a
        /// headset) can reach the server. Off by default: the API has no authentication, so opening it
        /// to the network is a choice, not a default.</summary>
        public static bool AllowRemoteClients
        {
            get => EditorPrefs.GetBool(RemotePref, false);
            set => EditorPrefs.SetBool(RemotePref, value);
        }

        /// <summary>True when the URL points at this machine — the only case where Start makes sense.
        /// A URL naming another host means the server lives there and is started there.</summary>
        public static bool IsLocalUrl(string url)
        {
            if (!System.Uri.TryCreate(url, System.UriKind.Absolute, out var uri)) return true;
            string h = uri.Host;
            return h == "127.0.0.1" || h == "localhost" || h == "::1" || h == "0.0.0.0";
        }

        public static bool UsingBundledServer =>
            string.IsNullOrEmpty(EditorPrefs.GetString(ServerDirPref, ""));

        public static bool PythonIsSet => File.Exists(PythonPath);

        /// <summary>The `Server~` folder inside this package (`~` keeps Unity from importing it).
        /// Resolved through the package manager so it works whether the package sits in Packages/ or
        /// has been installed from git into the read-only cache.</summary>
        public static string BundledServerDir()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(
                typeof(KimodoServerLauncher).Assembly);
            string root = info != null ? info.resolvedPath
                                       : Path.GetFullPath("Packages/com.aminhp.kimodobridge");
            return Path.Combine(root, "Server~");
        }

        /// <summary>Look for a virtual environment near the project, the way one is usually laid out.
        /// The interpreter lives in `Scripts` on Windows and `bin` everywhere else.</summary>
        public static string GuessPython()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath) ?? ".";
            string up1 = Path.GetDirectoryName(projectRoot) ?? projectRoot;
            string up2 = Path.GetDirectoryName(up1) ?? up1;

            foreach (var root in new[] { projectRoot, up1, up2 })
                foreach (var venv in new[] { "venv", ".venv", "env" })
                    foreach (var exe in InterpreterNames)
                    {
                        string candidate = Path.Combine(root, venv, exe);
                        if (File.Exists(candidate)) return candidate;
                    }
            return "";   // nothing found: the user picks one
        }

        private static string[] InterpreterNames =>
            Application.platform == RuntimePlatform.WindowsEditor
                ? new[] { Path.Combine("Scripts", "python.exe") }
                : new[] { Path.Combine("bin", "python3"), Path.Combine("bin", "python") };

        // ---- state ------------------------------------------------------------------------------
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
        /// <summary>Make sure a server is serving this URL, then report back. <paramref name="onReady"/>
        /// fires once /health answers, or with false when it gives up / the process dies first.
        ///
        /// It does NOT blindly launch one. The PID we track only covers servers *this project* started,
        /// so another Unity project — or a terminal — can already be serving the port, and starting a
        /// second one would only collide on it. If something already answers, that one is adopted.</summary>
        public static void Start(string serverUrl, string preloadModel, Action<bool, string> onReady = null)
        {
            if (IsRunning) { onReady?.Invoke(true, "The server is already running."); return; }

            ProbeUrl = serverUrl;
            new KimodoClient(serverUrl).GetHealth((alive, _, __) =>
            {
                if (alive)
                {
                    Problem = "";
                    Debug.Log($"[Kimodo] A bridge server is already running at {serverUrl} — using that one " +
                              "instead of starting another.");
                    onReady?.Invoke(true, "A server was already running here; connected to it.");
                    return;
                }
                StartProcess(serverUrl, preloadModel, onReady);
            });
        }

        private static void StartProcess(string serverUrl, string preloadModel, Action<bool, string> onReady)
        {
            string python = PythonPath;
            if (string.IsNullOrEmpty(python) || !File.Exists(python))
            {
                Fail("Set Python to the interpreter of an environment that has Kimodo installed " +
                     "(venv/Scripts/python.exe on Windows, venv/bin/python elsewhere).", onReady);
                return;
            }

            string serverDir = ServerDir;
            if (!Directory.Exists(Path.Combine(serverDir, "kimodo_bridge")))
            {
                Fail($"No 'kimodo_bridge' folder in '{serverDir}'.", onReady);
                return;
            }

            var uri = ParseUrl(serverUrl);
            var psi = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = serverDir,     // `-m` puts the working directory on sys.path
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-u");           // unbuffered: otherwise output arrives in lumps
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("kimodo_bridge");
            psi.ArgumentList.Add("--host");
            psi.ArgumentList.Add(AllowRemoteClients ? "0.0.0.0" : uri.host);
            psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(uri.port.ToString());
            if (!string.IsNullOrWhiteSpace(preloadModel))
            {
                psi.ArgumentList.Add("--preload");
                psi.ArgumentList.Add(preloadModel);
            }
            // The rest of the environment is inherited (HF_HOME and friends keep working).
            psi.Environment["PYTHONPATH"] = serverDir;
            if (TextEncoderOnCpu) psi.Environment["TEXT_ENCODER_DEVICE"] = "cpu";

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
                Fail($"Could not run '{python}': {e.Message}", onReady);
                return;
            }

            Debug.Log($"[Kimodo] Starting the bridge server (PID {_proc.Id}) — {python} -m kimodo_bridge");
            Starting = true;
            _onReady = onReady;
            // Loading the ~8B text encoder is slow, and -Preload makes the first start slower still.
            _waitUntil = EditorApplication.timeSinceStartup + 300.0;
            StartPump();
            EditorApplication.update -= PollHealth;
            EditorApplication.update += PollHealth;
        }

        /// <summary>Stop the server we started.</summary>
        public static void Stop()
        {
            int pid = RunningPid;
            if (pid == 0) return;
            try
            {
                // One process, no shell wrapper, so a plain Kill is enough — and it is the same call on
                // every platform. (The PowerShell version needed taskkill /T to reach its python child.)
                var p = _proc != null && !_proc.HasExited ? _proc : Process.GetProcessById(pid);
                p.Kill();
                p.WaitForExit(5000);
            }
            catch (Exception e) { Debug.LogWarning("[Kimodo] Could not stop the server: " + e.Message); }

            _proc = null;
            Forget();
            Starting = false;
            EditorApplication.update -= PollHealth;
            Debug.Log("[Kimodo] Bridge server stopped.");
            Stopped?.Invoke();
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
                Stopped?.Invoke();
            }
        }

        // Turn the output that matters into something actionable, and let the rest sit in the buffer.
        private static void Classify(string line)
        {
            if (line.IndexOf("No module named", StringComparison.OrdinalIgnoreCase) >= 0 ||
                line.IndexOf("ModuleNotFoundError", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Which module is missing says which of three different things went wrong.
                bool bridge = line.IndexOf("kimodo_bridge", StringComparison.OrdinalIgnoreCase) >= 0;
                bool kimodo = !bridge && line.IndexOf("kimodo", StringComparison.OrdinalIgnoreCase) >= 0;
                Problem = (bridge
                    ? "Python could not find the bridge server itself — the Server folder under Setup is " +
                      "not a folder containing 'kimodo_bridge'. Clear it to use the bundled copy."
                    : kimodo
                    ? "That Python environment does not have Kimodo installed — install it there " +
                      "(pip install -e . in the Kimodo repo) and start again."
                    : "A Python package the server needs is missing from that environment.") +
                    "  (" + line.Trim() + ")";
                Debug.LogError("[Kimodo] " + Problem);
            }
            else if (line.IndexOf("address already in use", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     line.IndexOf("only one usage of each socket address", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Problem = "That port is already taken — a bridge server is already running on it (another " +
                          "Unity project, or a terminal). Press Connect to use it, or stop it where it was " +
                          "started.";
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
            new KimodoClient(ProbeUrl).GetHealth((ok, _, __) =>
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

        private static (string host, int port) ParseUrl(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Port > 0)
                return (uri.Host, uri.Port);
            return ("127.0.0.1", 8765);
        }
    }
}
