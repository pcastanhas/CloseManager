using Azure.Identity;
using CloseManager.Web.Data.Services;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace CloseManager.Web.Graph;

/// <summary>
/// Manages workstream file storage on SharePoint via Microsoft Graph.
/// Credentials are read from AppSetting at call time — no startup configuration needed.
/// All settings come from AppSettingService; if any are null the service returns a
/// clear ConfigurationException rather than a cryptic Graph error.
/// </summary>
public class SharePointService
{
    private readonly AppSettingService _settings;
    private readonly ILogger<SharePointService> _logger;

    public SharePointService(AppSettingService settings, ILogger<SharePointService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Uploads a file to SharePoint and returns metadata about the uploaded item.
    /// Folder path: /{EntityCode}/{Period}/{WorkstreamCode}/
    /// </summary>
    public async Task<SharePointUploadResult> UploadFileAsync(
        string entityCode,
        string period,
        string workstreamCode,
        string fileName,
        Stream fileStream,
        CancellationToken ct = default)
    {
        var client = await BuildClientAsync();
        var (siteId, driveId) = await GetSiteAndDriveAsync();

        var folder = $"{entityCode}/{period}/{workstreamCode}";
        var itemPath = $"{folder}/{fileName}";

        _logger.LogInformation(
            "Uploading file to SharePoint: {Path}", itemPath);

        // Use upload session for files > 4 MB; simple PUT for smaller files
        DriveItem? item;
        if (fileStream.Length > 4 * 1024 * 1024)
        {
            item = await UploadLargeFileAsync(client, siteId, driveId, itemPath, fileStream, ct);
        }
        else
        {
            item = await client.Sites[siteId].Drives[driveId]
                .Root.ItemWithPath(itemPath)
                .Content.PutAsync(fileStream, cancellationToken: ct);
        }

        if (item is null)
            throw new InvalidOperationException("SharePoint upload returned null item");

        return new SharePointUploadResult(
            SpDriveId: driveId,
            SpItemId: item.Id ?? string.Empty,
            SpWebUrl: item.WebUrl ?? string.Empty,
            SpRelativePath: itemPath
        );
    }

    /// <summary>
    /// Tests the Graph connection by fetching the drive root.
    /// Returns a success message or throws on failure.
    /// </summary>
    public async Task<string> TestConnectionAsync()
    {
        var client = await BuildClientAsync();
        var (siteId, driveId) = await GetSiteAndDriveAsync();

        var drive = await client.Sites[siteId].Drives[driveId].GetAsync();
        var driveName = drive?.Name ?? "(unnamed)";
        var siteName = drive?.DriveType ?? "SharePoint";
        return $"Connected — {siteName} / {driveName}";
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<GraphServiceClient> BuildClientAsync()
    {
        var tenantId  = await _settings.GetSharePointTenantIdAsync();
        var clientId  = await _settings.GetSharePointClientIdAsync();
        var secret    = await _settings.GetSharePointClientSecretAsync();

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(clientId)  ||
            string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "SharePoint is not configured. Set TenantId, ClientId, and ClientSecret in Settings.");
        }

        var credential = new ClientSecretCredential(tenantId, clientId, secret);
        return new GraphServiceClient(credential,
            new[] { "https://graph.microsoft.com/.default" });
    }

    private async Task<(string siteId, string driveId)> GetSiteAndDriveAsync()
    {
        var siteId  = await _settings.GetSharePointSiteIdAsync();
        var driveId = await _settings.GetSharePointDriveIdAsync();

        if (string.IsNullOrWhiteSpace(siteId) || string.IsNullOrWhiteSpace(driveId))
            throw new InvalidOperationException(
                "SharePoint SiteId and DriveId must be configured in Settings.");

        return (siteId, driveId);
    }

    private static async Task<DriveItem?> UploadLargeFileAsync(
        GraphServiceClient client,
        string siteId, string driveId,
        string itemPath,
        Stream fileStream,
        CancellationToken ct)
    {
        var uploadSession = await client.Sites[siteId].Drives[driveId]
            .Root.ItemWithPath(itemPath)
            .CreateUploadSession
            .PostAsync(new Microsoft.Graph.Drives.Item.Items.Item.CreateUploadSession.CreateUploadSessionPostRequestBody
            {
                Item = new DriveItemUploadableProperties
                {
                    AdditionalData = new Dictionary<string, object>
                    {
                        { "@microsoft.graph.conflictBehavior", "rename" }
                    }
                }
            }, cancellationToken: ct);

        if (uploadSession is null)
            throw new InvalidOperationException("Failed to create upload session");

        const int maxSliceSize = 320 * 1024; // 320 KB per slice (Graph requirement)
        var fileUploadTask = new LargeFileUploadTask<DriveItem>(
            uploadSession, fileStream, maxSliceSize, client.RequestAdapter);

        var uploadResult = await fileUploadTask.UploadAsync(cancellationToken: ct);
        return uploadResult.ItemResponse;
    }
}

public record SharePointUploadResult(
    string SpDriveId,
    string SpItemId,
    string SpWebUrl,
    string SpRelativePath
);
