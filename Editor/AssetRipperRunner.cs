using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DivineDragon
{
    // TODO: Rework so we create and destroy a temp directory automatically?
    // TODO: Rework as a static class? What would the benefits be?
    public class AssetRipperRunner : IDisposable
    {
        private const string APIUrl = "http://localhost:6969";
        private static Thread _processThread;
        private static Process _assetRipperProcess;
        private static HttpClient _apiClient;
        private static volatile bool _cancelRequested;
        private static readonly StringBuilder _processOutput = new StringBuilder();
        private static readonly Dictionary<string, string> _argumentsCache = new Dictionary<string, string>();

        private const int MaxRetries = 150;
        private const int RetryDelayMs = 200;

        public bool Running => _processThread is { IsAlive: true };

        public AssetRipperRunner(string executablePath)
        {
            if (Running)
            {
                Debug.Log("AssetRipper is already running?");
                return;
            }

            if (string.IsNullOrEmpty(executablePath))
            {
                throw new InvalidOperationException(
                    "AssetRipper executable path is not configured. " +
                    "Set it in Preferences > Divine Ripper.");
            }

            if (!System.IO.File.Exists(executablePath))
            {
                throw new InvalidOperationException(
                    $"AssetRipper executable not found at configured path: {executablePath}. " +
                    "Update it in Preferences > Divine Ripper.");
            }

            lock (_processOutput) { _processOutput.Clear(); }

            string arguments = BuildLaunchArguments(executablePath);

            // Start the process while the class instance is running
            _processThread = new Thread(() =>
            {
                Process process = null;
                try
                {
                    ProcessStartInfo ps = new ProcessStartInfo(executablePath)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        Arguments = arguments,
                        CreateNoWindow = true
                    };

                    process = new Process { StartInfo = ps };
                    process.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data != null) lock (_processOutput) _processOutput.AppendLine(e.Data);
                    };
                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data != null) lock (_processOutput) _processOutput.AppendLine(e.Data);
                    };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    _assetRipperProcess = process;

                    while (!process.HasExited)
                    {
                        if (_cancelRequested)
                        {
                            try
                            {
                                process.Kill();
                            }
                            catch
                            {
                                // We don't care about the possible errors here. Let it fail silently
                            }
                            break;
                        }

                        Thread.Sleep(200);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("Process error: " + ex.Message);
                }
                finally
                {
                    process?.Dispose();
                    _assetRipperProcess = null;
                    _cancelRequested = false;

                    Debug.Log("AssetRipperRunner thread is stopped");
                }
            });
            
            _processThread.Start();
            
            // Wait 500ms to ensure the client had time to start and initialize. Jank but that's all we've got ATM.
            // TODO: Find a more reliable way
            // Thread.Sleep(500);
            
            _apiClient = new HttpClient();
            
            bool apiReady = false;

            bool processDiedEarly = false;

            for (int i = 0; i < MaxRetries; i++)
            {
                var proc = _assetRipperProcess;
                if (proc != null)
                {
                    bool exited = false;
                    try { exited = proc.HasExited; }
                    catch (InvalidOperationException) { }

                    if (exited)
                    {
                        processDiedEarly = true;
                        break;
                    }
                }

                try
                {
                    var response = _apiClient.GetAsync("http://localhost:6969").Result;

                    if (response.IsSuccessStatusCode)
                    {
                        apiReady = true;
                        break;
                    }
                }
                catch (Exception)
                {
                }

                Thread.Sleep(RetryDelayMs);
            }

            if (!apiReady)
            {
                int? exitCode = null;
                try
                {
                    var proc = _assetRipperProcess;
                    if (proc != null && proc.HasExited) exitCode = proc.ExitCode;
                }
                catch { }

                string output;
                lock (_processOutput) { output = _processOutput.ToString().Trim(); }

                Dispose();
                _assetRipperProcess = null;
                _cancelRequested = false;

                string reason = processDiedEarly || exitCode.HasValue
                    ? $"AssetRipper exited before the HTTP API came up (exit code {exitCode?.ToString() ?? "unknown"})."
                    : $"AssetRipper did not respond on http://localhost:6969 within {(MaxRetries * RetryDelayMs) / 1000}s.";

                string detail = string.IsNullOrEmpty(output)
                    ? "(no stdout/stderr captured)"
                    : output;

                throw new InvalidOperationException(
                    reason +
                    " Verify the executable path in Preferences > Divine Ripper points to a working AssetRipper build " +
                    "(the headless server build, not the GUI one).\n" +
                    "--- AssetRipper output ---\n" + detail);
            }
        }
        
        public bool SetDefaultUnityVersion()
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "DefaultVersion", Application.unityVersion },
                { "LightmapTextureExportFormat", "Image" },
                { "ShaderExportMode", "Disassembly" },
            });

            var result = _apiClient.PostAsync(APIUrl + "/Settings/Update", form).Result;

            if (!result.IsSuccessStatusCode)
                throw new AssetRipperApiException(result.StatusCode, result.ReasonPhrase);

            return result.IsSuccessStatusCode;
        }

        public bool AddFile(string path)
        {
            // TODO: Move to FormUrlEncodedContent
            var response = _apiClient.PostAsync(APIUrl + "/LoadFile", new StringContent($"path={path}", Encoding.ASCII, "application/x-www-form-urlencoded")).Result;

            if (!response.IsSuccessStatusCode)
                throw new AssetRipperApiException(response.StatusCode, response.ReasonPhrase);

            return response.IsSuccessStatusCode;
        }

        public bool LoadFolder(string path)
        {
            var apiUrl = APIUrl;
            var response = _apiClient.PostAsync(apiUrl + "/LoadFolder", new StringContent($"path={path}", Encoding.ASCII, "application/x-www-form-urlencoded")).Result;

            if (!response.IsSuccessStatusCode)
                throw new AssetRipperApiException(response.StatusCode, response.ReasonPhrase);

            return response.IsSuccessStatusCode;
        }

        public bool ExportProject(string path)
        {
            // TODO: Move to FormUrlEncodedContent
            var response = _apiClient.PostAsync(APIUrl + "/Export/UnityProject", new StringContent($"path={path}", Encoding.ASCII, "application/x-www-form-urlencoded")).Result;

            if (!response.IsSuccessStatusCode)
                throw new AssetRipperApiException(response.StatusCode, response.ReasonPhrase);

            return response.IsSuccessStatusCode;
        }
        
        private static string BuildLaunchArguments(string executablePath)
        {
            string cacheKey = MakeProbeCacheKey(executablePath);

            if (_argumentsCache.TryGetValue(cacheKey, out string cached))
                return cached;

            const string newArgs = "--headless --port 6969";
            const string oldArgs = "--launch-browser=false --port=6969";

            string args = newArgs;
            string help = ProbeHelp(executablePath);

            if (!string.IsNullOrEmpty(help))
            {
                bool hasHeadless = help.IndexOf("--headless", StringComparison.Ordinal) >= 0;
                bool hasLaunchBrowser = help.IndexOf("--launch-browser", StringComparison.Ordinal) >= 0;

                if (hasHeadless)
                {
                    args = newArgs;
                }
                else if (hasLaunchBrowser)
                {
                    args = oldArgs;
                    Debug.LogWarning(
                        "Detected an older AssetRipper build (uses --launch-browser). " +
                        "Consider updating to a newer AssetRipper release.");
                }
                else
                {
                    Debug.LogWarning(
                        "Could not detect AssetRipper command-line flavor from --help output; " +
                        "defaulting to '" + newArgs + "'. Help output was:\n" + help);
                }
            }
            else
            {
                Debug.LogWarning(
                    "AssetRipper --help probe returned no output; defaulting to '" + newArgs + "'.");
            }

            _argumentsCache[cacheKey] = args;
            Debug.Log($"AssetRipper launch arguments: {args}");
            return args;
        }

        private static string MakeProbeCacheKey(string executablePath)
        {
            try
            {
                var info = new System.IO.FileInfo(executablePath);
                return $"{executablePath}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
            }
            catch
            {
                return executablePath;
            }
        }

        private static string ProbeHelp(string executablePath)
        {
            try
            {
                var ps = new ProcessStartInfo(executablePath)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    Arguments = "--help",
                    CreateNoWindow = true
                };

                using (var process = new Process { StartInfo = ps })
                {
                    var output = new StringBuilder();
                    process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                    process.ErrorDataReceived  += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(5000))
                    {
                        try { process.Kill(); } catch { }
                        Debug.LogWarning("AssetRipper --help probe timed out after 5s.");
                    }

                    return output.ToString();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AssetRipper --help probe failed: {ex.Message}");
                return string.Empty;
            }
        }

        public void Dispose()
        {
            // Make sure we close the thread when the instance is dropped so that it doesn't hog resources in the background
            if (Running & _assetRipperProcess != null)
            {
                // Close the API client before AssetRipper (I suppose we'd avoid errors this way?)
                _apiClient.CancelPendingRequests();
                _apiClient.Dispose();
                _apiClient = null;
                
                // Signal to the thread that we're done here
                _cancelRequested = true;
                
                // Wait for the Thread to end
                _processThread.Join();
                _processThread = null;
                
                Debug.Log("Running Dispose for AssetRipperRunner");
            }
        }
    }
}