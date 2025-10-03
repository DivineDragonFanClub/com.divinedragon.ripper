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

        private const int MaxRetries = 10;
        private const int RetryDelayMs = 100;

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
                Debug.LogError("AssetRipper executablePath is null or empty");
                return;
            }
            
            // Start the process while the class instance is running
            _processThread = new Thread(() =>
            {
                try
                {
                    ProcessStartInfo ps = new ProcessStartInfo(executablePath)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = false,
                        Arguments = "--launch-browser=false --port=6969",
                        CreateNoWindow = true
                    };

                    _assetRipperProcess = new Process();
                    _assetRipperProcess.StartInfo = ps;
                    _assetRipperProcess.Start();
                    
                    while (!_assetRipperProcess.HasExited)
                    {
                        if (_cancelRequested)
                        {
                            try
                            {
                                _assetRipperProcess.Kill();
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
                    _assetRipperProcess?.Dispose();
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

            for (int i = 0; i < MaxRetries; i++)
            {
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
                    Debug.Log($"Retry count {i}");
                    
                    // Ignore the exceptions
                }

                Thread.Sleep(RetryDelayMs);
            }

            if (!apiReady)
            {
                Debug.LogError("AssetRipper process did not respond in time.");
                Dispose();
                _assetRipperProcess = null;
                _cancelRequested = false;
            }
        }
        
        public bool SetDefaultUnityVersion()
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "DefaultVersion", Application.unityVersion },
                { "LightmapTextureExportFormat", "Image" },
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