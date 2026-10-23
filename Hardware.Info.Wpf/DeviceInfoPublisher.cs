using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;

namespace Hardware.Info.Wpf
{
    /// <summary>
    /// Cấu hình gửi báo cáo, đọc từ mục Kafka:DeviceInfo trong appsettings.json cạnh file chạy.
    /// <c>Transport</c> = <c>Http</c> (MẶC ĐỊNH từ bản hotfix 22/10/2026: gửi HTTPS ký HMAC tới TaiSanApi, dùng được ngoài VPN) hoặc
    /// <c>Kafka</c> (gửi thẳng broker, chỉ trong VPN). Địa chỉ và khóa chung có giá trị NHÚNG SẴN trong code; mục DeviceReport:Url /
    /// DeviceReport:SharedKey trong appsettings.json (nếu có và không rỗng) sẽ ghi đè giá trị nhúng sẵn.
    /// </summary>
    public sealed class DeviceInfoPublisherOptions
    {
        public const string TransportKafka = "Kafka";
        public const string TransportHttp = "Http";

        /// <summary>Địa chỉ nhận báo cáo của TaiSanApi nhúng sẵn (hotfix 22/10/2026); appsettings.json DeviceReport:Url ghi đè nếu có.</summary>
        public const string DefaultDeviceReportUrl = "https://taisanapi.vtax.id.vn/api/taisan/v1/device-reports";

        /// <summary>
        /// Khóa chung HMAC nhúng sẵn theo quyết định chủ dự án (hotfix 22/10/2026): khóa nằm trong source và file chạy nên ai có file đều trích
        /// được, mọi máy dùng chung một khóa. Phải KHỚP DeviceReport:SharedKeys (Docker secret device_report_shared_key) của TaiSanApi.
        /// Muốn đổi khóa: sửa hằng số này (hoặc đặt DeviceReport:SharedKey trong appsettings.json) và đổi secret phía máy chủ cho khớp.
        /// </summary>
        public const string DefaultDeviceReportSharedKey = "a3403b6806b952f302d3133653633960dea13ff93f2b3e00";

        /// <summary>Kafka (mặc định, giữ hành vi cũ) hoặc Http.</summary>
        public string Transport { get; set; } = TransportHttp;

