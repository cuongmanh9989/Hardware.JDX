using System;
using System.Threading;
using System.Threading.Tasks;
using Hardware.Info.Wpf;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hardware.Info.Service
{
    /// <summary>
    /// Chạy ngầm: ngay khi khởi động rồi mỗi IntervalMinutes phút, đọc lại phần cứng và gửi một bản tin lên Kafka
    /// (cùng định dạng với app WPF).
    /// Thiết kế để không ảnh hưởng máy và không tự chết:
    /// - nhường CPU: chờ CPU rảnh (tối đa MaxDeferMinutes) trước khi đọc, luồng đọc có ưu tiên thấp nhất;
    /// - mỗi chu kỳ có giới hạn thời gian; lần đọc treo (WMI/driver) bị bỏ lại và chu kỳ sau không chạy chồng lên;
    /// - lỗi chỉ ghi log, thử lại sớm hơn theo kiểu giãn dần (5 phút, 10 phút... tối đa bằng chu kỳ), không bao giờ dừng service.
    /// </summary>
    public sealed class DeviceInfoWorker : BackgroundService
    {
        /// <summary>Tối đa cho cả lần đọc phần cứng; quá hạn thì bỏ qua lượt này.</summary>
        private static readonly TimeSpan CollectTimeout = TimeSpan.FromMinutes(5);

        /// <summary>Khoảng thử lại đầu tiên sau lỗi; nhân đôi mỗi lần lỗi liên tiếp.</summary>
        private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMinutes(5);

        private readonly ILogger<DeviceInfoWorker> _logger;
        private readonly IHardwareInfo _hardwareInfo = new HardwareInfo();

        /// <summary>Lần đọc phần cứng đang chạy (kể cả lần đã quá hạn nhưng chưa kết thúc).</summary>
        private Task<SummaryResult>? _collecting;

        public DeviceInfoWorker(ILogger<DeviceInfoWorker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            int consecutiveFailures = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan interval = TimeSpan.FromMinutes(60);
                bool success = false;

                try
                {
                    // Nạp cấu hình mỗi chu kỳ để sửa appsettings.json không cần khởi động lại service
                    DeviceInfoPublisherOptions? options = DeviceInfoPublisherOptions.Load(out string error);
                    if (options == null)
                    {
                        _logger.LogError("Không gửi Kafka: {Error}", error);
                    }
                    else
                    {
                        interval = TimeSpan.FromMinutes(options.IntervalMinutes);
                        success = await RunCycleAsync(options, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi không lường trước ở chu kỳ thu thập/gửi");
                }

                consecutiveFailures = success ? 0 : consecutiveFailures + 1;
                TimeSpan delay = success ? interval : NextRetryDelay(consecutiveFailures, interval);
                if (!success)
                    _logger.LogWarning("Chu kỳ chưa thành công ({Count} lần liên tiếp), thử lại sau {Delay}", consecutiveFailures, delay);

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            try { await base.StopAsync(cancellationToken); }
            catch (Exception ex) { _logger.LogWarning("Lỗi khi dừng worker: {Error}", ex.GetBaseException().Message); }

            // Flush có giới hạn 3 giây, tổng chờ tối đa 5 giây
            DeviceInfoPublisher.Shutdown();
        }

        /// <summary>5 phút, 10 phút, 20 phút... nhưng không dài hơn chu kỳ bình thường.</summary>
        private static TimeSpan NextRetryDelay(int failures, TimeSpan interval)
        {
            double minutes = FirstRetryDelay.TotalMinutes * Math.Pow(2, Math.Min(failures - 1, 10));
            return TimeSpan.FromMinutes(Math.Min(minutes, interval.TotalMinutes));
        }

        /// <summary>Một chu kỳ: chờ CPU rảnh → đọc phần cứng (ưu tiên thấp, có giới hạn thời gian) → gửi Kafka. Trả true nếu gửi được.</summary>
        private async Task<bool> RunCycleAsync(DeviceInfoPublisherOptions options, CancellationToken stoppingToken)
        {
            // Lần đọc trước bị treo và chưa xong: không chạy chồng thêm (mỗi lần treo giữ một luồng)
            if (_collecting != null && !_collecting.IsCompleted)
            {
                _logger.LogWarning("Lần đọc phần cứng trước vẫn chưa kết thúc, bỏ qua lượt này");
                return false;
            }

            bool idle = await LowPriority.WaitForIdleAsync(options.MaxCpuPercent, TimeSpan.FromMinutes(options.MaxDeferMinutes), stoppingToken);
            if (!idle)
                _logger.LogInformation("CPU vẫn trên {Percent}% sau {Minutes} phút chờ, vẫn thực hiện để không bỏ lỡ lượt gửi", options.MaxCpuPercent, options.MaxDeferMinutes);

            _collecting = Task.Run(Collect, CancellationToken.None);

            SummaryResult summary;
            try
            {
                summary = await _collecting.WaitAsync(CollectTimeout, stoppingToken);
            }
            catch (TimeoutException)
            {
                _logger.LogError("Đọc phần cứng quá {Minutes} phút (WMI/driver có thể bị treo), bỏ qua lượt này", CollectTimeout.TotalMinutes);
                return false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Đọc phần cứng thất bại");
                return false;
            }

            foreach (string note in summary.Notes)
                _logger.LogWarning("{Note}", note);

            if (summary.Rows.Count == 0)
            {
                _logger.LogWarning("Không đọc được linh kiện nào, bỏ qua lượt gửi này");
                return false;
            }

            PublishResult result = await DeviceInfoPublisher.PublishAsync(options, summary.Rows);
            if (result.Success) _logger.LogInformation("{Result}", result.Message);
            else _logger.LogError("{Result}", result.Message);
            return result.Success;
        }

        /// <summary>Đọc phần cứng ở luồng thread pool với ưu tiên thấp nhất; lỗi từng nhóm không làm hỏng cả lần đọc.</summary>
        private SummaryResult Collect()
        {
            using IDisposable lowPriority = LowPriority.ScopeThread();

            foreach (HardwareTreeBuilder.Category category in HardwareTreeBuilder.CreateCategories(_hardwareInfo, includeLiveCounters: false))
            {
                try { category.Refresh(); }
                catch (Exception ex) { _logger.LogWarning("Không làm mới được nhóm {Group}: {Error}", category.Title, ex.GetBaseException().Message); }
            }
            return ComponentSummaryCollector.BuildRows(_hardwareInfo);
        }
    }
}
