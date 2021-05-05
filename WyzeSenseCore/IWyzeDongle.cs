﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WyzeSenseCore
{
    public interface IWyzeDongle
    {
        event EventHandler<WyzeSensor> OnAddSensor;
        event EventHandler<WyzeSensor> OnRemoveSensor;
        event EventHandler<WyzeSenseEvent> OnSensorAlarm;
        event EventHandler<WyzeDongleState> OnDongleStateChange;
        event EventHandler<WyzeKeyPadEvent> OnKeyPadEvent;
        event EventHandler<WyzeKeyPadPin> OnKeyPadPin;

        void Stop(); 
        bool OpenDevice(string devicePath);
        Task StartAsync(CancellationToken cancellationToken);
        void SetLedAsync(bool On);
        void StartScanAsync(int Timeout);
        Task StopScanAsync();
        Task DeleteSensorAsync(string MAC);
        void RequestRefreshSensorListAsync();
        Task<WyzeSensor[]> GetSensorAsync();
        WyzeDongleState GetDongleState();
    }
}
