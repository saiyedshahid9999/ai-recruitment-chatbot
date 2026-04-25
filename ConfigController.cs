using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ChatBot.Controllers
{
    public class ConfigController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<ConfigController> _logger;
        private readonly string _appSettingsPath;

        public ConfigController(IConfiguration configuration, ILogger<ConfigController> logger)
        {
            _configuration = configuration;
            _logger = logger;
            _appSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
        }

        [HttpGet]
        public IActionResult ConfigurationManager()
        {
            try
            {
                // Read the current appsettings.json content
                var jsonContent = System.IO.File.ReadAllText(_appSettingsPath);
                ViewBag.JsonContent = jsonContent;
                return View();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading appsettings.json");
                TempData["Error"] = "Failed to load configuration. Please try again.";
                return View();
            }
        }

        [HttpPost]
        public async Task<IActionResult> SaveConfig(string jsonData)
        {
            try
            {
                // Validate JSON format
                if (string.IsNullOrWhiteSpace(jsonData))
                {
                    TempData["Error"] = "JSON data cannot be empty.";
                    ViewBag.JsonContent = jsonData;
                    return View("ConfigurationManager");
                }

                // Attempt to parse JSON to ensure it's valid
                try
                {
                    JToken.Parse(jsonData);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Invalid JSON format submitted");
                    TempData["Error"] = "Invalid JSON format. Please check the syntax and try again.";
                    ViewBag.JsonContent = jsonData;
                    return View("ConfigurationManager");
                }

                // Write the JSON data to appsettings.json
                await System.IO.File.WriteAllTextAsync(_appSettingsPath, jsonData);
                _logger.LogInformation("Successfully updated appsettings.json");

                // Reload configuration to reflect changes
                var builder = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                var updatedConfig = builder.Build();

                // Update the configuration instance (this is a simple approach; in production, consider service scope or restarting the app)
                var field = _configuration.GetType().GetField("_configuration", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null)
                {
                    field.SetValue(_configuration, updatedConfig);
                }

                TempData["Success"] = "Configuration updated successfully.";
                ViewBag.JsonContent = jsonData;
                return View("ConfigurationManager");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving appsettings.json");
                TempData["Error"] = "Failed to save configuration. Please try again.";
                ViewBag.JsonContent = jsonData;
                return View("ConfigurationManager");
            }
        }
    }
}