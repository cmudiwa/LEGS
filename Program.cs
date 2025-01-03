using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Localization;
using System.Globalization;
using System.Text.Json.Serialization;
using ZimraEGS.ApiClient.Helpers;
using ZimraEGS.Helpers;

namespace ZimraEGS
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            var builder = WebApplication.CreateBuilder(args);

            string logDirectory = Environment.GetEnvironmentVariable("HOME") != null
            ? Path.Combine(Environment.GetEnvironmentVariable("HOME"), "LogFiles", "MyApp") // Azure
            : Path.Combine(AppContext.BaseDirectory, "Logs"); // Local

            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            // Konfigurasi logging
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.AddProvider(new FileLoggerProvider(logDirectory, LogLevel.Information));


            builder.Services.Configure<JsonOptions>(options =>
                options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault | JsonIgnoreCondition.WhenWritingNull);

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowAllOrigins",
                    builder => builder.AllowAnyOrigin()
                                      .AllowAnyMethod()
                                      .AllowAnyHeader());
            });


            // Add services to the container.
            builder.Services.AddControllersWithViews();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Home/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseRouting();

            app.UseCors("AllowAllOrigins");

            var defaultCulture = new CultureInfo("en-US");

            var localizationOptions = new RequestLocalizationOptions
            {
                DefaultRequestCulture = new RequestCulture(defaultCulture),
                SupportedCultures = new List<CultureInfo> { defaultCulture },
                SupportedUICultures = new List<CultureInfo> { defaultCulture }
            };

            app.UseRequestLocalization(localizationOptions);


            app.UseAuthorization();

            app.MapControllerRoute(
                name: "default",
                pattern: "{controller=Home}/{action=Index}/{id?}");

            app.Run();
        }
    }
}