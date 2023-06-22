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

namespace WyzeSenseBlazor.DataServices
{
    public class MqttClientService : IMqttClientService
    {
        private readonly IWyzeSenseService _wyzeSenseService;
        private readonly IDataStoreService _dataStore;
        private readonly IMqttClient _mqttClient;
        private readonly IMqttClientOptions _options;

        public MqttClientService(IMqttClientOptions options, IWyzeSenseService wyzeSenseService, IDataStoreService dataStore)
        {
            _options = options;

            _dataStore = dataStore;

            _wyzeSenseService = wyzeSenseService;
            _wyzeSenseService.OnEvent += _wyzeSenseService_OnEvent;

            _mqttClient = new MqttFactory().CreateMqttClient();
            ConfigureMqttClient();
        }

        private void _wyzeSenseService_OnEvent(object sender, WyzeSenseCore.WyzeSenseEvent e)
        {
            Console.WriteLine("Keys in event data: " + string.Join(", ", e.Data.Keys));
            e.Data.Add("timestamp", e.ServerTime.ToString());
            bool hasPublished = false;
            string topic = AppSettingsProvider.ClientSettings.Topic;

            if (_dataStore.DataStore.Sensors.TryGetValue(e.Sensor.MAC, out var sensor))
            {
                if (sensor.Alias.Length > 0)
                {
                    var payloadData = new Dictionary<string, object>
            {
                {"state", e.Data["ModeName"]},
                {"code_format", "regex"},
                {"changed_by", null},
                {"code_arm_required", false}
            };

                    string newTopic = string.Join('/', topic, sensor.Alias);
                    PublishMessageAsync(newTopic, JsonSerializer.Serialize(payloadData));
                    hasPublished = true;
                }
            }

            if (!hasPublished)
            {
                var payloadData = new Dictionary<string, object>
        {
            {"state", e.Data["ModeName"]},
            {"code_format", "regex"},
            {"changed_by", null},
            {"code_arm_required", false}
        };

                string newTopic = string.Join('/', topic, e.Sensor.MAC);
                PublishMessageAsync(newTopic, JsonSerializer.Serialize(payloadData));
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
            //throw new System.NotImplementedException();
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
