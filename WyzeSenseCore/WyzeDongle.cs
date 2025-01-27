﻿using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers.Binary;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace WyzeSenseCore
{
    public sealed class WyzeDongle : IWyzeDongle, IDisposable
    {
        private const int CMD_TIMEOUT = 5000;

        private ByteBuffer dataBuffer;

        private ManualResetEvent waitHandle = new ManualResetEvent(false);
        private CancellationTokenSource dongleTokenSource;
        private CancellationTokenSource dongleScanTokenSource;

        private DataProcessor dataProcessor;
        
        private string donglePath;
        
        private FileStream dongleRead;
        private FileStream dongleWrite;

        private WyzeDongleState dongleState;
        private bool commandedLEDState;

        private Dictionary<string, WyzeSensor> sensors;
        private Dictionary<string, WyzeSensor> tempScanSensors;
        private int expectedSensorCount;
        private int actualSensorCount;

        private string lastAddedSensorMAC = "";
        private WyzeSensor lastAddedSensor;

        public event EventHandler<WyzeSensor> OnAddSensor;
        public event EventHandler<WyzeSensor> OnRemoveSensor;
        public event EventHandler<WyzeSenseEvent> OnSensorEvent;
        public event EventHandler<WyzeDongleState> OnDongleStateChange;

        private IWyzeSenseLogger _logger;
        

        public WyzeDongle( IWyzeSenseLogger logger)
        {
            _logger = logger;
            sensors = new();
            dongleState = new();
            dataBuffer = new(0x80, MaxCapacity: 1024);
        }
        public void Dispose()
        {
            dongleWrite?.Dispose();
            dongleRead?.Dispose();
        }
        public bool OpenDevice(string devicePath)
        {
            donglePath = devicePath;
            try
            {
                dongleRead = new FileStream(donglePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, true);
                dongleWrite = new FileStream(donglePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, true);
            }
            catch (Exception e)
            {
                _logger.LogError($"[Dongle][OpenDevice] {e.ToString()}");
                return false;
            }
            return true;
        }
        public void Stop()
        {
            dongleTokenSource.Cancel();
            dongleWrite.Close();
            dongleRead.Close();
        }
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting Dongle");

            if (dongleRead == null || dongleWrite == null) throw new Exception("Device Not open");


            
            dongleTokenSource =  CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            dataProcessor = new(dongleTokenSource.Token, dongleTokenSource.Token);
            dongleScanTokenSource = new();
            _logger.LogInformation($"[Dongle][StartAsync] Opening USB Device {donglePath}");

            _logger.LogDebug("[Dongle][StartAsync] USB Device opened");

            Task process = UsbProcessingAsync();
                        
            _logger.LogInformation("[Dongle][StartAsync] Requesting Device Type");
            await this.WriteCommandAsync(BasePacket.RequestDeviceType());

            //TODO: Research this feature to better understand what its doing.
            _logger.LogInformation("[Dongle][StartAsync] Requesting Enr");
            byte[] pack = new byte[16];
            for (int i = 0; i < pack.Length; i++)
                pack[i] = 30;
            await this.WriteCommandAsync(BasePacket.RequestEnr(pack));

            _logger.LogInformation("[Dongle][StartAsync] Requesting MAC");
            await this.WriteCommandAsync(BasePacket.RequestMAC());

            _logger.LogInformation("[Dongle][StartAsync] Requesting Version");
            await this.WriteCommandAsync(BasePacket.RequestVersion());

            _logger.LogInformation("[Dongle][StartAsync] Finishing Auth");
            await this.WriteCommandAsync(BasePacket.FinishAuth());

            //Request Sensor List
            await RequestRefreshSensorListAsync();

        }
        public async void SetLedAsync(bool On)
        {
            _logger.LogDebug($"[Dongle][SetLedAsync] Setting LED to: {On}");
            commandedLEDState = On;
            if (On)
                await this.WriteCommandAsync(BasePacket.SetLightOn());
            else
                await this.WriteCommandAsync(BasePacket.SetLightOff());
        }
        public async Task StartScanAsync(int Timeout = 60 * 1000)
        {
            _logger.LogDebug($"[Dongle][StartScanAsync] Starting Inclusion for {Timeout / 1000} seconds");
            await this.WriteCommandAsync(BasePacket.RequestEnableScan());
            try
            {
                await Task.Delay(Timeout, dongleScanTokenSource.Token);
                await StopScanAsync();
            }
            catch (TaskCanceledException tce) 
            {
                dongleScanTokenSource = new();
            }
        }
        public async Task StopScanAsync()
        {
            _logger.LogDebug($"[Dongle][StopScanAsync] Stopping Inclusion Scan");
            await this.WriteCommandAsync(BasePacket.RequestDisableScan());
            dongleScanTokenSource.Cancel();
        }
        public async Task RequestRefreshSensorListAsync()
        {
            _logger.LogDebug($"[Dongle][RequestRefreshSensorListAsync] Sending Sensor Count Request");
            await this.WriteCommandAsync(BasePacket.RequestSensorCount());
        }
        public async Task DeleteSensorAsync(string MAC)
        {
            _logger.LogDebug($"[Dongle][DeleteSensor] Issuing sensor delete: {MAC}");
            await WriteCommandAsync(BasePacket.DeleteSensor(MAC));
        }
        public async Task RequestCC1310Update()
        {
            _logger.LogDebug($"[Dongle][RequestCC1310Update] Asking to upgrade cc1310.");
            await WriteCommandAsync(BasePacket.UpdateCC1310());
        }
        private async Task UsbProcessingAsync()
        {
            _logger.LogInformation("[Dongle][UsbProcessingAsync] Beginning to process USB data");
            Memory<byte> dataReadBuffer = new byte[0x40];
            Memory<byte> headerBuffer = new byte[5];
            try
            {
                while (!dongleTokenSource.Token.IsCancellationRequested)
                {
                    while (dataBuffer.Peek(headerBuffer.Span))
                    {
                        _logger.LogTrace($"[Dongle][UsbProcessingAsync] dataBuffer contains enough bytes for header {dataBuffer.Size}");
                        ushort magic = BitConverter.ToUInt16(headerBuffer.Slice(0, 2).Span);
                        if (magic != 0xAA55)
                        {
                            _logger.LogError($"[Dongle][UsbProcessingAsync] Unexpected header bytes {magic:X4}");
                            dataBuffer.Burn(1);
                            continue;
                        }
                        if (headerBuffer.Span[4] == 0xff)//If this is the ack packet
                        {
                            dataBuffer.Burn(7);
                            //Acknowledge the command that we sent. Do we need to track which packet was last sent and verify?
                            this.waitHandle.Set();
                            _logger.LogTrace($"[Dongle][UsbProcessingAsync] Received dongle ack packet for {headerBuffer.Span[3]} burning 7 bytes, {dataBuffer.Size} remain");
                            continue;
                        }

                        byte dataLen = headerBuffer.Span[3];

                        if (dataBuffer.Size >= (dataLen + headerBuffer.Length - 1))
                        {
                            //A complete packet has been received
                            Memory<byte> dataPack = new Memory<byte>(new byte[dataLen + headerBuffer.Length - 1]);
                            dataBuffer.Dequeue(dataPack.Span);


                            if (dataPack.Span[2] == (byte)Command.CommandTypes.TYPE_ASYNC)
                            {
                                await this.WriteAsync(BasePacket.AckPacket(dataPack.Span[4]));
                                _logger.LogTrace($"[Dongle][UsbProcessingAsync] Acknowledging packet type 0x{dataPack.Span[4]:X2}");
                            }
                            dataReceived(dataPack.Span);
                        }
                        else
                        {
                            _logger.LogDebug($"[Dongle][UsbProcessingAsync] Incomplete packet received {dataBuffer.Size}/{dataLen + headerBuffer.Length - 1}");
                            break;
                        }
                    }

                    _logger.LogTrace("[Dongle][UsbProcessingAsync] Preparing to receive bytes");
                    int result = -1;
                    try
                    {
                        result = await dongleRead.ReadAsync(dataReadBuffer, dongleTokenSource.Token);
                    }
                    catch (TaskCanceledException tce)
                    {
                        _logger.LogInformation("[Dongle][UsbProcessingAsync] ReadAsync was cancelled");
                        continue;
                    }

                    if (result == 0)
                    {
                        _logger.LogError($"[Dongle][UsbProcessingAsync] End of file stream reached");
                        break;
                    }
                    else if (result >= 0)
                    {
                        _logger.LogTrace($"[Dongle][UsbProcessingAsync] {dataReadBuffer.Span[0]} Bytes Read Raw:  {DataToString(dataReadBuffer.Slice(1, dataReadBuffer.Span[0]).Span)}");
                        dataBuffer.Queue(dataReadBuffer.Slice(1, dataReadBuffer.Span[0]).Span);
                        _logger.LogTrace("[Dongle][UsbProcessingAsync] Finished processing received bytes");

                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError($"[Dongle][UsbProcessingAsync] {e.ToString()}");
            }


            _logger.LogInformation("[Dongle][UsbProcessingAsync] Exiting Processing loop");
        }

        private async Task WriteCommandAsync(BasePacket Pack)
        {
            waitHandle.Reset();
            await this.WriteAsync(Pack);
            if (!waitHandle.WaitOne(CMD_TIMEOUT))
                _logger.LogWarning("[Dongle][WriteCommandAsync] Command Timed out - did not receive response in alloted time");
            else
                _logger.LogInformation("[Dongle][WriteCommandAsync] Successfully Wrote command");
        }
        private async Task WriteAsync(BasePacket Pack)
        {
            byte[] data = Pack.Write();
            byte[] preppedData = new byte[data.Length + 1];
            preppedData[0] = (byte)data.Length;
            Array.Copy(data, 0, preppedData, 1, data.Length);
            try
            {
                await dongleWrite.WriteAsync(preppedData, 0, preppedData.Length);
                await dongleWrite.FlushAsync();
                _logger.LogTrace($"[Dongle][WriteAsync] Successfully wrote {preppedData.Length} bytes: {DataToString(preppedData)}");
            }
            catch (Exception e)
            {
                _logger.LogError($"[Dongle][WriteAsync] {e.ToString()}");
            }

        }

        private void refreshSensors()
        {
            List<WyzeSensor> toRem = new List<WyzeSensor>();
            //Look for existing sensors that aren't actually bound
            foreach (var sensor in sensors.Values)
            {
                if (!tempScanSensors.ContainsKey(sensor.MAC))
                    toRem.Add(sensor);
            }
            foreach (var sensor in toRem)
            {
                sensors.Remove(sensor.MAC);
                if (OnRemoveSensor != null)
                    this.dataProcessor.Queue(() => OnRemoveSensor.Invoke(this,sensor));
            }

            //Add sensors that don't already exist
            foreach (var sensor in tempScanSensors.Values)
            {
                if (!sensors.ContainsKey(sensor.MAC))
                {
                    sensors.Add(sensor.MAC, sensor);
                    if (OnAddSensor != null)
                        this.dataProcessor.Queue(() => OnAddSensor.Invoke(this,sensor));
                }
            }
        }

        private string DataToString(ReadOnlySpan<byte> data)
        {
            string byteString = "";
            for (int i = 0; i < data.Length; i++)
            {
                byteString += string.Format("{0:X2} ", data[i]);
            }
            return byteString.TrimEnd(' ');
        }

        private void dataReceived(ReadOnlySpan<byte> Data)
        {
            _logger.LogInformation($"[Dongle][dataReceived] {Data.Length} Bytes received: {DataToString(Data)}");

            switch ((Command.CommandIDs)Data[4])
            {
                case Command.CommandIDs.NotifySensorStart:
                    string mac = ASCIIEncoding.ASCII.GetString(Data.Slice(6, 8));
                    byte type = Data[14];
                    byte version = Data[15];
                    _logger.LogDebug($"[Dongle][dataReceived] New Sensor: {mac} Type: {(WyzeSensorType)type} Version: {version}");
                    lastAddedSensor = new WyzeSensor()
                    {
                        MAC = mac,
                        Type = (WyzeSensorType)type,
                        Version = version
                    };
                    lastAddedSensorMAC = mac;
                    WriteCommandAsync(BasePacket.SetSensorRandomDate(mac)).FireAndForget(_logger);
                    break;
                case Command.CommandIDs.SensorRandomDateResp:
                    string rmac = ASCIIEncoding.ASCII.GetString(Data.Slice(6, 8));
                    string rand = ASCIIEncoding.ASCII.GetString(Data.Slice(14, 16));
                    byte isSuccess = Data[30];
                    //byte 31 is version?

                    //TODO: Do I need to store this key, what is it's purpose. The data doesn't appear to be encrypted - maybe a future update?
                    if (isSuccess < 0 || isSuccess > 0xC)
                        _logger.LogError($"[Dongle][dataReceived] Random Date Seed Failed for : {rmac}");
                    else
                        _logger.LogDebug($"[Dongle][dataReceived] Random Date Resp: {rmac} Random: {rand}");

                    _logger.LogInformation($"[Dongle][dataReceived] Verifying Sensor: {lastAddedSensorMAC}");

                    WriteCommandAsync(BasePacket.VerifySensor(lastAddedSensorMAC)).FireAndForget(_logger);
                    break;
                case Command.CommandIDs.NotifyEventLog:
                    //timestamp is Slice(5,8);
                    eventReceived(Data.Slice(14));
                    break;
                case Command.CommandIDs.NotifySensorAlarm:
                    switch ((WyzeEventType)Data[0x0D])
                    {
                        case WyzeEventType.Alarm:
                            var alarmEvent = new WyzeSenseEvent()
                            {
                                Sensor = getSensor(ASCIIEncoding.ASCII.GetString(Data.Slice(0x0E, 8)), (WyzeSensorType)Data[0x16]),
                                ServerTime = DateTime.Now,
                                EventType = WyzeEventType.Alarm,
                                Data = new Dictionary<string, object>()
                                {
                                    { "Battery" , Data[0x18] },
                                    { "Signal" , Data[0x1E] }
                                }
                            };
                            _logger.LogTrace($"[Dongle][dataReceived] Sensor Type: {Data[0x16]}");
                            if (alarmEvent.Sensor.Type == WyzeSensorType.Motion || alarmEvent.Sensor.Type == WyzeSensorType.Motionv2)
                                alarmEvent.Data.Add("Motion", Data[0x1B]);
                            else if (alarmEvent.Sensor.Type == WyzeSensorType.Switch || alarmEvent.Sensor.Type == WyzeSensorType.SwitchV2)
                                alarmEvent.Data.Add("Contact", Data[0x1B]);
                            else
                                alarmEvent.Data.Add("State", Data[0x1B]);

                            _logger.LogInformation($"[Dongle][dataReceived] NotifySensorAlarm - Alarm: {alarmEvent.ToString()}");
                            if (OnSensorEvent != null)
                                    dataProcessor.Queue(() => OnSensorEvent.Invoke(this, alarmEvent));


                            break;
                        case WyzeEventType.Climate:
                            var tempEvent = new WyzeSenseEvent()
                            {
                                Sensor = getSensor(ASCIIEncoding.ASCII.GetString(Data.Slice(0x0E, 8)), WyzeSensorType.Climate),
                                ServerTime = DateTime.Now,
                                EventType = WyzeEventType.Status,
                                Data = new Dictionary<string, object>()
                                {
                                    { "Temperature", Data[0x1B] },
                                    { "Humidity", Data[0x1D] },
                                    { "Battery" , Data[0x18] },
                                    { "Signal" , Data[0x20] },
                                }
                            };
                            _logger.LogInformation($"[Dongle][dataReceived] NotifySensorAlarm - Temp: {tempEvent.ToString()}");
                            if (OnSensorEvent != null)
                            {
                                dataProcessor.Queue(() => OnSensorEvent.Invoke(this, tempEvent));
                            }
                            break;
                        default:
                            _logger.LogDebug($"[Dongle][dataReceived] Unhandled SensorAlarm {Data[0x0D]:X2}\r\n\t{DataToString(Data)}");
                            break;

                    }                         
                    break;
                case Command.CommandIDs.RequestSyncTime:
                    _logger.LogDebug("[Dongle][dataReceived] Dongle is requesting time");
                    break;
                case Command.CommandIDs.VerifySensorResp:
                    _logger.LogDebug("[Dongle][dataReceived] Verify Sensor Resp Ack");
                    if(lastAddedSensor == null)
                    {
                        _logger.LogDebug("[Dongle][dataReceived] Last Added  Sensor is null");
                        break;
                    }
                    dataProcessor.Queue(() => OnAddSensor.Invoke(this, lastAddedSensor));
                    break;
                case Command.CommandIDs.StartStopScanResp:
                case Command.CommandIDs.AuthResp:
                case Command.CommandIDs.SetLEDResp:
                case Command.CommandIDs.DeleteSensorResp:
                case Command.CommandIDs.GetDeviceTypeResp:
                case Command.CommandIDs.RequestDongleVersionResp:
                case Command.CommandIDs.RequestEnrResp:
                case Command.CommandIDs.RequestMACResp:
                case Command.CommandIDs.GetSensorCountResp:
                case Command.CommandIDs.GetSensorListResp:
                case Command.CommandIDs.UpdateCC1310Resp:
                    this.commandCallbackReceived(Data);
                    break;
                case Command.CommandIDs.KeyPadEvent:
                    keypadDataReceived(Data.Slice(5));
                    break;
                default:
                    _logger.LogDebug($"[Dongle][dataReceived] Data handler not assigned {(Command.CommandIDs)Data[4]} (Hex={string.Format("{0:X2}", Data[4])})\r\n\t{DataToString(Data)}");
                    break;

            }
        }
        private void eventReceived(ReadOnlySpan<byte> Data)
        {
            switch (Data[0])
            {
                case 0x14:
                    _logger.LogDebug($"[Dongle][eventReceived] Dongle Auth State: {Data[1]}");
                    this.dongleState.AuthState = Data[1];
                    if (this.OnDongleStateChange != null)
                        this.dataProcessor.Queue(() => this.OnDongleStateChange.Invoke(this, this.dongleState));
                    break;
                case 0x1C:
                    _logger.LogDebug($"[Dongle][eventReceived] Dongle Scan State Set: {Data[1]}");
                    this.dongleState.IsInclusive = Data[1] == 1 ? true : false;
                    if (this.OnDongleStateChange != null)
                        this.dataProcessor.Queue(() => this.OnDongleStateChange.Invoke(this, this.dongleState));
                    break;
                case 0x21:
                    //This packet is deformed, cuts the last byte of the MAC off.
                    string partialMac = ASCIIEncoding.ASCII.GetString(Data.Slice(1, 7));
                    _logger.LogDebug($"[Dongle][eventReceived] Set Random Date Confirmation Partial Mac:{partialMac}");
                    if (!lastAddedSensorMAC.StartsWith(partialMac))
                    {
                        _logger.LogError($"[Dongle][eventReceived] Random Date Confirmation is not for the correct sensor. Expected: {lastAddedSensorMAC}, Received Partial : {partialMac}");
                        return;
                    }
                    break;
                case 0x23:
                    _logger.LogDebug($"[Dongle][eventReceived] Verify Sensor Event {ASCIIEncoding.ASCII.GetString(Data.Slice(1, 8))}");
                    break;
                case 0x25:
                    _logger.LogDebug($"[Dongle][eventReceived] Dongle Deleted {ASCIIEncoding.ASCII.GetString(Data.Slice(1,8))}");
                    break;
                case 0xA2:
                    string mac = ASCIIEncoding.ASCII.GetString(Data.Slice(1, 8));
                    ushort eventID = BinaryPrimitives.ReadUInt16BigEndian(Data.Slice(11, 2));
                    _logger.LogDebug($"[Dongle][eventReceived] Event - {mac} SensorType:{(WyzeSensorType)Data[9]} State:{Data[10]} EventNumber:{eventID}");
                    break;
                case 0xA3:
                    string newmac = ASCIIEncoding.ASCII.GetString(Data.Slice(1, 8));
                    byte type = Data[9];
                    byte version = Data[10];
                    _logger.LogDebug($"[Dongle][dataReceived] New Sensor: {newmac} Type: {(WyzeSensorType)type} Version: {version}");
                    break;
                default:
                    _logger.LogError($"[Dongle][eventReceived] Unknown event ID: {string.Format("{0:X2}", Data[0])}\r\n\t{DataToString(Data)}");
                    break;
            }
        }
        private void commandCallbackReceived(ReadOnlySpan<byte> Data)
        {
            _logger.LogInformation("[Dongle][commandCallback] Receiving command response");

            Command.CommandIDs cmdID = (Command.CommandIDs)Data[4];
            switch (cmdID)
            {
                case Command.CommandIDs.UpdateCC1310Resp:
                    _logger.LogError($"[Dongle][commandCallback] READY TO START CC1310 UPGRADE");
                    break;
                case Command.CommandIDs.GetSensorCountResp:
                    expectedSensorCount = Data[5];
                    _logger.LogDebug($"[Dongle][commandCallback] There are {expectedSensorCount} sensor bound to this dongle");
                    this.tempScanSensors = new Dictionary<string, WyzeSensor>();
                    actualSensorCount = 0;
                    this.WriteCommandAsync(BasePacket.RequestSensorList((byte)expectedSensorCount)).FireAndForget(_logger);
                    break;
                case Command.CommandIDs.GetSensorListResp:
                    _logger.LogDebug($"[Dongle][commandCallback] GetSensorResp");
                    _logger.LogTrace($"[Dongle][commandCallback] {DataToString(Data)}");
                    WyzeSensor sensor = new WyzeSensor(Data);
                    tempScanSensors.TryAdd(sensor.MAC, sensor);
                    actualSensorCount++;
                    if (actualSensorCount == expectedSensorCount)
                        refreshSensors();
                    break;
                case Command.CommandIDs.DeleteSensorResp:
                    string mac = ASCIIEncoding.ASCII.GetString(Data.Slice(5, 8));
                    byte delStatue = Data[13];
                    _logger.LogDebug($"[Dongle][commandCallback] Delete Sensor Resp: {mac}");
                    if (sensors.TryGetValue(mac, out var delsensor))
                    {
                        if (OnRemoveSensor != null)
                            dataProcessor.Queue(() => OnRemoveSensor.Invoke(this, delsensor));
                        sensors.Remove(mac);
                    }
                    break;
                case Command.CommandIDs.SetLEDResp:
                    if (Data[5] == 0xff) this.dongleState.LEDState = commandedLEDState;
                    _logger.LogDebug($"[Dongle][commandCallback] Dongle LED Feedback: {dongleState.LEDState}");
                    if (OnDongleStateChange != null)
                        this.dataProcessor.Queue(() => OnDongleStateChange.Invoke(this,dongleState));
                    break;
                case Command.CommandIDs.GetDeviceTypeResp:
                    dongleState.DeviceType = Data[5];
                    _logger.LogDebug($"[Dongle][commandCallback] Dongle Device Type: {dongleState.DeviceType}");
                    waitHandle.Set();
                    break;
                case Command.CommandIDs.RequestDongleVersionResp:
                    dongleState.Version = ASCIIEncoding.ASCII.GetString(Data.Slice(5, (Data[3] - 3)));
                    _logger.LogDebug($"[Dongle][commandCallback] Dongle Version: {dongleState.Version}");
                    break;
                case Command.CommandIDs.RequestEnrResp:
                    dongleState.ENR = Data.Slice(5, (Data[3] - 3)).ToArray();
                    _logger.LogDebug($"[Dongle][commandCallback] Dongle ENR: {DataToString(dongleState.ENR)}");
                    waitHandle.Set();
                    break;
                case Command.CommandIDs.RequestMACResp:
                    dongleState.MAC = ASCIIEncoding.ASCII.GetString(Data.Slice(5, (Data[3] - 3)));
                    _logger.LogDebug($"[Dongle][commandCallback] Dongle MAC: {dongleState.MAC}");
                    waitHandle.Set();
                    break;
                case Command.CommandIDs.AuthResp:
                    _logger.LogDebug("[Dongle][commandCallback] Dongle Auth Resp");
                    break;
                case Command.CommandIDs.StartStopScanResp:
                    _logger.LogDebug("[Dongle][commandCallback] Start/Stop Scan Resp");
                    break;
                default:
                    _logger.LogDebug($"[Dongle][commandCallback] Unknown Command ID {cmdID}\r\n\t{ DataToString(Data)}");
                    break;
            }

        }
        private void keypadDataReceived(ReadOnlySpan<byte> Data)
        {
            byte eventType = Data[0]; //Used to send P1301 or P1302 to wyze server. No idea.
            string MAC = ASCIIEncoding.ASCII.GetString(Data.Slice(1, 8));
            var var1 = Data[0xA];
            var var2 = Data[0xe];
            var var3 = Data[var1 + 0xB]; //P1303 - battery?
            var var4 = Data[0xB];
            var var5 = Data[0xC];//P1304 - signal?
            var var6 = Data[0xD];

            switch (Data[0xE])
            {
                case 2:
                case 0xA:
                    //WyzeKeyPadEvent kpEvent = new(Data);
                    var keypadEvent = new WyzeSenseEvent()
                    {
                        Sensor = getSensor(MAC, WyzeSensorType.KeyPad),
                        ServerTime = DateTime.Now,
                        EventType = (Data[0x0E] == 0x02 ? WyzeEventType.UserAction : WyzeEventType.Alarm),
                        Data = new Dictionary<string, object>()
                        {

                            { "Battery", Data[0x0C] },
                            {  "Signal", Data[Data[0x0A] + 0x0B] }
                        }
                    };
                    if (Data[0x0E] == 0x02)
                    {
                        keypadEvent.Data.Add("Mode", Data[0x0F]+1);
                        keypadEvent.Data.Add("ModeName", ((WyzeKeyPadState)Data[0x0F]+1).ToString());
                    }
                    else
                        keypadEvent.Data.Add("Motion", Data[0x0F]);

                    _logger.LogInformation($"[Dongle][keypadDataReceived] KeypadEvent - {keypadEvent}");
                    if (OnSensorEvent != null)
                        dataProcessor.Queue(() => OnSensorEvent.Invoke(this, keypadEvent));
                    break;
                case 6:
                    //Received when you start entering keypad pin code.
                    _logger.LogWarning($"[Dongle][KeypadDataReceived] Keypad({MAC}) Request Profile status - is it requesting a response??");
                    break;
                case 8:
                    var pinEvent = new WyzeSenseEvent()
                    {
                        Sensor = getSensor(MAC, (WyzeSensorType)Data[0x0E]),
                        ServerTime = DateTime.Now,
                        EventType = WyzeEventType.UserAction,
                        Data = new()
                        {
                            { "Battery", Data[0x0C] },//Not confident
                            { "Signal", Data[Data[0xA] + 0xB] }
                        }
                    };

                    //Extract pin. 
                    ReadOnlySpan<byte> pinRaw = Data.Slice(0xF, Data[0xA] - 6);
                    string Pin = "";
                    foreach (byte b in pinRaw)
                        Pin += b.ToString();
                    pinEvent.Data.Add("Pin", Pin);

                    _logger.LogInformation($"[Dongle][KeypadDataReceived] Pin event received");
                    if(OnSensorEvent != null)
                        dataProcessor.Queue(() => OnSensorEvent.Invoke(this, pinEvent));
                    break;
                case 0xC:
                    _logger.LogInformation($"[Dongle][KeypadDataReceived] Keypad({MAC}) Some sort of alarm event?");
                    break;
                case 0x12:
                    var dongleWaterEvent = new WyzeSenseEvent()
                    {
                        Sensor = getSensor(MAC, WyzeSensorType.Water),
                        ServerTime = DateTime.Now,
                        EventType = WyzeEventType.Alarm, //Alarm. Not sure if water sensors check in periodically.
                        Data = new Dictionary<string, object>()
                        {
                            { "HasExtension", Data[0x11] },
                            { "ExtensionWater", Data[0x10] },
                            { "Water", Data[0x0F] },
                            { "Battery" , Data[0x0C] },//Not confident
                            { "Signal" , Data[0x14] }
                        }
                    };
                    _logger.LogInformation($"[Dongle][keypadDataReceived] WaterEvent -: {dongleWaterEvent}");
                    if (OnSensorEvent != null)
                        dataProcessor.Queue(() => OnSensorEvent.Invoke(this, dongleWaterEvent));

                    break;
                default: break;
            }
        }
        public Task<WyzeSensor[]> GetSensorAsync()
        {
            return Task.FromResult(sensors.Select(index => index.Value).ToArray());
        }

        public WyzeDongleState GetDongleState()
        {
            return this.dongleState;
        }
        private WyzeSensor getSensor(string MAC, WyzeSensorType Type)
        {
            if (sensors.TryGetValue(MAC, out var sensor))
            {
                sensor.Type = Type;
                return sensor;
            }
            return new WyzeSensor()
            {
                MAC = MAC,
                Type = Type,
                Version = 0
            }; 
        }

    }
}
