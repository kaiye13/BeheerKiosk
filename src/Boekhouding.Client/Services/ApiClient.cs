using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Boekhouding.Shared;

namespace Boekhouding.Client.Services;

public class ApiException(string message) : Exception(message);

/// <summary>Praat met de Boekhouding.Api-server op het netwerk.</summary>
public class ApiClient
{
    public static ApiClient Current { get; private set; } = new(ClientSettings.Load());

    private readonly HttpClient _http;
    public ClientSettings Settings { get; }

    public ApiClient(ClientSettings settings)
    {
        Settings = settings;
        _http = new HttpClient { BaseAddress = new Uri(settings.ServerUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Add("X-Api-Key", settings.ApiKey);
    }

    public static void Reconfigure(ClientSettings settings) => Current = new ApiClient(settings);

    // ---------- Endpoints ----------

    public Task<bool> PingAsync() => SafePing();
    public Task<DashboardDto> GetDashboardAsync(int year, int month) => GetAsync<DashboardDto>($"api/dashboard?year={year}&month={month}");
    public Task<List<ProductDto>> GetProductsAsync() => GetAsync<List<ProductDto>>("api/products");
    public Task<ProductDto> UpdateProductAsync(int id, UpdateProductRequest req, bool pushToShopify = false) =>
        SendAsync<ProductDto>(HttpMethod.Put, $"api/products/{id}?pushToShopify={pushToShopify.ToString().ToLowerInvariant()}", req);
    public Task<ProductDto> CreateProductAsync(CreateProductRequest req) => SendAsync<ProductDto>(HttpMethod.Post, "api/products", req);
    public Task<ProductDto> SetProductStatusAsync(int id, string status) => SendAsync<ProductDto>(HttpMethod.Post, $"api/products/{id}/status?value={status}", null);
    public Task<ProductDto> PushProductToShopifyAsync(int id) => SendAsync<ProductDto>(HttpMethod.Post, $"api/products/{id}/shopify", null);
    public Task<ShopifyImportResult> ImportShopifyProductsAsync() => SendAsync<ShopifyImportResult>(HttpMethod.Post, "api/shopify/products/import", null);
    public Task<List<StockLotDto>> GetStockLotsAsync() => GetAsync<List<StockLotDto>>("api/stock-lots");
    public Task<PurchaseAgreementDto> CreateMarginPurchaseAsync(CreateMarginPurchaseRequest req) => SendAsync<PurchaseAgreementDto>(HttpMethod.Post, "api/purchases/margin", req);
    public Task<PurchaseInvoiceDto> CreateInvoicePurchaseAsync(CreateInvoicePurchaseRequest req) => SendAsync<PurchaseInvoiceDto>(HttpMethod.Post, "api/purchases/invoice", req);
    public Task<List<PurchaseAgreementDto>> GetAgreementsAsync() => GetAsync<List<PurchaseAgreementDto>>("api/purchase-agreements");
    public Task<PurchaseAgreementDto> UpdateSellerAsync(int id, UpdateSellerRequest req) => SendAsync<PurchaseAgreementDto>(HttpMethod.Put, $"api/purchase-agreements/{id}/seller", req);
    public Task<List<PurchaseInvoiceDto>> GetInvoicesAsync() => GetAsync<List<PurchaseInvoiceDto>>("api/purchase-invoices");
    public Task<OrderDetailDto> GetOrderAsync(int id) => GetAsync<OrderDetailDto>($"api/orders/{id}");
    public Task<List<SalesOrderDto>> GetOrdersAsync(DateTime from, DateTime to) => GetAsync<List<SalesOrderDto>>($"api/orders?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
    public Task<List<SalesOrderDto>> GetOrdersAsync(int year, int month) => GetAsync<List<SalesOrderDto>>($"api/orders?year={year}&month={month}");
    public Task<SyncResultDto> SyncAsync() => SendAsync<SyncResultDto>(HttpMethod.Post, "api/sync", null);
    public Task<SyncStatusDto> GetSyncStatusAsync() => GetAsync<SyncStatusDto>("api/sync/status");
    public Task<MonthlyReportDto> GetMonthlyReportAsync(int year, int month) => GetAsync<MonthlyReportDto>($"api/reports/monthly?year={year}&month={month}");
    public Task<byte[]> ExportMonthAsync(int year, int month) => GetBytesAsync($"api/reports/monthly/export?year={year}&month={month}");
    public Task<MonthlyReportDto> GetReportAsync(DateTime from, DateTime to) => GetAsync<MonthlyReportDto>($"api/reports?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
    public Task<byte[]> ExportAsync(DateTime from, DateTime to) => GetBytesAsync($"api/reports/export?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
    public Task<byte[]> DownloadInvoiceFileAsync(int id) => GetBytesAsync($"api/purchase-invoices/{id}/file");

    // Livestream
    public Task<PixelCodeDto> GetPixelCodeAsync() => GetAsync<PixelCodeDto>("api/pixel/code");
    public Task<StreamDashboardDto> GetStreamDashboardAsync() => GetAsync<StreamDashboardDto>("api/streams/dashboard");
    public Task<StreamDto> StartStreamAsync(StartStreamRequest req) => SendAsync<StreamDto>(HttpMethod.Post, "api/streams/start", req);
    public Task StopStreamAsync() => SendNoContentAsync(HttpMethod.Post, "api/streams/stop", null);
    public Task<LabelInfoDto> GetLabelInfoAsync(string name, string? reference = null) => GetAsync<LabelInfoDto>(
        $"api/streams/label-info?name={Uri.EscapeDataString(name)}" + (reference is null ? "" : $"&reference={Uri.EscapeDataString(reference)}"));
    public Task<List<LabelJobDto>> GetPendingLabelJobsAsync() => GetAsync<List<LabelJobDto>>("api/labels/jobs/pending");
    public async Task<bool> ClaimLabelJobAsync(int id, string by)
    {
        var res = await Call(() => _http.PostAsync($"api/labels/jobs/{id}/claim?by={Uri.EscapeDataString(by)}", null));
        if (res.StatusCode == System.Net.HttpStatusCode.Conflict) return false;
        await EnsureSuccess(res);
        return true;
    }
    public Task CompleteLabelJobAsync(int id, string? error) => SendNoContentAsync(HttpMethod.Post, $"api/labels/jobs/{id}/done", new CompleteLabelJobRequest(error));
    public Task MarkLabelPrintedAsync(List<int> orderIds) => SendNoContentAsync(HttpMethod.Post, "api/orders/label-printed", new MarkLabelPrintedRequest(orderIds));

    // Opvolging
    public Task<List<FollowUpOrderDto>> GetFollowUpAsync(int days) => GetAsync<List<FollowUpOrderDto>>($"api/follow-up?days={days}");
    public Task<FollowUpOrderDto> UpdateFollowUpAsync(int id, UpdateFollowUpRequest req) => SendAsync<FollowUpOrderDto>(HttpMethod.Put, $"api/follow-up/{id}", req);

    // Klachten
    public Task<List<ComplaintDto>> GetComplaintsAsync() => GetAsync<List<ComplaintDto>>("api/complaints");
    public Task<ComplaintDetailDto> GetComplaintAsync(int id) => GetAsync<ComplaintDetailDto>($"api/complaints/{id}");
    public Task<ComplaintDetailDto> ReplyComplaintAsync(int id, ReplyRequest req) => SendAsync<ComplaintDetailDto>(HttpMethod.Post, $"api/complaints/{id}/reply", req);
    public Task UpdateComplaintAsync(int id, UpdateComplaintRequest req) => SendNoContentAsync(HttpMethod.Put, $"api/complaints/{id}", req);
    public Task CheckMailAsync() => SendNoContentAsync(HttpMethod.Post, "api/complaints/check-mail", null);

    public async Task<PurchaseInvoiceDto> UploadInvoiceFileAsync(int invoiceId, string path)
    {
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(File.OpenRead(path));
        file.Headers.ContentType = new MediaTypeHeaderValue(Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream",
        });
        content.Add(file, "file", Path.GetFileName(path));
        return await Read<PurchaseInvoiceDto>(await Call(() => _http.PostAsync($"api/purchase-invoices/{invoiceId}/file", content)));
    }

    /// <summary>Link om in de standaardbrowser te openen (sleutel in de URL, want een browser stuurt geen header mee).</summary>
    public string BrowserUrl(string relative) =>
        $"{Settings.ServerUrl.TrimEnd('/')}/{relative}?key={Uri.EscapeDataString(Settings.ApiKey)}";

    // ---------- Plumbing ----------

    private async Task<bool> SafePing()
    {
        try { return (await _http.GetAsync("api/health")).IsSuccessStatusCode; }
        catch (Exception) { return false; }
    }

    private async Task<T> GetAsync<T>(string url) => await Read<T>(await Call(() => _http.GetAsync(url)));

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body)
    {
        var req = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        return await Read<T>(await Call(() => _http.SendAsync(req)));
    }

    private async Task SendNoContentAsync(HttpMethod method, string url, object? body)
    {
        var req = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        await EnsureSuccess(await Call(() => _http.SendAsync(req)));
    }

    private async Task<byte[]> GetBytesAsync(string url)
    {
        var res = await Call(() => _http.GetAsync(url));
        await EnsureSuccess(res);
        return await res.Content.ReadAsByteArrayAsync();
    }

    private async Task<HttpResponseMessage> Call(Func<Task<HttpResponseMessage>> call)
    {
        try { return await call(); }
        catch (HttpRequestException ex) { throw new ApiException($"Server niet bereikbaar op {Settings.ServerUrl}. ({ex.Message})"); }
        catch (TaskCanceledException) { throw new ApiException($"Server antwoordt niet op tijd ({Settings.ServerUrl})."); }
    }

    private static async Task<T> Read<T>(HttpResponseMessage res)
    {
        await EnsureSuccess(res);
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task EnsureSuccess(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;
        var text = await res.Content.ReadAsStringAsync();
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new ApiException("API-sleutel klopt niet. Controleer Instellingen.");
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("detail", out var detail)) text = detail.GetString() ?? text;
        }
        catch (JsonException) { }
        throw new ApiException($"Fout van server ({(int)res.StatusCode}): {text}");
    }
}

public record ShopifyImportResult(int Changed);

public record PixelCodeDto(string Code, string Server, List<string> Warnings);
