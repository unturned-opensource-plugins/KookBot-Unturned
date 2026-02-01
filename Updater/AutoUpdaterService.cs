using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Rocket.Core.Logging;

namespace Emqo.KookBot_Unturned.Updater
{
	internal static class AutoUpdaterService
	{
		private static Timer _timer;
		private static int _running;  // 0 = not running, 1 = running (for Interlocked)

		private static string BotToken => KookBot_UnturnedPlugin.Instance?.Configuration?.Instance?.BotToken;
		private static string ChannelId => KookBot_UnturnedPlugin.Instance?.Configuration?.Instance?.ChannelId;

		public static void Start()
		{
			try
			{
				var cfg = KookBot_UnturnedPlugin.Instance?.Configuration?.Instance;
				if (cfg == null || !cfg.AutoUpdateEnabled)
				{
					return;
				}

				var interval = Math.Max(cfg.UpdateCheckIntervalMinutes, 10);
				_timer = new Timer(async _ => await SafeCheckAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(interval));
				Logger.Log($"🔔 AutoUpdaterService started (every {interval} min).");
			}
			catch (Exception ex)
			{
				Logger.LogError($"AutoUpdaterService.Start error: {ex.Message}");
			}
		}

		public static void Stop()
		{
			try
			{
				_timer?.Dispose();
				_timer = null;
				Logger.Log("🔕 AutoUpdaterService stopped.");
			}
			catch (Exception ex)
			{
				Logger.LogError($"Error stopping AutoUpdaterService: {ex.Message}");
			}
		}

		private static async Task SafeCheckAsync()
		{
			// Use Interlocked for thread-safe check-and-set
			if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
			try
			{
				await CheckAndUpdateAsync();
			}
			catch (Exception ex)
			{
				Logger.LogError($"AutoUpdaterService.Check error: {ex.Message}");
			}
			finally
			{
				Interlocked.Exchange(ref _running, 0);
			}
		}

		private static async Task CheckAndUpdateAsync()
		{
			var cfg = KookBot_UnturnedPlugin.Instance?.Configuration?.Instance;
			if (cfg == null) return;

			var owner = string.IsNullOrWhiteSpace(cfg.UpdateRepoOwner) ? "Emqo" : cfg.UpdateRepoOwner;
			var repo = string.IsNullOrWhiteSpace(cfg.UpdateRepoName) ? "KookBot-Unturned" : cfg.UpdateRepoName;

			// Get current version from assembly informational version
			var currentVersion = GetAssemblyVersion();

			var apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases";
			var releasesJson = await HttpGetAsync(apiUrl);
			if (string.IsNullOrWhiteSpace(releasesJson)) return;

			var releases = JArray.Parse(releasesJson);
			JObject latest = null;
			foreach (var r in releases)
			{
				var prerelease = r.Value<bool?>("prerelease") ?? false;
				if (!cfg.IncludePrereleases && prerelease) continue;
				latest = (JObject)r;
				break;
			}
			if (latest == null) return;

			var tag = latest.Value<string>("tag_name") ?? "";
            if (!IsNewer(tag, currentVersion))
			{
                try { Logger.Log($"�?Up to date. Latest {tag}, current {currentVersion}"); } catch {}
				return;
			}

            // Log detect
            try
            {
                Logger.Log($"⬇️ Update detected: latest {tag}, current {currentVersion}");
            }
            catch { }

            var asset = FindDllAsset(latest);
			if (asset == null)
			{
				Logger.LogWarning("AutoUpdater: No DLL asset found in latest release.");
				return;
			}

			var name = asset.Value<string>("name");
			var downloadUrl = asset.Value<string>("browser_download_url");
            var releasePage = latest.Value<string>("html_url") ?? "";

            NotifyKook($"⬇️ New version detected: {tag}\nCurrent: {currentVersion}\nDownloading: `{name}`{(string.IsNullOrWhiteSpace(releasePage) ? "" : $"\nRelease: {releasePage}")}");

			var tempDir = Path.Combine(Path.GetTempPath(), "KookBot-Unturned", "updates");
			Directory.CreateDirectory(tempDir);
			var tempPath = Path.Combine(tempDir, name);

			await HttpDownloadAsync(downloadUrl, tempPath);

			var dllPath = GetPluginDllPath();
			if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
			{
				Logger.LogWarning("AutoUpdater: Cannot find current DLL path.");
				return;
			}

			// staged replace
			var backupPath = dllPath + ".bak";
			var stagedPath = dllPath + ".new";
			File.Copy(tempPath, stagedPath, true);

			try
			{
				// Try atomic replace
				File.Replace(stagedPath, dllPath, backupPath, ignoreMetadataErrors: true);
				NotifyKook($"�?Updated to {tag}. {(cfg.AutoRestartAfterUpdate ? "Restarting..." : "Please restart server to take effect.")}");
			}
			catch (Exception ex)
			{
				// If locked, leave .new and set pending flag
				Logger.LogError($"AutoUpdater: Failed to replace DLL: {ex.Message}");
				var pendingFlag = Path.Combine(Path.GetDirectoryName(dllPath) ?? ".", "KookBot-Unturned.update.pending");
				File.WriteAllText(pendingFlag, tempPath, Encoding.UTF8);
				NotifyKook($"⚠️ Update staged for {tag}. Restart required to apply.");
			}

			// Optional auto-restart
			if (cfg.AutoRestartAfterUpdate)
			{
				try
				{
					// Best-effort: request graceful shutdown; external supervisor should restart
					SDG.Unturned.Provider.shutdown();
				}
				catch (Exception ex)
				{
					Logger.LogError($"AutoUpdater: failed to trigger restart: {ex.Message}");
				}
			}
		}

