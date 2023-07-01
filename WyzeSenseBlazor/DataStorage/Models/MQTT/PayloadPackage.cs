using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
namespace WyzeSenseBlazor.DataStorage.Models
{
    public class PayloadPackage
    {
        internal string command_topic;
        internal string timestamp;
        internal string code;

        public string Topic { get; set; }
        public Dictionary<string, string> Payload { get; set; }
        public object Battery { get; internal set; }
        public object Signal { get; internal set; }
        public object Mode { get; internal set; }

        public PayloadPackage()
        {
            Payload = new Dictionary<string, string>();
        }

    }
}