        /// <summary>https luôn được phép; http chỉ cho loopback/IP mạng nội bộ (máy chủ dev TaiSanApi chỉ nghe http), tránh gửi chữ ký qua Internet không mã hóa.</summary>
        private static bool IsAllowedReportUri(Uri uri)
        {
            if (uri.Scheme == Uri.UriSchemeHttps) return true;
            if (uri.Scheme != Uri.UriSchemeHttp) return false;
            if (uri.IsLoopback) return true;
            if (!System.Net.IPAddress.TryParse(uri.Host, out System.Net.IPAddress? ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            byte[] b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }

        /// <summary>Địa chỉ endpoint nhận báo cáo (https), từ DeviceReport:Url. Chỉ dùng khi Transport=Http.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string DeviceReportUrl { get; set; } = string.Empty;

        /// <summary>Khóa HMAC dùng chung, từ DeviceReport:SharedKey (tối thiểu 32 ký tự). Chỉ dùng khi Transport=Http.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string DeviceReportSharedKey { get; set; } = string.Empty;

        public bool UseHttp => Transport.Equals(TransportHttp, StringComparison.OrdinalIgnoreCase);

        public string BootstrapServers { get; set; } = string.Empty;
        public string Topic { get; set; } = string.Empty;
        public string Acks { get; set; } = "all";
        public bool EnableIdempotence { get; set; } = true;
        public string CompressionType { get; set; } = "lz4";
        public int MaxInFlight { get; set; } = 5;
        public int RetryAttempts { get; set; } = 3;
        public int RetryBackoffMs { get; set; } = 1000;
        public int IntervalMinutes { get; set; } = 60;
        public string DeviceInfoPath { get; set; } = @"C:\deviceinfo\deviceinfo.txt";

        /// <summary>CPU toàn hệ thống vượt mức này (%) thì hoãn việc đọc phần cứng để nhường cho tác vụ khác (100 = không hoãn).</summary>
        public int MaxCpuPercent { get; set; } = 85;

        /// <summary>Hoãn tối đa bao nhiêu phút; hết hạn mà CPU vẫn bận thì vẫn chạy để không bỏ lỡ lượt gửi.</summary>
        public int MaxDeferMinutes { get; set; } = 10;

        /// <summary>Khóa chung tối thiểu (ký tự), khớp yêu cầu phía TaiSanApi.</summary>
        public const int MinSharedKeyLength = 32;

        /// <summary>Đọc cấu hình; trả null kèm lý do nếu file thiếu hoặc không hợp lệ.</summary>
        public static DeviceInfoPublisherOptions? Load(out string error)
        {
            error = string.Empty;
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("Kafka", out JsonElement kafka) || !kafka.TryGetProperty("DeviceInfo", out JsonElement section))
                {
                    error = "appsettings.json thiếu mục Kafka:DeviceInfo";
                    return null;
                }

                var options = section.Deserialize<DeviceInfoPublisherOptions>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (options == null)
                {
                    error = "Kafka:DeviceInfo không hợp lệ";
                    return null;
                }
                if (options.UseHttp)
                {
                    // Kênh HTTPS: không cần broker; cần địa chỉ https và khóa chung
                    string configuredUrl = string.Empty, configuredKey = string.Empty;
                    if (doc.RootElement.TryGetProperty("DeviceReport", out JsonElement report) && report.ValueKind == JsonValueKind.Object)
                    {
                        configuredUrl = (report.TryGetProperty("Url", out JsonElement url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null)?.Trim() ?? string.Empty;
                        configuredKey = (report.TryGetProperty("SharedKey", out JsonElement key) && key.ValueKind == JsonValueKind.String ? key.GetString() : null)?.Trim() ?? string.Empty;
                    }
                    options.DeviceReportUrl = configuredUrl.Length > 0 ? configuredUrl : DefaultDeviceReportUrl;
                    options.DeviceReportSharedKey = configuredKey.Length > 0 ? configuredKey : DefaultDeviceReportSharedKey;
                    if (!Uri.TryCreate(options.DeviceReportUrl, UriKind.Absolute, out Uri? uri) || !IsAllowedReportUri(uri))
                    {
                        error = "DeviceReport:Url phải là địa chỉ https hợp lệ (http chỉ được dùng cho địa chỉ nội bộ/loopback khi thử nghiệm) khi Transport=Http";
                        return null;
                    }
                    if (options.DeviceReportSharedKey.Length < MinSharedKeyLength)
                    {
                        error = $"DeviceReport:SharedKey phải có tối thiểu {MinSharedKeyLength} ký tự";
                        return null;
                    }
                }
                else if (options.Transport.Equals(TransportKafka, StringComparison.OrdinalIgnoreCase))
                {
                    if (options.BootstrapServers.Length == 0 || options.Topic.Length == 0)
                    {
                        error = "Kafka:DeviceInfo thiếu BootstrapServers hoặc Topic";
                        return null;
                    }
                }
                else
                {
                    error = "Kafka:DeviceInfo:Transport chỉ nhận Kafka hoặc Http";
                    return null;
                }
                // Giá trị vô lý trong file cấu hình không được làm service chạy sai (vd chu kỳ 0 phút = gửi dồn dập)
                if (options.IntervalMinutes < 1) options.IntervalMinutes = 60;
                if (options.MaxCpuPercent < 10 || options.MaxCpuPercent > 100) options.MaxCpuPercent = 85;
                if (options.MaxDeferMinutes < 0) options.MaxDeferMinutes = 0;
                if (options.MaxInFlight < 1) options.MaxInFlight = 5;
                if (options.RetryAttempts < 0) options.RetryAttempts = 3;
                if (options.RetryBackoffMs < 100) options.RetryBackoffMs = 1000;
                return options;
            }
            catch (Exception ex)
            {
                error = "Không đọc được appsettings.json: " + ex.GetBaseException().Message;
                return null;
            }
        }
    }

    /// <summary>Kết quả một lần gửi: thành công hay không, kèm nội dung để hiện ở trạng thái/log.</summary>
    public sealed record PublishResult(bool Success, string Message);

