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
using AntDesign;

namespace WyzeSenseBlazor.DataServices
{
    public class MqttClientService : IMqttClientService
    {
        private readonly IWyzeSenseService _wyzeSenseService;
        private readonly IDataStoreService _dataStore;
        private readonly IMqttClient _mqttClient;
        private readonly IMqttClientOptions _options;
        private readonly ILogger<MqttClientService> _logger;

        public MqttClientService(IMqttClientOptions options, IWyzeSenseService wyzeSenseService, IDataStoreService dataStore)
        {
            _options = options;
            _dataStore = dataStore;
            _wyzeSenseService = wyzeSenseService;
            _wyzeSenseService.OnEvent += WyzeSenseService_OnEvent;
            _mqttClient = new MqttFactory().CreateMqttClient();
            _logger = new LoggerFactory().CreateLogger<MqttClientService>();
            ConfigureMqttClient();
        }
        private void WyzeSenseService_OnEvent(object sender, WyzeSenseEvent e)
        {
            _logger.LogInformation($"[Dongle][{e.EventType}] {e}");
            if (e.EventType == WyzeEventType.Status)

            {
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
                    string commandTopic = "";

                    switch (modeName)
                    {
                        case "Disarmed":
                            commandTopic = "DISARM";
                            break;
                        case "Home":
                            commandTopic = "ARM_HOME";
                            break;
                        case "Away":
                            commandTopic = "ARM_AWAY";
                            break;
                        case "Night":
                            commandTopic = "ARM_NIGHT";
                            break;
                        case "Vacation":
                            commandTopic = "ARM_VACATION";
                            break;
                        case "Bypass":
                            commandTopic = "ARM_CUSTOM_BYPASS";
                            break;
                        default:
                            _logger.LogWarning($"Unknown ModeName: {modeName}");
                            return;
                    }

                    payload.command_topic = commandTopic;
                }
                else
                {
                    _logger.LogWarning("ModeName key not present in event data");
                }
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic: $"{_options.TopicAliasMaximum}/{e.Sensor.MAC}")
                    .WithPayload(System.Text.Json.JsonSerializer.Serialize(payload))
                    .WithExactlyOnceQoS()
                    .WithRetainFlag()
                .Build();
     
                _mqttClient.PublishAsync(message, CancellationToken.None);
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
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithExactlyOnceQoS()
                .Build();
            await _mqttClient.PublishAsync(message);
        }

        private void ConfigureMqttClient()
        {
            _mqttClient.ConnectedHandler = this;
            _mqttClient.DisconnectedHandler = this;
        }
        public async Task HandleConnectedAsync(MqttClientConnectedEventArgs eventArgs)
        {
            await _mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(AppSettingsProvider.ClientSettings.Topic)
                .WithPayload("Online")
                .WithExactlyOnceQoS()
                .Build());
        }

        public Task HandleDisconnectedAsync(MqttClientDisconnectedEventArgs eventArgs)
        {
            return Task.CompletedTask;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _mqttClient.ConnectAsync(_options);
            if (!_mqttClient.IsConnected)
            {
                await _mqttClient.ReconnectAsync();
            }
            System.Console.WriteLine("Finishing starting MQTT service");
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
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
        }
    }
}
