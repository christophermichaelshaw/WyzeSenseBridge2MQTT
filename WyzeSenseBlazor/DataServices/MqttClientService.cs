using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Connecting;
using MQTTnet.Client.Disconnecting;
using MQTTnet.Client.Options;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using WyzeSenseBlazor.Settings;
using WyzeSenseBlazor.DataStorage;
using WyzeSenseBlazor.DataStorage.Models;
using System.Collections.Generic;
using System.Linq;
using System;
using WyzeSenseCore;
using Microsoft.Extensions.Logging;
using WyzeSenseBlazor.Component;
using MQTTnet.Extensions.ManagedClient;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using System.Text;
using MQTTnet.Client.Receiving;

namespace WyzeSenseBlazor.DataServices
{
    public class MqttClientService : IMqttClientService
    {
        private readonly IWyzeSenseService _wyzeSenseService;
        private readonly IDataStoreService _dataStore;
        private readonly IMqttClient _mqttClient;
        private readonly IMqttClientOptions _options;
        private readonly ILogger<MqttClientService> _logger;
        private readonly AppSettingsProvider _appSettingsProvider;

        public MqttClientService(IMqttClientOptions options, IWyzeSenseService wyzeSenseService, IDataStoreService dataStore, AppSettingsProvider appSettingsProvider)
        {
            _options = options;
            _dataStore = dataStore;
            _wyzeSenseService = wyzeSenseService;
            _wyzeSenseService.OnEvent += WyzeSenseService_OnEventAsync;
            _mqttClient = new MqttFactory().CreateMqttClient();
            _logger = new LoggerFactory().CreateLogger<MqttClientService>();
            _appSettingsProvider = appSettingsProvider;
            ConfigureMqttClient();
        }

        private async void WyzeSenseService_OnEventAsync(object sender, WyzeSenseEvent e)
        {
            _logger.LogInformation($"[Dongle][{e.EventType}] {e}");
            if (e.Data.ContainsKey("State"))
            {
                var state = e.Data["State"].ToString();
                var payload = new PayloadPackage
                {
                    state = e.Data["State"].ToString(),
                    code_format = e.Data["CodeFormat"].ToString(),
                    changed_by = e.Data["ChangedBy"].ToString(),
                    code_arm_required = e.Data["CodeArmRequired"].ToString()
                };

                if (e.Data.ContainsKey("ModeName"))
                {
                    string modeName = e.Data["ModeName"].ToString();
                    string commandTopic = ConvertModeNameToState(modeName);
                    payload.command_topic = commandTopic;
                }
                else
                {
                    _logger.LogWarning("ModeName key not present in event data");
                }

                await PublishMessageAsync($"{AppSettingsProvider.ClientSettings.Topic}/{e.Sensor.MAC}", JsonConvert.SerializeObject(payload));
            }
        }

        private string ConvertModeNameToState(string modeName)
        {
            return modeName switch
            {
                "Disarmed" => "DISARM",
                "Home" => "ARM_HOME",
                "Away" => "ARM_AWAY",
                "Night" => "ARM_NIGHT",
                "Vacation" => "ARM_VACATION",
                "Bypass" => "ARM_CUSTOM_BYPASS",
                _ => modeName,
            };
        }
        private async Task PublishMessageAsync(string topic, string payload)
        {
            if (!_mqttClient.IsConnected)
            {
                _logger.LogError("MQTT client is not connected");
                System.Console.WriteLine("MQTT client is not connected");
                return;
            }
            try
            {
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithExactlyOnceQoS()
                    .Build();

                await _mqttClient.PublishAsync(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing MQTT message");
                System.Console.WriteLine("Error publishing MQTT message");
            }
        }


        private void ConfigureMqttClient()
        {
            _mqttClient.UseConnectedHandler(async e =>
            {
                _logger.LogInformation("Connected successfully with MQTT broker.");
                System.Console.WriteLine("Connected successfully with MQTT broker.");
                // Perform your necessary action on connected
                await Task.CompletedTask;
            })
            .UseDisconnectedHandler(async e =>
            {
                _logger.LogWarning("Disconnected from MQTT broker.");
                System.Console.WriteLine("Disconnected from MQTT broker.");
                // Perform your necessary action on disconnected
                await Task.Delay(TimeSpan.FromSeconds(5));
                try
                {
                    await _mqttClient.ConnectAsync(_options, CancellationToken.None); // Since 3.0.5 with CancellationToken
                }
                catch
                {
                    _logger.LogWarning("Reconnected to MQTT broker.");
                    System.Console.WriteLine("Reconnected to MQTT broker.");
                }
            });

            _mqttClient.UseApplicationMessageReceivedHandler(e =>
            {
                _logger.LogInformation("### RECEIVED APPLICATION MESSAGE ###");
                _logger.LogInformation($"+ Topic = {e.ApplicationMessage.Topic}");
                _logger.LogInformation($"+ Payload = {Encoding.UTF8.GetString(e.ApplicationMessage.Payload)}");
                _logger.LogInformation($"+ QoS = {e.ApplicationMessage.QualityOfServiceLevel}");
                _logger.LogInformation($"+ Retain = {e.ApplicationMessage.Retain}");
                _logger.LogInformation(" ");
            });
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting MQTT service...");
            System.Console.WriteLine("Starting MQTT service...");
            await _mqttClient.ConnectAsync(_options, cancellationToken);
            if (!_mqttClient.IsConnected)
            {
                _logger.LogWarning("Failed to connect with MQTT broker. Trying to reconnect...");
                System.Console.WriteLine("Failed to connect with MQTT broker. Trying to reconnect...");
                await _mqttClient.ReconnectAsync();
            }
            _logger.LogInformation("Finished starting MQTT service.");
            System.Console.WriteLine("Finished starting MQTT service.");
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping MQTT service...");
            System.Console.WriteLine("Stopping MQTT service...");
            if (cancellationToken.IsCancellationRequested)
            {
                var disconnectOption = new MqttClientDisconnectOptions
                {
                    ReasonCode = MqttClientDisconnectReason.NormalDisconnection,
                    ReasonString = "NormalDiconnection"
                };
                await _mqttClient.DisconnectAsync(disconnectOption, cancellationToken);
            }
            await _mqttClient.DisconnectAsync();
            _logger.LogInformation("Stopped MQTT service.");
            System.Console.WriteLine("Stopped MQTT service.");
        }
        public async Task HandleConnectedAsync(MqttClientConnectedEventArgs eventArgs)
        {
            await _mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(AppSettingsProvider.ClientSettings.Topic)
                .WithPayload("Online")
                .WithExactlyOnceQoS()
                .Build());
        }

        public async Task HandleDisconnectedAsync(MqttClientDisconnectedEventArgs eventArgs)
        {
        }
    }
}