		private static string GetPluginDllPath()
		{
			try
			{
				var asm = Assembly.GetExecutingAssembly().Location;
				return asm;
			}
			catch
			{
				return null;
			}
		}

		private static string GetAssemblyVersion()
		{
			try
			{
				var asm = Assembly.GetExecutingAssembly();
				var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
				if (!string.IsNullOrWhiteSpace(info)) return info;
				var fileVer = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
				if (!string.IsNullOrWhiteSpace(fileVer)) return fileVer;
				return asm.GetName().Version?.ToString() ?? "0.0.0";
			}
			catch
			{
				return "0.0.0";
			}
		}

        private static bool IsNewer(string tag, string current)
		{
			// tag format: v1.2.3 or 1.2.3
			try
			{
				if (string.IsNullOrWhiteSpace(tag)) return false;
                var t = NormalizeVersion(tag);
                var c = NormalizeVersion(current);
				Version tv, cv;
				if (!Version.TryParse(t, out tv)) return false;
                if (!Version.TryParse(c, out cv)) return true; // if current unparsable, treat as older
                return tv > cv;
			}
			catch
			{
				return false;
			}
		}

        private static string NormalizeVersion(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return "0.0.0";
            var s = v.Trim();
            s = s.TrimStart('v', 'V');
            // strip build metadata or pre-release suffixes (e.g., 1.2.3+abc, 1.2.3-rc1)
            var plus = s.IndexOf('+');
            if (plus > 0) s = s.Substring(0, plus);
            var dash = s.IndexOf('-');
            if (dash > 0) s = s.Substring(0, dash);
            return s;
        }

		private static JObject FindDllAsset(JObject release)
		{
			try
			{
				var assets = (JArray)release["assets"];
				if (assets == null) return null;
				foreach (var a in assets)
				{
					var name = a.Value<string>("name") ?? "";
					if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
					{
						return (JObject)a;
					}
				}
				return null;
			}
			catch
			{
				return null;
			}
		}

		private static async Task<string> HttpGetAsync(string url)
		{
			var req = (HttpWebRequest)WebRequest.Create(url);
			req.Method = "GET";
			req.UserAgent = "KookBot-Unturned-Updater";
			req.Timeout = 8000;
			using var resp = (HttpWebResponse)await req.GetResponseAsync();
			using var sr = new StreamReader(resp.GetResponseStream());
			return await sr.ReadToEndAsync();
		}

		private static async Task HttpDownloadAsync(string url, string path)
		{
			var req = (HttpWebRequest)WebRequest.Create(url);
			req.Method = "GET";
			req.Timeout = 60000;  // 60秒下载超时
			req.ReadWriteTimeout = 60000;
			req.UserAgent = "KookBot-Unturned-Updater";
			using var resp = (HttpWebResponse)await req.GetResponseAsync();
			using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
			await resp.GetResponseStream().CopyToAsync(fs);
		}

		private static void NotifyKook(string text)
		{
			try
			{
				if (string.IsNullOrWhiteSpace(BotToken) || string.IsNullOrWhiteSpace(ChannelId))
				{
					return;
				}
				var msg = new KookApi.Message(BotToken);
				// Use markdown card for consistency
				var card = KookApi.KookCardFactory.BuildMarkdownCard("⬆️", "Auto Update", text, DateTimeOffset.Now, "info");
				_ = msg.CreateMessageAsync(10, ChannelId, card);
			}
			catch
			{
			}
		}
	}
}


