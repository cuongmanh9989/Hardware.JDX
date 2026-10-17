using System;
using System.Threading.Tasks;
using System.Windows;

namespace Hardware.Info.Wpf
{
    /// <summary>
    /// Điểm vào của ứng dụng WPF.
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Nhường CPU cho các tác vụ khác; giữ BelowNormal (không phải Idle) để giao diện vẫn mượt
            LowPriority.ApplyToProcess(background: false);

            // Lỗi chưa bắt ở luồng giao diện: hiện ở thanh trạng thái thay vì làm app đóng đột ngột
            DispatcherUnhandledException += (_, args) =>
            {
                ReportError(args.Exception);
                args.Handled = true;
            };

            // Task bị bỏ quên lỗi không được làm sập tiến trình
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                ReportError(args.Exception);
                args.SetObserved();
            };

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Giải phóng producer Kafka có giới hạn thời gian (tối đa 5 giây), không treo lúc thoát
            DeviceInfoPublisher.Shutdown();
            base.OnExit(e);
        }

        private static void ReportError(Exception ex)
        {
            try
            {
                string message = "Lỗi không mong muốn (đã bỏ qua): " + ex.GetBaseException().Message;
                Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (Current?.MainWindow is MainWindow window)
                        window.ShowStatus(message);
                }));
            }
            catch (Exception)
            {
                // Không báo được lỗi thì thôi, tuyệt đối không ném lỗi tiếp từ chính trình xử lý lỗi
            }
        }
    }
}
