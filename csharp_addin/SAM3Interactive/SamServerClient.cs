using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SAM3Interactive
{
    // ------------------------------------------------------------------
    // JSON DTOs (snake_case wire format shared with sam_server.py)
    // ------------------------------------------------------------------

    internal class ExtentDto
    {
        [JsonPropertyName("xmin")] public double XMin { get; set; }
        [JsonPropertyName("ymin")] public double YMin { get; set; }
        [JsonPropertyName("xmax")] public double XMax { get; set; }
        [JsonPropertyName("ymax")] public double YMax { get; set; }
    }

    internal class SetImageRequest
    {
        [JsonPropertyName("raster_path")] public string RasterPath { get; set; }
        [JsonPropertyName("extent")] public ExtentDto Extent { get; set; }
        [JsonPropertyName("extent_sr_wkt")] public string ExtentSrWkt { get; set; }
        [JsonPropertyName("max_size")] public int MaxSize { get; set; }
        [JsonPropertyName("engine")] public string Engine { get; set; }
        [JsonPropertyName("model_id")] public string ModelId { get; set; }
        [JsonPropertyName("ritm_checkpoint")] public string RitmCheckpoint { get; set; }
    }

    internal class ImageInfoDto
    {
        [JsonPropertyName("cols")] public int Cols { get; set; }
        [JsonPropertyName("rows")] public int Rows { get; set; }
        [JsonPropertyName("xmin")] public double XMin { get; set; }
        [JsonPropertyName("ymin")] public double YMin { get; set; }
        [JsonPropertyName("xmax")] public double XMax { get; set; }
        [JsonPropertyName("ymax")] public double YMax { get; set; }
        [JsonPropertyName("cell_w")] public double CellW { get; set; }
        [JsonPropertyName("cell_h")] public double CellH { get; set; }
        [JsonPropertyName("sr_wkt")] public string SrWkt { get; set; }
    }

    internal class SetImageResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("error")] public string Error { get; set; }
        [JsonPropertyName("device")] public string Device { get; set; }
        [JsonPropertyName("image")] public ImageInfoDto Image { get; set; }
    }

    internal class PredictRequest
    {
        [JsonPropertyName("points")] public List<double[]> Points { get; set; }
        [JsonPropertyName("labels")] public List<int> Labels { get; set; }
        [JsonPropertyName("simplify_tolerance")]
        public double SimplifyTolerance { get; set; } = 1.0;
    }

    internal class PredictResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("error")] public string Error { get; set; }
        [JsonPropertyName("score")] public double Score { get; set; }
        [JsonPropertyName("rings")]
        public List<List<double[]>> Rings { get; set; }
    }

    internal class PingResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; }
        [JsonPropertyName("device")] public string Device { get; set; }
    }

    // ------------------------------------------------------------------
    // HTTP client
    // ------------------------------------------------------------------

    /// <summary>Thin JSON client for the local sam_server.py process.</summary>
    internal static class SamServerClient
    {
        private static readonly HttpClient Http = new HttpClient
        {
            // set_image may include the one-time model load/download.
            Timeout = TimeSpan.FromMinutes(30),
        };

        private static string BaseUrl(int port) =>
            "http://127.0.0.1:" + port;

        public static async Task<PingResponse> PingAsync(
            int port, int timeoutMs = 2000)
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(
                    timeoutMs);
                var resp = await Http.GetAsync(
                    BaseUrl(port) + "/ping", cts.Token);
                var body = await resp.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<PingResponse>(body);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<T> PostAsync<T>(
            int port, string route, object payload,
            CancellationToken ct = default)
        {
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(
                json, Encoding.UTF8, "application/json");
            var resp = await Http.PostAsync(
                BaseUrl(port) + route, content, ct);
            var body = await resp.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(body);
        }

        public static Task<SetImageResponse> SetImageAsync(
            int port, SetImageRequest req,
            CancellationToken ct = default) =>
            PostAsync<SetImageResponse>(port, "/set_image", req, ct);

        public static Task<PredictResponse> PredictAsync(
            int port, PredictRequest req) =>
            PostAsync<PredictResponse>(port, "/predict", req);

        public static async Task ShutdownAsync(int port)
        {
            try
            {
                await PostAsync<PingResponse>(port, "/shutdown", new { });
            }
            catch
            {
                // Server may already be gone.
            }
        }
    }
}