    /// <summary>Gửi thông tin thiết bị (deviceinfo.txt) cùng danh sách linh kiện: thẳng Kafka hoặc qua HTTPS ký HMAC tới TaiSanApi.</summary>
    public static class DeviceInfoPublisher
    {
        /// <summary>HttpClient dùng chung (không tạo mới mỗi giờ): timeout 30 giây, không theo redirect (không để chữ ký bị chuyển sang nơi khác).</summary>
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private const string TimestampHeader = "X-Device-Timestamp";
        private const string SignatureHeader = "X-Device-Signature";
        private const string ServerTimeHeader = "X-Server-Time";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            // Giữ nguyên tiếng Việt thay vì \uXXXX
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>Bản tin lớn hơn mức này (byte) thì không gửi, để không vượt giới hạn message của broker (thường 1 MB).</summary>
        private const int MaxPayloadBytes = 900_000;

        /// <summary>Thời gian tối đa chờ broker xác nhận một bản tin.</summary>
        private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(35);

        private static readonly object ProducerGate = new object();

        /// <summary>
        /// Producer dùng chung cho cả vòng đời tiến trình: không tạo/hủy mỗi giờ (tránh dựng lại luồng và kết nối).
        /// Chỉ dùng trong khối lock(ProducerGate).
        /// </summary>
        private static IProducer<string, string>? _producer;
        private static string _producerSignature = string.Empty;
        private static volatile bool _producerBroken;

        /// <summary>Lỗi gần nhất do librdkafka báo (mất kết nối broker...), kèm vào thông báo khi gửi thất bại.</summary>
        private static volatile string _lastBrokerError = string.Empty;

        /// <summary>
        /// Giải phóng producer khi thoát chương trình. Flush có giới hạn thời gian (3 giây), không bao giờ chờ vô hạn,
        /// và chạy ở luồng riêng nên không treo luồng gọi (giao diện/StopAsync) quá 5 giây.
        /// </summary>
        public static void Shutdown()
        {
            IProducer<string, string>? producer;
            lock (ProducerGate)
            {
                producer = _producer;
                _producer = null;
                _producerSignature = string.Empty;
            }
            DisposeProducerBounded(producer);
        }

        private static void DisposeProducerBounded(IProducer<string, string>? producer)
        {
            if (producer == null) return;
            try
            {
                Task.Run(() =>
                {
                    try { producer.Flush(TimeSpan.FromSeconds(3)); }
                    catch (Exception) { /* không flush được: bỏ qua */ }
                    try { producer.Dispose(); }
                    catch (Exception) { /* bỏ qua */ }
                }).Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Không chờ được: để tiến trình tự dọn khi thoát
            }
        }

        /// <summary>Lấy producer dùng chung; tạo mới khi chưa có, khi cấu hình đổi, hoặc khi producer bị lỗi nghiêm trọng.</summary>
        private static IProducer<string, string> GetProducer(DeviceInfoPublisherOptions options)
        {
            // MaxInFlight > 5 không hợp lệ khi bật idempotence; idempotence bắt buộc acks=all
            bool idempotent = options.EnableIdempotence;
            bool acksAll = idempotent || options.Acks.Equals("all", StringComparison.OrdinalIgnoreCase);
            int maxInFlight = Math.Max(1, idempotent ? Math.Min(options.MaxInFlight, 5) : options.MaxInFlight);

            string signature = string.Join("|", options.BootstrapServers, acksAll, idempotent, options.CompressionType, maxInFlight, options.RetryAttempts, options.RetryBackoffMs);

            IProducer<string, string>? stale = null;
            lock (ProducerGate)
            {
                if (_producer != null && _producerSignature == signature && !_producerBroken)
                    return _producer;

                stale = _producer;
                _producer = null;
            }
            DisposeProducerBounded(stale);

            var config = new ProducerConfig
            {
                BootstrapServers = options.BootstrapServers,
                Acks = acksAll ? Confluent.Kafka.Acks.All : Confluent.Kafka.Acks.Leader,
                EnableIdempotence = idempotent,
                CompressionType = options.CompressionType.Equals("lz4", StringComparison.OrdinalIgnoreCase) ? Confluent.Kafka.CompressionType.Lz4 : Confluent.Kafka.CompressionType.None,
                MaxInFlight = maxInFlight,
                MessageSendMaxRetries = options.RetryAttempts,
                RetryBackoffMs = options.RetryBackoffMs,
                MessageTimeoutMs = 30000,

                // Gửi ngay từng bản tin, không gom lô chờ (mỗi giờ chỉ có 1 bản tin)
                LingerMs = 0,
                MessageMaxBytes = 1_000_000,

                // Chặn bộ nhớ hàng đợi: broker chết lâu cũng không phình RAM
                QueueBufferingMaxMessages = 10,
                QueueBufferingMaxKbytes = 4096,

                // Broker không tới được: thử nối lại thưa dần (1s → 30s), không dồn dập gây tốn CPU/mạng
                SocketTimeoutMs = 10000,
                ReconnectBackoffMs = 1000,
                ReconnectBackoffMaxMs = 30000,
                SocketKeepaliveEnable = true
            };

            IProducer<string, string> producer = new ProducerBuilder<string, string>(config)
                .SetErrorHandler((_, error) =>
                {
                    _lastBrokerError = error.Reason;
                    // Lỗi nghiêm trọng (idempotence...) thì producer không dùng tiếp được: đánh dấu để tạo lại ở lần gửi sau
                    if (error.IsFatal) _producerBroken = true;
                })
                .SetLogHandler((_, _) => { })       // librdkafka không ghi log ra ngoài
                .Build();

            lock (ProducerGate)
            {
                _producer = producer;
                _producerSignature = signature;
                _producerBroken = false;
            }
            return producer;
        }

