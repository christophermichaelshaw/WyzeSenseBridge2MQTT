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
        public string state { get; set; }
        public string code_format { get; set; }
        public string changed_by { get; set; }
        public string code_arm_required { get; set; }
    }

}
