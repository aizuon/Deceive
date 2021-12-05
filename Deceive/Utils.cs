using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

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
                var release = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(content);
                string latestVersion = release["tag_name"].Deserialize<string>();

                // If failed to fetch or already latest or newer, return.
                if (latestVersion == null)
                    return;

                var githubVersion = new Version(latestVersion.Replace("v", ""));
                var assemblyVersion = new Version(DeceiveVersion.Replace("v", ""));
                // Earlier = -1, Same = 0, Later = 1
                if (assemblyVersion.CompareTo(githubVersion) != -1)
                    return;

                // Check if we have shown this before.
                string persistencePath = Path.Combine(DataDir, "updateVersionPrompted");
                string latestShownVersion = File.Exists(persistencePath) ? File.ReadAllText(persistencePath) : "";

                // If we have, return.
                if (latestShownVersion == latestVersion)
                    return;

                // Show a message and record the latest shown.
                File.WriteAllText(persistencePath, latestVersion);

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
                    Process.Start(release["html_url"].Deserialize<string>());
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

            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(installPath));
            var rcPaths = new List<string>();

            if (data.ContainsKey("rc_default"))
                rcPaths.Add(data["rc_default"].Deserialize<string>());

            if (data.ContainsKey("rc_live"))
                rcPaths.Add(data["rc_live"].Deserialize<string>());

            if (data.ContainsKey("rc_beta"))
                rcPaths.Add(data["rc_beta"].Deserialize<string>());

            return rcPaths.FirstOrDefault(File.Exists);
        }
    }
}
