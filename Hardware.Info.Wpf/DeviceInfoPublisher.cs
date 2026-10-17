using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;

namespace Hardware.Info.Wpf
{
    /// <summary>Cấu hình gửi Kafka, đọc từ mục Kafka:DeviceInfo trong appsettings.json cạnh file chạy.</summary>
    public sealed class DeviceInfoPublisherOptions
    {
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
                if (options == null || options.BootstrapServers.Length == 0 || options.Topic.Length == 0)
                {
                    error = "Kafka:DeviceInfo thiếu BootstrapServers hoặc Topic";
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

    /// <summary>Gửi thông tin thiết bị (deviceinfo.txt) cùng danh sách linh kiện lên Kafka.</summary>
    public static class DeviceInfoPublisher
    {
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
        {
            try
            {
                (JsonNode? deviceInfo, string messageKey, string fileNote) = ReadDeviceInfo(options.DeviceInfoPath);

                string payload = JsonSerializer.Serialize(new
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