        /// <summary>
        /// Gửi một bản tin; không bao giờ ném lỗi. Có giới hạn thời gian chờ (35 giây) và không gọi Flush.
        /// Bản tin: thời điểm gửi, nội dung deviceinfo.txt (đọc lại mỗi lần) và các dòng linh kiện. Key = device_id.
        /// </summary>
        public static async Task<PublishResult> PublishAsync(DeviceInfoPublisherOptions options, IReadOnlyList<SummaryRow> rows)
            => options.UseHttp ? await PublishHttpAsync(options, rows) : await PublishKafkaAsync(options, rows);

        /// <summary>
        /// Chữ ký HMAC-SHA256 của báo cáo: hex thường của HMAC(key, timestamp + "." + body), key và body theo UTF-8.
        /// Phải khớp đúng thuật toán của TaiSanApi (DeviceReportSignature).
        /// </summary>
        internal static string ComputeSignature(string key, string timestamp, byte[] body)
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(key));
            hmac.AppendData(Encoding.UTF8.GetBytes(timestamp + "."));
            hmac.AppendData(body);
            return Convert.ToHexString(hmac.GetHashAndReset()).ToLowerInvariant();
        }

        /// <summary>
        /// Gửi một bản tin qua HTTPS tới TaiSanApi (không ném lỗi). Mã trả về: 200 thành công (kể cả chờ duyệt hoặc bị từ chối,
        /// không cần thử lại); 401/400/413 là lỗi cấu hình (ghi rõ); 429/503/lỗi mạng sẽ thử lại theo lịch giãn dần của người gọi.
        /// </summary>
        private static async Task<PublishResult> PublishHttpAsync(DeviceInfoPublisherOptions options, IReadOnlyList<SummaryRow> rows)
        {
            try
            {
                (JsonNode? deviceInfo, _, string fileNote) = ReadDeviceInfo(options.DeviceInfoPath);
                string payload = BuildPayload(deviceInfo, rows);
                byte[] body = Encoding.UTF8.GetBytes(payload);
                // TaiSanApi giới hạn body 512 KB
                if (body.Length > 500_000)
                    return new PublishResult(false, $"Gửi HTTPS lỗi: bản tin quá lớn ({body.Length / 1024} KB, tối đa 500 KB)");

                string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                using var request = new HttpRequestMessage(HttpMethod.Post, options.DeviceReportUrl);
                request.Headers.Add(TimestampHeader, timestamp);
                request.Headers.Add(SignatureHeader, ComputeSignature(options.DeviceReportSharedKey, timestamp, body));
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

                using HttpResponseMessage response = await Http.SendAsync(request);
                string text = await response.Content.ReadAsStringAsync();
                return MapHttpResponse(response, text, rows.Count, fileNote);
            }
            catch (TaskCanceledException)
            {
                return new PublishResult(false, "Gửi HTTPS lỗi: hết thời gian chờ máy chủ (sẽ thử lại ở lần sau)");
            }
            catch (HttpRequestException ex)
            {
                return new PublishResult(false, "Gửi HTTPS lỗi: không kết nối được máy chủ — " + ex.GetBaseException().Message + " (sẽ thử lại ở lần sau)");
            }
            catch (Exception ex)
            {
                return new PublishResult(false, "Gửi HTTPS lỗi: " + ex.GetBaseException().Message + " (sẽ thử lại ở lần sau)");
            }
        }

