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
using Microsoft.Extensions.Logging;
using System;
using System.Reflection.PortableExecutable;

namespace WyzeSenseBlazor.DataServices
{
    public class MqttClientService : IMqttClientService
    {
        private readonly IWyzeSenseService _wyzeSenseService;
        private readonly IDataStoreService _dataStore;
        private readonly IMqttClient _mqttClient;
        private readonly IMqttClientOptions _options;
        private readonly ILogger<MqttClientService> _logger;

        public MqttClientService(IMqttClientOptions options, IWyzeSenseService wyzeSenseService, IDataStoreService dataStore, ILogger<MqttClientService> logger)
        {
            _options = options;
            _dataStore = dataStore;
            _wyzeSenseService = wyzeSenseService;
            _wyzeSenseService.OnEvent += _wyzeSenseService_OnEvent;
            _mqttClient = new MqttFactory().CreateMqttClient();
            _logger = logger;
            ConfigureMqttClient();
        }

        private string currentPin = null;
        private string currentModeName = null;

        private void _wyzeSenseService_OnEvent(object sender, WyzeSenseCore.WyzeSenseEvent e)
        {
            e.Data.Add("timestamp", e.ServerTime.ToString());

            // Check if the Pin or ModeName is in the payload
            if (e.Data.TryGetValue("Pin", out var pinObj) && pinObj is string pin)
            {
                currentPin = pin;
                e.Data.Remove("Pin");
            }
            if (e.Data.TryGetValue("ModeName", out var modeNameObj) && modeNameObj is string modeName)
            {
                e.Data.Remove("ModeName");
                string command_topic;
                switch (modeName)
                {
                    case "Disarmed":
                        command_topic = "DISARM";
                        break;
                    case "Home":
                        command_topic = "ARM_HOME";
                        break;
                    case "Away":
                        command_topic = "ARM_AWAY";
                        break;
                    case "Night":
                        command_topic = "ARM_NIGHT";
                        break;
                    case "Vacation":
                        command_topic = "ARM_VACATION";
                        break;
                    case "Bypass":
                        command_topic = "ARM_CUSTOM_BYPASS";
                        break;
                    default:
                        command_topic = modeName;
                        break;
                }
                currentModeName = command_topic;
            }

            // If both the Pin and ModeName have been received, publish the message
            if (currentPin != null && currentModeName != null)
            {
                // Create a new payload with both the Pin and ModeName
                var payload = new Dictionary<string, object>(e.Data)
                {
                    ["Pin"] = currentPin,
                    ["ModeName"] = currentModeName
                };

                string topic = AppSettingsProvider.ClientSettings.Topic;

                // Publish the message
                PublishMessageAsync(topic, JsonSerializer.Serialize(payload));

                // Clear the current Pin and ModeName
                currentPin = null;
                currentModeName = null;
            }
            else
            {
                string topic = AppSettingsProvider.ClientSettings.Topic;
                if (_dataStore.DataStore.Sensors.TryGetValue(e.Sensor.MAC, out var sensor))
                {
                    List<Topics> toRemove = new();
                    if (sensor.Topics?.Count() > 0)
                    {
                        foreach (var topicTemplate in sensor.Topics)
                        {
                            if (_dataStore.DataStore.Templates.TryGetValue(topicTemplate.TemplateName, out var template))
                            {
                                Dictionary<string, object> payloadData = new();
                                foreach (var payloadPack in template.PayloadPackages)
                                {
                                    payloadData.Clear();
                                    topic = string.Join('/', topic, sensor.Alias, topicTemplate.RootTopic, payloadPack.Topic);
                                    topic = System.Text.RegularExpressions.Regex.Replace(topic, @"/+", @"/");
                                    topic = topic.TrimEnd('/');
                                    foreach (var pair in payloadPack.Payload)
                                    {
                                        if (e.Data.TryGetValue(pair.Value, out var value))
                                            payloadData.Add(pair.Key, value);
                                    }
                                    if (payloadData.Count() > 0)
                                    {
                                        payloadData.Add("timestamp", e.ServerTime.ToString());
                                        PublishMessageAsync(topic, JsonSerializer.Serialize(payloadData));
                                    }
                                }
                            }
                            else
                            {
                                toRemove.Add(topicTemplate);
                            }
                        }
                        if (toRemove.Count() > 0)
                            toRemove.ForEach(p => sensor.Topics.Remove(p));
                    }
                    else if (sensor.Alias.Length > 0)
                    {
                        PublishMessageAsync(string.Join('/', topic, sensor.Alias), JsonSerializer.Serialize(e.Data));
                    }
                }
                else
                {
                    PublishMessageAsync(string.Join('/', topic, e.Sensor.MAC), JsonSerializer.Serialize(e.Data));
                }
            }
        }

        private async Task PublishMessageAsync(string topic, string payload)
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithExactlyOnceQoS()
                .Build();
            await _mqttClient.PublishAsync(message);
            _logger.LogInformation($"Published message to topic {topic}");
        }

        private void ConfigureMqttClient()
        {
            _mqttClient.ConnectedHandler = this;
            _mqttClient.DisconnectedHandler = this;
        }

        async Task IMqttClientConnectedHandler.HandleConnectedAsync(MqttClientConnectedEventArgs eventArgs)
        {
            await _mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(AppSettingsProvider.ClientSettings.Topic)
                .WithPayload("Online")
                .WithExactlyOnceQoS()
                .Build());
            _logger.LogInformation("MQTT client connected");
        }

        Task IMqttClientDisconnectedHandler.HandleDisconnectedAsync(MqttClientDisconnectedEventArgs eventArgs)
        {
            _logger.LogInformation("MQTT client disconnected");
            return Task.CompletedTask;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _mqttClient.ConnectAsync(_options);
            if (!_mqttClient.IsConnected)
            {
                await _mqttClient.ReconnectAsync();
            }
            _logger.LogInformation("Finishing starting MQTT service");
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
            _logger.LogInformation("MQTT client stopped");
        }
    }
}

