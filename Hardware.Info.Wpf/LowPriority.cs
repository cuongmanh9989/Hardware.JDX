using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Hardware.Info.Wpf
{
    /// <summary>
    /// Giữ cho việc thu thập/gửi dữ liệu không tranh tài nguyên với tác vụ khác:
    /// hạ độ ưu tiên tiến trình và luồng, và chờ lúc CPU rảnh trước khi đọc phần cứng.
    /// Mọi hàm đều nuốt lỗi: không hạ được ưu tiên thì vẫn chạy bình thường.
    /// </summary>
    public static class LowPriority
    {
        /// <summary>Chế độ nền của Windows: hạ cả CPU, I/O đĩa và ưu tiên bộ nhớ của tiến trình.</summary>
        private const uint ProcessModeBackgroundBegin = 0x00100000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint Low;
            public uint High;
            public ulong Value => ((ulong)High << 32) | Low;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

        /// <summary>
        /// Hạ độ ưu tiên tiến trình hiện tại. <paramref name="background"/> = true (service): chế độ nền, nhường tối đa
        /// cả CPU lẫn I/O đĩa. false (app WPF có giao diện): chỉ BelowNormal để giao diện vẫn mượt.
        /// </summary>
        public static void ApplyToProcess(bool background)
        {
            try
            {
                if (background && SetPriorityClass(GetCurrentProcess(), ProcessModeBackgroundBegin))
                    return;

                using Process process = Process.GetCurrentProcess();
                process.PriorityClass = background ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal;
            }
            catch (Exception)
            {
                // Không đủ quyền hoặc hệ điều hành từ chối: chạy với ưu tiên mặc định
            }
        }

        /// <summary>
        /// Hạ độ ưu tiên luồng hiện tại xuống thấp nhất, trả về đối tượng để khôi phục khi xong
        /// (luồng của thread pool được dùng lại nên bắt buộc khôi phục).
        /// </summary>
        public static IDisposable ScopeThread() => new ThreadScope();

        private sealed class ThreadScope : IDisposable
        {
            private readonly ThreadPriority _previous;
            private readonly bool _changed;

            public ThreadScope()
            {
                try
                {
                    _previous = Thread.CurrentThread.Priority;
                    Thread.CurrentThread.Priority = ThreadPriority.Lowest;
                    _changed = true;
                }
                catch (Exception)
                {
                    // Bỏ qua: giữ ưu tiên hiện tại
                }
            }

            public void Dispose()
            {
                if (!_changed) return;
                try { Thread.CurrentThread.Priority = _previous; }
                catch (Exception) { /* bỏ qua */ }
            }
        }

        /// <summary>
        /// Chờ tới khi CPU toàn hệ thống rảnh (dưới <paramref name="maxCpuPercent"/>%), kiểm tra mỗi 30 giây.
        /// Quá <paramref name="maxWait"/> thì thôi chờ và trả false để vẫn chạy (không bỏ lỡ lượt gửi).
        /// </summary>
        public static async Task<bool> WaitForIdleAsync(int maxCpuPercent, TimeSpan maxWait, CancellationToken cancellationToken)
        {
            if (maxCpuPercent >= 100 || maxWait <= TimeSpan.Zero) return true;

            DateTime deadline = DateTime.UtcNow + maxWait;
            while (true)
            {
                double usage = await SampleCpuPercentAsync(cancellationToken);
                if (usage < 0 || usage <= maxCpuPercent) return true;      // không đo được thì coi như rảnh
                if (DateTime.UtcNow >= deadline) return false;

                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
        }

        /// <summary>Đo CPU toàn hệ thống trong 1 giây (0-100); -1 nếu không đo được.</summary>
        private static async Task<double> SampleCpuPercentAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!GetSystemTimes(out FileTime idle1, out FileTime kernel1, out FileTime user1)) return -1;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                if (!GetSystemTimes(out FileTime idle2, out FileTime kernel2, out FileTime user2)) return -1;

                // kernel đã gồm cả thời gian idle
                ulong total = (kernel2.Value - kernel1.Value) + (user2.Value - user1.Value);
                ulong idle = idle2.Value - idle1.Value;
                return total == 0 ? -1 : (1d - (double)idle / total) * 100d;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}