        /// <summary>Ánh xạ phản hồi HTTP của TaiSanApi sang kết quả hiển thị/log.</summary>
        internal static PublishResult MapHttpResponse(HttpResponseMessage response, string body, int componentCount, string fileNote)
        {
            int status = (int)response.StatusCode;
            if (status == 200)
            {
                string state = ReadStatus(body);
                string suffix = $"{DateTime.Now:HH:mm:ss}, {componentCount} linh kiện)" + fileNote;
                return state switch
                {
                    "FORWARDED" => new PublishResult(true, "Đã gửi máy chủ, đã chuyển tiếp (" + suffix),
                    "PENDING_APPROVAL" => new PublishResult(true, "Đã gửi máy chủ, đang CHỜ DUYỆT thiết bị (" + suffix),
                    "REJECTED" => new PublishResult(true, "Cảnh báo: máy chủ ĐÃ TỪ CHỐI nhận dữ liệu của thiết bị này (" + suffix),
                    _ => new PublishResult(true, "Đã gửi máy chủ (" + suffix)
                };
            }

            string code = ReadErrorCode(body);
            switch (status)
            {
                case 401:
                    if (response.Headers.TryGetValues(ServerTimeHeader, out IEnumerable<string>? values) && long.TryParse(values.FirstOrDefault(), out long serverMs))
                    {
                        double skewMinutes = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - serverMs) / 60000.0;
                        return new PublishResult(false, $"Gửi HTTPS lỗi 401: đồng hồ máy lệch {skewMinutes:0.#} phút so với máy chủ (cho phép ±5 phút). Hãy bật đồng bộ giờ (NTP) trên máy này");
                    }
                    return new PublishResult(false, "Gửi HTTPS lỗi 401: sai khóa chung hoặc chữ ký (kiểm tra DeviceReport:SharedKey) — lỗi cấu hình");
                case 400:
                    return new PublishResult(false, "Gửi HTTPS lỗi 400" + (code.Length > 0 ? " (" + code + ")" : string.Empty)
                        + (code == "TAISAN_DEVICE_REPORT_SITE_UNMAPPED" ? ": mã phòng (ma_phong) chưa được khai báo ở máy chủ" : ": nội dung báo cáo không hợp lệ") + " — lỗi cấu hình");
                case 413:
                    return new PublishResult(false, "Gửi HTTPS lỗi 413: bản tin quá lớn so với giới hạn của máy chủ");
                case 429:
                    return new PublishResult(false, "Gửi HTTPS: máy chủ giới hạn tốc độ hoặc phòng đang quá nhiều thiết bị chờ duyệt (429), sẽ thử lại ở lần sau");
                case 503:
                    return new PublishResult(false, "Gửi HTTPS: máy chủ tạm thời chưa nhận được" + (code.Length > 0 ? " (" + code + ")" : string.Empty) + ", sẽ thử lại ở lần sau");
                default:
                    return new PublishResult(false, $"Gửi HTTPS lỗi {status}" + (code.Length > 0 ? " (" + code + ")" : string.Empty) + ", sẽ thử lại ở lần sau");
            }
        }

        private static string ReadStatus(string body)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                return doc.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("status", out JsonElement st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? string.Empty : string.Empty;
            }
            catch (Exception) { return string.Empty; }
        }

        private static string ReadErrorCode(string body)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                return doc.RootElement.TryGetProperty("errorCode", out JsonElement ec) && ec.ValueKind == JsonValueKind.String ? ec.GetString() ?? string.Empty : string.Empty;
            }
            catch (Exception) { return string.Empty; }
        }

        /// <summary>Dựng JSON bản tin (dùng chung cho Kafka và HTTPS): thời điểm gửi, tên máy, deviceinfo.txt và các dòng linh kiện.</summary>
        private static string BuildPayload(JsonNode? deviceInfo, IReadOnlyList<SummaryRow> rows)
            => JsonSerializer.Serialize(new
            {
                sent_at = DateTime.UtcNow.ToString("o"),
                machine_name = Environment.MachineName,
                device_info = deviceInfo,
                components = rows.Select(r => new
                {
                    type = r.Type,
                    vendor = r.Vendor,
                    product = r.Product,
                    year = r.Year,
                    spec = r.Spec,
                    serial = r.Serial,
                    identifier = r.Identifier,
                    config = r.Config
                })
            }, JsonOptions);

        private static async Task<PublishResult> PublishKafkaAsync(DeviceInfoPublisherOptions options, IReadOnlyList<SummaryRow> rows)
        {
            try
            {
                (JsonNode? deviceInfo, string messageKey, string fileNote) = ReadDeviceInfo(options.DeviceInfoPath);

                string payload = BuildPayload(deviceInfo, rows);

                int payloadBytes = System.Text.Encoding.UTF8.GetByteCount(payload);
                if (payloadBytes > MaxPayloadBytes)
                    return new PublishResult(false, $"Gửi Kafka lỗi: bản tin quá lớn ({payloadBytes / 1024} KB, tối đa {MaxPayloadBytes / 1024} KB)");

                IProducer<string, string> producer = GetProducer(options);
                using var cts = new CancellationTokenSource(SendTimeout);

                DeliveryResult<string, string> result;
                try
                {
                    result = await producer.ProduceAsync(
                        options.Topic, new Message<string, string> { Key = messageKey, Value = payload }, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return new PublishResult(false, FailureText("hết thời gian chờ broker xác nhận"));
                }
                catch (ProduceException<string, string> ex)
                {
                    return new PublishResult(false, FailureText(ex.Error.Reason));
                }

                _lastBrokerError = string.Empty;
                return new PublishResult(true,
                    $"Đã gửi Kafka {DateTime.Now:HH:mm:ss} (partition {result.Partition.Value}, offset {result.Offset.Value}, {rows.Count} linh kiện)" + fileNote);
            }
            catch (Exception ex)
            {
                return new PublishResult(false, FailureText(ex.GetBaseException().Message));
            }
        }

        private static string FailureText(string reason)
        {
            string broker = _lastBrokerError;
            return "Gửi Kafka lỗi: " + reason + (broker.Length > 0 && !reason.Contains(broker) ? " [" + broker + "]" : string.Empty) + " (sẽ thử lại ở lần sau)";
        }

        /// <summary>Lấy giá trị chuỗi root[group][name]; thiếu hoặc không phải chuỗi thì trả rỗng.</summary>
        private static string Field(JsonObject root, string group, string name) =>
            (root[group] as JsonObject)?[name] is JsonValue value && value.TryGetValue(out string? text) ? text.Trim() : string.Empty;

        /// <summary>Đọc deviceinfo.txt; thiếu hoặc sai định dạng thì vẫn gửi phần linh kiện, key dùng tên máy Windows.</summary>
        private static (JsonNode? Info, string MessageKey, string Note) ReadDeviceInfo(string path)
        {
            try
            {
                if (!(JsonNode.Parse(File.ReadAllText(path)) is JsonObject root))
                    return (null, Environment.MachineName, " — deviceinfo.txt không phải đối tượng JSON, dùng tên máy làm key");

                string maPhong = Field(root, "chi_nhanh", "ma_phong");
                string maZone = Field(root, "khu_vuc", "ma_zone");
                string maMay = Field(root, "thiet_bi", "ma_may");

                // device_id = ma_phong/ma_zone/ma_may; thiếu mã nào thì không tạo, tránh ra ID cụt như "//MAY-21"
                if (maPhong.Length > 0 && maZone.Length > 0 && maMay.Length > 0)
                {
                    string deviceId = $"{maPhong}/{maZone}/{maMay}";
                    root["device_id"] = deviceId;
                    return (root, deviceId, string.Empty);
                }

                return (root, Environment.MachineName, " — thiếu ma_phong/ma_zone/ma_may nên không tạo device_id, dùng tên máy làm key");
            }
            catch (Exception ex)
            {
                return (null, Environment.MachineName, " — không đọc được deviceinfo.txt (" + ex.GetBaseException().Message + "), dùng tên máy làm key");
            }
        }
    }
}
