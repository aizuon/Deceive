using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deceive
{
    public static class Utils
    {
        public static readonly string DataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Deceive");

        static Utils()
        {
            if (!Directory.Exists(DataDir))
                Directory.CreateDirectory(DataDir);
        }

        public static string DeceiveVersion
        {
            get
            {
                var version = Assembly.GetEntryAssembly()!.GetName().Version;
                return "v" + version.Major + "." + version.Minor + "." + version.Build;
            }
        }

        /**
         * Asynchronously checks if the current version of Deceive is the latest version.
         * If not, and the user has not dismissed the message before, an alert is shown.
         */
        public static async void CheckForUpdates()
        {
            try
            {
                var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.UserAgent.Add(
                    new ProductInfoHeaderValue("Deceive", DeceiveVersion));

                var response =
                    await httpClient.GetAsync("https://api.github.com/repos/molenzwiebel/deceive/releases/latest");
                string content = await response.Content.ReadAsStringAsync();
                var release = JsonSerializer.Deserialize<JsonNode>(content);
                if (release != null)
                {
                    string latestVersion = release["tag_name"]?.GetValue<string>();

                    // If failed to fetch or already latest or newer, return.
                    if (latestVersion == null)
                        return;

                    var githubVersion = new Version(latestVersion.Replace("v", string.Empty));
                    var assemblyVersion = new Version(DeceiveVersion.Replace("v", string.Empty));
                    // Earlier = -1, Same = 0, Later = 1
                    if (assemblyVersion.CompareTo(githubVersion) != -1)
                        return;

                    // Check if we have shown this before.
                    string persistencePath = Path.Combine(DataDir, "updateVersionPrompted");
                    string latestShownVersion = File.Exists(persistencePath)
                        ? await File.ReadAllTextAsync(persistencePath)
                        : string.Empty;

                    // If we have, return.
                    if (latestShownVersion == latestVersion)
                        return;

                    // Show a message and record the latest shown.
                    await File.WriteAllTextAsync(persistencePath, latestVersion);

                    var result = MessageBox.Show(
                        $"There is a new version of Deceive available: {latestVersion}. You are currently using Deceive {DeceiveVersion}. " +
                        "Deceive updates usually fix critical bugs or adapt to changes by Riot, so it is recommended that you install the latest version.\n\n" +
                        "Press OK to visit the download page, or press Cancel to continue. Don't worry, we won't bother you with this message again if you press cancel.",
                        StartupHandler.DeceiveTitle,
                        MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Information,
                        MessageBoxDefaultButton.Button1
                    );

                    if (result == DialogResult.OK)
                        // Open the url in the browser.
                        Process.Start(release["html_url"]?.GetValue<string>());
                }
            }
            catch
            {
                // Ignored.
            }
        }

        private static IEnumerable<Process> GetProcesses()
        {
            var riotCandidates = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName)
                .Where(process => process.Id != Environment.ProcessId).ToList();
            riotCandidates.AddRange(Process.GetProcessesByName("LeagueClient"));
            riotCandidates.AddRange(Process.GetProcessesByName("LoR"));
            riotCandidates.AddRange(Process.GetProcessesByName("VALORANT-Win64-Shipping"));
            riotCandidates.AddRange(Process.GetProcessesByName("RiotClientServices"));
            return riotCandidates;
        }

        // Checks if there is a running LCU/LoR/VALORANT/RC or Deceive instance.
        public static bool IsClientRunning()
        {
            return GetProcesses().Any();
        }

        // Kills the running LCU/LoR/VALORANT/RC or Deceive instance, if applicable.
        public static void KillProcesses()
        {
            foreach (var process in GetProcesses())
            {
                process.Refresh();
                if (process.HasExited)
                    continue;

                process.Kill();
                process.WaitForExit();
            }
        }

        // Checks for any installed Riot Client configuration,
        // and returns the path of the client if it does. Else, returns null.
        public static string GetRiotClientPath()
        {
            // Find the RiotClientInstalls file.
            string installPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Riot Games/RiotClientInstalls.json");
            if (!File.Exists(installPath))
                return null;

            var data = JsonSerializer.Deserialize<JsonNode>(File.ReadAllText(installPath));
            var rcPaths = new List<string>
            {
                data?["rc_default"]?.GetValue<string>(),
                data?["rc_live"]?.GetValue<string>(),
                data?["rc_beta"]?.GetValue<string>()
            };

            return rcPaths.FirstOrDefault(File.Exists);
        }
    }
}
