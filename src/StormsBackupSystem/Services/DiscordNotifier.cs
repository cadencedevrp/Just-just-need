using System.Text;
using System.Text.Json.Nodes;
using StormsBackupSystem.Core;

namespace StormsBackupSystem.Services;

/// <summary>Optional: posts the result of every backup to a Discord channel webhook.</summary>
public static class DiscordNotifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task SendAsync(string webhookUrl, BackupResult result)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return;
        }

        var description = new StringBuilder(result.Summary);
        foreach (var error in result.Errors.Take(5))
        {
            description.Append("\n❌ ").Append(Truncate(error, 300));
        }

        foreach (var warning in result.Warnings.Take(5))
        {
            description.Append("\n⚠️ ").Append(Truncate(warning, 300));
        }

        var payload = new JsonObject
        {
            ["username"] = AppPaths.AppName,
            ["embeds"] = new JsonArray
            {
                new JsonObject
                {
                    ["title"] = result.Success ? "✅ Server backup completed" : "❌ Server backup FAILED",
                    ["description"] = Truncate(description.ToString(), 3900),
                    ["color"] = result.Success ? (result.Warnings.Count > 0 ? 0xF1C40F : 0x2ECC71) : 0xE74C3C,
                    ["footer"] = new JsonObject { ["text"] = Environment.MachineName },
                    ["timestamp"] = DateTime.UtcNow.ToString("o"),
                },
            },
        };

        try
        {
            using var response = await Http.PostAsync(
                webhookUrl.Trim(),
                new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"));
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"Discord notification failed: {(int)response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Discord notification failed: " + ex.Message);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
