using System;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SomaticLauncher.Services;
using SomaticLauncher.ViewModels;

namespace SomaticLauncher
{
    public partial class App : Application
    {
        public new static App Current => (App)Application.Current;
        public IServiceProvider Services { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);
            Services = serviceCollection.BuildServiceProvider();

            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            base.OnStartup(e);
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddHttpClient();
            services.AddSingleton<ConfigService>();
            services.AddSingleton<StatusService>();
            services.AddSingleton<GameService>();
            services.AddSingleton<MainViewModel>();
            services.AddTransient<MainWindow>();
        }
    }
}
