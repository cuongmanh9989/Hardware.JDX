using System;
using System.Threading.Tasks;
using Hardware.Info.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hardware.Info.Service
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            // Nhường tối đa CPU và I/O đĩa cho các tác vụ khác của máy
            LowPriority.ApplyToProcess(background: true);

            try
            {
                HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

                // Chạy được cả như Windows Service lẫn console (để thử: Hardware.Info.Service.exe)
                builder.Services.AddWindowsService(options => options.ServiceName = "Hardware.JDX.Service");
                builder.Services.AddHostedService<DeviceInfoWorker>();

                // Worker tự bắt mọi lỗi trong vòng lặp; đây là lớp bảo vệ cuối: lỗi lọt ra cũng không làm dừng cả host
                builder.Services.Configure<HostOptions>(options =>
                {
                    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
                    options.ShutdownTimeout = TimeSpan.FromSeconds(15);
                });

                IHost host = builder.Build();
                ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Hardware.Info.Service");
                RegisterGlobalHandlers(logger);

                host.Run();
                return 0;
            }
            catch (Exception ex)
            {
                // Lỗi khởi động (hiếm): ghi ra stderr/Event Log của Windows qua mã thoát khác 0 để SCM tự khởi động lại
                Console.Error.WriteLine("Hardware.JDX.Service dừng do lỗi: " + ex);
                return 1;
            }
        }

        private static void RegisterGlobalHandlers(ILogger logger)
        {
            // Task bị bỏ quên lỗi (vd lần đọc quá hạn rồi mới ném lỗi) không được làm sập tiến trình
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                logger.LogWarning("Lỗi task không được quan sát: {Error}", e.Exception.GetBaseException().Message);
                e.SetObserved();
            };

            // Lỗi không bắt được ở luồng khác: không ngăn được việc tiến trình dừng, nhưng ghi lại lý do
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                logger.LogCritical("Lỗi nghiêm trọng không bắt được: {Error}", e.ExceptionObject);
        }
    }
}
