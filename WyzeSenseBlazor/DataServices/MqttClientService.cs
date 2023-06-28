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

        private void _wyzeSenseService_OnEvent(object sender, WyzeSenseCore.WyzeSenseEvent e)
        {
            e.Data.Add("timestamp", e.ServerTime.ToString());

            bool hasPublished = false;

            // Convert ModeName to command_topic, and the key's values to match Home Assistant mqtt_alarm_control_panel expected values
            if (e.Data.TryGetValue("ModeName", out var modeNameObj) && modeNameObj is string modeName)
            {
                e.Data.Remove("ModeName");
                string commandTopic;
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
                        commandTopic = modeName; // If the modeName is not one of the above, use it as is.
                        break;
                }
                e.Data.Add("command_topic", commandTopic);
            }

            // Convert Pin to code
            if (e.Data.TryGetValue("Pin", out var pinObj) && pinObj is string pin)
            {
                e.Data.Remove("Pin");
                e.Data.Add("code", pin);
            }


            //Topic should always start with the root topic.
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

                            //Template does exist, need to publish a message for each package.
                            foreach (var payloadPack in template.PayloadPackages)
                            {
                                payloadData.Clear();

                                topic = string.Join('/', topic, sensor.Alias, topicTemplate.RootTopic, payloadPack.Topic);
                                //Replace double slash to accomodate blank root topic.
                                topic = System.Text.RegularExpressions.Regex.Replace(topic, @"/+", @"/");
                                //Remove trailing slash to accomdate blank payload topic.
                                topic = topic.TrimEnd('/');

                                foreach (var pair in payloadPack.Payload)
                                {
                                    if (e.Data.TryGetValue(pair.Value, out var value))
                                        payloadData.Add(pair.Key, value);
                                }
                                if (payloadData.Count() > 0)
                                {
                                    //If event data contained any of the payload packet data lets add time and publish.
                                    payloadData.Add("timestamp", e.ServerTime.ToString());
                                    PublishMessageAsync(topic, JsonSerializer.Serialize(payloadData));
                                    hasPublished = true;
                                }
                            }
                        }
                        else
                        {
                            //Template doesn't exist
                            toRemove.Add(topicTemplate);
                        }
                    }
                    //Remove the topic templates that didn't have a valid template associated.
                    if (toRemove.Count() > 0)
                        toRemove.ForEach(p => sensor.Topics.Remove(p));
                }
                else if (sensor.Alias.Length > 0)
                {
                    //Sensor with no topics, publish with alias. 
                    PublishMessageAsync(string.Join('/', topic, sensor.Alias), JsonSerializer.Serialize(e.Data));
                    hasPublished = true;
                }
            }
            if (!hasPublished)
            {
                //No database sensor, publish all data to MAC
                PublishMessageAsync(string.Join('/', topic, e.Sensor.MAC), JsonSerializer.Serialize(e.Data));
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

